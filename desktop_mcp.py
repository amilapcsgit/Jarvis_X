"""Local Windows-MCP adapter limited to Jarvis's selected HWND."""
import ctypes as c
import io
import json
import os
import time
from ctypes import wintypes as w
from pathlib import Path

from fastmcp import FastMCP
from fastmcp.utilities.types import Image
from PIL import ImageGrab
import windows_mcp.uia as uia

user = c.WinDLL("user32", use_last_error=True)
kernel = c.WinDLL("kernel32", use_last_error=True)
user.SetProcessDpiAwarenessContext(c.c_void_p(-4))
user.GetForegroundWindow.restype = w.HWND
user.GetWindowThreadProcessId.argtypes = [w.HWND, c.POINTER(w.DWORD)]
user.GetWindowRect.argtypes = [w.HWND, c.POINTER(w.RECT)]
user.WindowFromPoint.argtypes = [w.POINT]
user.WindowFromPoint.restype = w.HWND
user.GetAncestor.argtypes = [w.HWND, w.UINT]
user.GetAncestor.restype = w.HWND
kernel.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
kernel.OpenProcess.restype = w.HANDLE
kernel.GetProcessTimes.argtypes = [w.HANDLE] + [c.POINTER(w.FILETIME)] * 4
kernel.CloseHandle.argtypes = [w.HANDLE]

scope_path = Path(os.environ["JARVIS_RUN_SCOPE"])
mcp = FastMCP("Jarvis Windows-MCP")
observation = None


def target(previous=None):
    try:
        with scope_path.open(encoding="utf-8") as scope_file:
            scope = json.load(scope_file)
    except (OSError, ValueError) as error:
        raise RuntimeError("Desktop control released. Start a new run.") from error
    selected = scope.get("Target")
    if scope.get("Agent") != "desktop" or not selected:
        raise RuntimeError("Choose a desktop window before starting.")
    hwnd = selected["Handle"]
    pid = w.DWORD()
    user.GetWindowThreadProcessId(hwnd, c.byref(pid))
    handle = kernel.OpenProcess(0x1000, False, pid.value) if pid.value == selected["Pid"] else None
    if not handle:
        raise RuntimeError("Selected window closed. Refresh the window list.")
    try:
        start, exit_time, system, cpu = (w.FILETIME() for _ in range(4))
        if not kernel.GetProcessTimes(handle, c.byref(start), c.byref(exit_time), c.byref(system), c.byref(cpu)):
            raise RuntimeError("Cannot verify selected process identity.")
        if (start.dwHighDateTime << 32 | start.dwLowDateTime) != selected["Created"]:
            raise RuntimeError("Selected process changed. Select a window again.")
    finally:
        kernel.CloseHandle(handle)
    if user.GetForegroundWindow() != hwnd:
        raise RuntimeError("Desktop paused. Focus the selected window and take a fresh snapshot. Windows may be locked.")
    rect = w.RECT()
    if not user.GetWindowRect(hwnd, c.byref(rect)):
        raise RuntimeError("Cannot determine the selected window bounds.")
    stamp = scope["Id"], scope["FocusVersion"], hwnd, (rect.left, rect.top, rect.right, rect.bottom)
    if previous is not None and stamp != previous:
        raise RuntimeError("Desktop focus or layout changed. Take a fresh snapshot before input.")
    return stamp


@mcp.tool(name="Snapshot")
def snapshot() -> dict:
    """Read the selected window's accessibility tree. Returns window-relative pixel coordinates."""
    global observation
    stamp = target()
    _, _, hwnd, (left, top, right, bottom) = stamp
    controls = []
    with uia.UIAutomationInitializerInThread():
        root = uia.ControlFromHandle(hwnd)
        for control, depth in uia.WalkControl(root, includeTop=True, maxDepth=7):
            if len(controls) >= 48:
                break
            try:
                rect = control.BoundingRectangle
                x = (rect.left + rect.right) // 2
                y = (rect.top + rect.bottom) // 2
                if not (left <= x < right and top <= y < bottom) or control.IsOffscreen:
                    continue
                row = {"role": control.ControlTypeName, "name": (control.Name or "")[:180],
                       "x": x - left, "y": y - top}
                if not control.IsPassword and row["role"] in {"EditControl", "DocumentControl"}:
                    try:
                        row["text"] = control.GetValuePattern().Value[:500]
                    except Exception:
                        try:
                            row["text"] = control.GetTextPattern().DocumentRange.GetText(500)
                        except Exception:
                            pass
                controls.append(row)
            except (OSError, AttributeError, RuntimeError):
                continue
    target(stamp)
    observation = stamp
    return {"width": right - left, "height": bottom - top, "controls": controls}


@mcp.tool(name="Screenshot")
def screenshot() -> Image:
    """Capture the selected window only. Use Snapshot for window-relative click coordinates."""
    global observation
    stamp = target()
    image = ImageGrab.grab(window=stamp[2])
    target(stamp)
    image.thumbnail((960, 720))
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    observation = stamp
    return Image(data=buffer.getvalue(), format="png")


def require_observation():
    if observation is None:
        raise RuntimeError("Take a fresh Snapshot or Screenshot before input.")
    return target(observation)


@mcp.tool(name="Click")
def click(x: int, y: int) -> str:
    """Click one window-relative point from Snapshot. Observe again before the next input."""
    global observation
    stamp = require_observation()
    _, _, hwnd, (left, top, right, bottom) = stamp
    if not (0 <= x < right - left and 0 <= y < bottom - top):
        raise ValueError("Click is outside the selected window.")
    x, y = left + x, top + y
    if user.GetAncestor(user.WindowFromPoint(w.POINT(x, y)), 2) != hwnd:
        raise RuntimeError("The point is covered by another window.")
    user.SetCursorPos(x, y)
    target(stamp)
    if user.GetAncestor(user.WindowFromPoint(w.POINT(x, y)), 2) != hwnd:
        raise RuntimeError("The point is covered by another window.")
    events = (Input * 2)(Input(0, Payload(mouse=Mouse(0, 0, 0, 2))), Input(0, Payload(mouse=Mouse(0, 0, 0, 4))))
    send(events)
    observation = None
    return "Clicked selected window. Observe again before more input."


class Keyboard(c.Structure):
    _fields_ = [("key", w.WORD), ("scan", w.WORD), ("flags", w.DWORD),
                ("time", w.DWORD), ("extra", c.c_size_t)]


class Mouse(c.Structure):
    _fields_ = [("x", w.LONG), ("y", w.LONG), ("data", w.DWORD),
                ("flags", w.DWORD), ("time", w.DWORD), ("extra", c.c_size_t)]


class Payload(c.Union):
    _fields_ = [("mouse", Mouse), ("keyboard", Keyboard)]


class Input(c.Structure):
    _fields_ = [("kind", w.DWORD), ("payload", Payload)]


user.SendInput.argtypes = [w.UINT, c.POINTER(Input), c.c_int]
user.SendInput.restype = w.UINT


def send(events):
    if user.SendInput(len(events), events, c.sizeof(Input)) != len(events):
        raise RuntimeError("Windows blocked input. Check target focus and privilege level.")


@mcp.tool(name="Type")
def type_text(text: str) -> str:
    """Type literal text into the focused field of the selected window. Requires a fresh observation."""
    if not 0 < len(text) <= 2000 or any(ord(char) < 32 and char not in "\r\n" for char in text):
        raise ValueError("Provide 1–2000 characters of plain text.")
    global observation
    stamp = require_observation()
    observation = None
    for char in text.replace("\r\n", "\n"):
        target(stamp)
        if char in "\r\n":
            keys = [Keyboard(13, 0, 0), Keyboard(13, 0, 2)]
        else:
            data = char.encode("utf-16-le")
            units = [int.from_bytes(data[i:i + 2], "little") for i in range(0, len(data), 2)]
            keys = [Keyboard(0, unit, flag) for unit in units for flag in (4, 6)]
        send((Input * len(keys))(*(Input(1, Payload(keyboard=key)) for key in keys)))
        # Let the receiving window consume VK_PACKET before the next UTF-16 payload.
        time.sleep(0.015)
    return "Typed into the selected window."


@mcp.tool(name="Shortcut")
def shortcut(keys: str) -> str:
    """Use Ctrl+S, Ctrl+A, Ctrl+Z, Escape, Tab or Enter in the selected window."""
    global observation
    bindings = {"ctrl+s": (17, 83), "ctrl+a": (17, 65), "ctrl+z": (17, 90),
                "escape": (27,), "tab": (9,), "enter": (13,)}
    chord = bindings.get(keys.lower().strip())
    if not chord:
        raise ValueError("Unsupported shortcut.")
    stamp = require_observation()
    events = [Keyboard(key, 0, 0) for key in chord] + [Keyboard(key, 0, 2) for key in reversed(chord)]
    target(stamp)
    observation = None
    send((Input * len(events))(*(Input(1, Payload(keyboard=key)) for key in events)))
    return "Shortcut sent. Observe the outcome before more input."


if __name__ == "__main__":
    mcp.run()
