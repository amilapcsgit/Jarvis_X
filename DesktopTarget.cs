using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Jarvis;

sealed record DesktopTarget(long Handle, int Pid, long Created, string Process, string Title, string? Icon)
{
    public string Id => $"{Handle}:{Pid}:{Created}";
    [System.Text.Json.Serialization.JsonIgnore]
    public string CurrentTitle
    {
        get
        {
            var text = new StringBuilder(512);
            return IsAlive && Native.GetWindowText((nint)Handle, text, text.Capacity) != 0 ? text.ToString() : Title;
        }
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => $"{CurrentTitle} · {Process} (PID {Pid}, HWND 0x{Handle:X})";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsForeground => Native.GetForegroundWindow().ToInt64() == Handle && IsAlive;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAlive
    {
        get
        {
            var hwnd = (nint)Handle;
            if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return false;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != Pid) return false;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(Pid);
                return process.StartTime.ToFileTimeUtc() == Created;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }
    }

    public bool Focus()
    {
        if (!IsAlive) return false;
        var hwnd = (nint)Handle;
        if (Native.IsIconic(hwnd)) Native.ShowWindowAsync(hwnd, 9);
        Native.SetForegroundWindow(hwnd);
        return true;
    }

    public static IReadOnlyList<DesktopTarget> Find()
    {
        var windows = new List<DesktopTarget>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId || pid > int.MaxValue) return true;
            var title = new StringBuilder(512);
            if (Native.GetWindowText(hwnd, title, title.Capacity) == 0) return true;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                windows.Add(new(hwnd.ToInt64(), (int)pid, process.StartTime.ToFileTimeUtc(),
                    process.ProcessName, title.ToString(), ReadIcon(hwnd)));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, 0);
        return windows.OrderBy(t => t.Process, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Title).ToArray();
    }

    static string? ReadIcon(nint hwnd)
    {
        Native.SendMessageTimeout(hwnd, 0x007f, 2, 0, 2, 20, out var icon);
        if (icon == 0) icon = Native.GetClassLongPtr(hwnd, -34);
        if (icon == 0) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var bytes = new MemoryStream();
            encoder.Save(bytes);
            return "data:image/png;base64," + Convert.ToBase64String(bytes.ToArray());
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    internal static class Native
    {
        internal delegate bool WindowCallback(nint hwnd, nint data);
        internal delegate void WinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(WindowCallback callback, nint data);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(nint hwnd, int command);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] internal static extern nint GetClassLongPtr(nint hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
        [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEvent callback, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    }
}

sealed class RunScope(string file, string project, DesktopTarget? target, string agent)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Project { get; } = Path.GetFullPath(project);
    public DesktopTarget? Target { get; } = target;
    public string Agent { get; } = agent;
    public int FocusVersion { get; private set; }
    public string Destination => Target?.Label ?? Project;

    public void Publish()
    {
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            Id, Project, Agent, FocusVersion, Target = Target == null ? null : new { Target.Handle, Target.Pid, Target.Created }
        }));
        File.Move(temporary, file, true);
    }

    public void InvalidateObservation()
    {
        FocusVersion++;
        Publish();
    }

    public void Revoke()
    {
        File.Delete(file);
    }
}
