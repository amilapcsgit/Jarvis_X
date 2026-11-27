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
user.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, c.c_int]
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
image_size = None
budget_run = None
actions = 0


def read_scope():
    try:
        scope = json.loads(scope_path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        raise RuntimeError("Desktop control released. Start a new run.") from error
    if scope.get("Agent") != "desktop" or scope.get("Paused"):
        raise RuntimeError("Desktop control is paused or unavailable.")
    return scope


def spend(scope):
    global budget_run, actions
    if budget_run != scope["Id"]:
        budget_run, actions = scope["Id"], 0
    if actions >= 24:
        raise RuntimeError("Desktop action limit reached. Review the results before continuing.")
    actions += 1


def window_identity(hwnd):
    pid = w.DWORD()
    user.GetWindowThreadProcessId(hwnd, c.byref(pid))
    handle = kernel.OpenProcess(0x1000, False, pid.value)
    if not handle:
        raise RuntimeError("Cannot verify the window process.")
    try:
        start, exit_time, system, cpu = (w.FILETIME() for _ in range(4))
        if not kernel.GetProcessTimes(handle, c.byref(start), c.byref(exit_time), c.byref(system), c.byref(cpu)):
            raise RuntimeError("Cannot verify the window process.")
        return {"Handle": hwnd, "Pid": pid.value, "Created": (start.dwHighDateTime << 32) | start.dwLowDateTime}
    finally:
        kernel.CloseHandle(handle)


def target(previous=None):
    scope = read_scope()
    selected = window_identity(user.GetForegroundWindow()) if scope.get("FullDesktop") else scope.get("Target")
    if not selected or selected["Pid"] == scope.get("HostPid"):
        raise RuntimeError("Focus an ordinary app in the desktop session.")
    hwnd = selected["Handle"]
    if window_identity(hwnd) != selected:
        raise RuntimeError("The target process changed. Observe again.")
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
    global observation, image_size
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
    observation, image_size = stamp, None
    title = c.create_unicode_buffer(256)
    user.GetWindowTextW(hwnd, title, len(title))
    return {"window": title.value, "width": right - left, "height": bottom - top, "controls": controls}


@mcp.tool(name="Screenshot")
def screenshot() -> Image:
    """Capture the selected window only. Use Snapshot for window-relative click coordinates."""
    global observation, image_size
    stamp = target()
    image = ImageGrab.grab(window=stamp[2])
    target(stamp)
    image.thumbnail((960, 720))
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    observation, image_size = stamp, image.size
    return Image(data=buffer.getvalue(), format="png")


def require_observation():
    if observation is None:
        raise RuntimeError("Take a fresh Snapshot or Screenshot before input.")
    stamp = target(observation)
    spend(read_scope())
    return stamp


def point(stamp, x, y):
    _, _, hwnd, (left, top, right, bottom) = stamp
    width, height = image_size or (right - left, bottom - top)
    if not (0 <= x < width and 0 <= y < height):
        raise ValueError("Point is outside the latest observation.")
    x = left + x * (right - left) // width
    y = top + y * (bottom - top) // height
    if user.GetAncestor(user.WindowFromPoint(w.POINT(x, y)), 2) != hwnd:
        raise RuntimeError("The point is covered by another window.")
    return x, y


@mcp.tool(name="Click")
def click(x: int, y: int) -> str:
    """Click once in the latest Snapshot or Screenshot pixel space."""
    global observation
    stamp = require_observation()
    hwnd = stamp[2]
    x, y = point(stamp, x, y)
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
                "ctrl+c": (17, 67), "ctrl+v": (17, 86), "escape": (27,), "tab": (9,), "enter": (13,)}
    chord = bindings.get(keys.lower().strip())
    if not chord:
        raise ValueError("Unsupported shortcut.")
    stamp = require_observation()
    events = [Keyboard(key, 0, 0) for key in chord] + [Keyboard(key, 0, 2) for key in reversed(chord)]
    target(stamp)
    observation = None
    send((Input * len(events))(*(Input(1, Payload(keyboard=key)) for key in events)))
    return "Shortcut sent. Observe the outcome before more input."


@mcp.tool(name="Windows")
def windows() -> list[dict]:
    """List ordinary visible windows for Full desktop mode. Pass the returned identity to Focus."""
    scope = read_scope()
    if not scope.get("FullDesktop"):
        raise RuntimeError("Window discovery requires Full desktop scope.")
    found = []
    callback_type = c.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    user.IsWindowVisible.argtypes = [w.HWND]
    user.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, c.c_int]
    user.EnumWindows.argtypes = [callback_type, w.LPARAM]

    @callback_type
    def collect(hwnd, _):
        title = c.create_unicode_buffer(256)
        if len(found) >= 32:
            return False
        if user.IsWindowVisible(hwnd) and user.GetWindowTextW(hwnd, title, len(title)):
            try:
                identity = window_identity(hwnd)
                if identity["Pid"] != scope.get("HostPid"):
                    found.append({"title": title.value, **identity})
            except RuntimeError:
                pass
        return True

    user.EnumWindows(collect, 0)
    return found


@mcp.tool(name="Focus")
def focus(handle: int, pid: int, created: int) -> str:
    """Switch to a visible window returned by Windows, then observe again."""
    global observation
    scope = read_scope()
    identity = {"Handle": handle, "Pid": pid, "Created": created}
    if not scope.get("FullDesktop") or not any(all(row[k] == v for k, v in identity.items()) for row in windows()):
        raise RuntimeError("The window is outside the approved desktop scope.")
    spend(scope)
    observation = None
    user.SetForegroundWindow.argtypes = [w.HWND]
    if user.GetForegroundWindow() != handle and not user.SetForegroundWindow(handle):
        raise RuntimeError("Windows declined to activate this app. Focus it manually.")
    return "Window focus requested. Observe before input."


@mcp.tool(name="Scroll")
def scroll(x: int, y: int, steps: int) -> str:
    """Scroll at a point in the latest capture. Positive steps scroll up."""
    global observation
    if not -8 <= steps <= 8 or steps == 0:
        raise ValueError("Use 1–8 signed wheel steps.")
    stamp = require_observation()
    user.SetCursorPos(*point(stamp, x, y))
    target(stamp)
    observation = None
    send((Input * 1)(Input(0, Payload(mouse=Mouse(0, 0, steps * 120, 0x800)))))
    return "Scrolled. Observe the result."


@mcp.tool(name="Drag")
def drag(x: int, y: int, end_x: int, end_y: int) -> str:
    """Drag between two unobstructed points in the observed window."""
    global observation
    stamp = require_observation()
    start = point(stamp, x, y)
    end = point(stamp, end_x, end_y)
    user.SetCursorPos(*start)
    target(stamp)
    observation = None
    left, top, width, height = (user.GetSystemMetrics(index) for index in (76, 77, 78, 79))
    if width < 2 or height < 2:
        raise RuntimeError("Cannot map virtual desktop coordinates.")
    absolute_x = round((end[0] - left) * 65535 / (width - 1))
    absolute_y = round((end[1] - top) * 65535 / (height - 1))
    events = (Input * 3)(
        Input(0, Payload(mouse=Mouse(0, 0, 0, 2))),
        Input(0, Payload(mouse=Mouse(absolute_x, absolute_y, 0, 0xC001))),
        Input(0, Payload(mouse=Mouse(0, 0, 0, 4))))
    send(events)
    return "Dragged. Observe the result."


if __name__ == "__main__":
    import sys
    if len(sys.argv) == 3 and sys.argv[1] == "--plan":
        from desktop_planner import run
        run(Path(sys.argv[2]))
    else:
        mcp.run()
