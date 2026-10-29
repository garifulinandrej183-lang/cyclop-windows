using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Cyclop.Windows.Shell;

public record DisplayInfo(string Device, int Left, int Top, int Right, int Bottom,
    int WorkLeft, int WorkTop, int WorkRight, int WorkBottom, uint Dpi)
{
    public override string ToString() => $"{Device}  {(int)Math.Round(Dpi / 96d * 100)}%";
}

public static class NativeShell
{
    public const int WmHotkey = 0x0312;
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint hdc, ref Rect rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out Rect value, int size);

    public static List<DisplayInfo> Displays()
    {
        var result = new List<DisplayInfo>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Rect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(monitor, ref info))
            {
                _ = GetDpiForMonitor(monitor, 0, out var dpi, out _);
                result.Add(new(info.Device, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom,
                    info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom, dpi == 0 ? 96 : dpi));
            }
            return true;
        }, 0);
        return result;
    }
    public static void ConfigureToolWindow(nint hwnd)
    {
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, (nint)((style | 0x80 | 0x08000000) & ~0x40000L)); // TOOLWINDOW, passive initially; explicit actions enable activation
    }
    public static void SetActivationAllowed(nint hwnd, bool allowed)
    {
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, (nint)(allowed ? style & ~0x08000000L : style | 0x08000000));
    }
    public static unsafe nint ForegroundWindow() => (nint)global::Windows.Win32.PInvoke.GetForegroundWindow().Value;
    public static unsafe bool IsForegroundFullscreen(nint ownWindow, DisplayInfo display)
    {
        var hwnd = global::Windows.Win32.PInvoke.GetForegroundWindow();
        if (hwnd.IsNull || (nint)hwnd.Value == ownWindow || (nint)hwnd.Value == GetShellWindow() || (nint)hwnd.Value == GetDesktopWindow()) return false;
        var className = new StringBuilder(128); GetClassName((nint)hwnd.Value, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!global::Windows.Win32.PInvoke.IsWindowVisible(hwnd)) return false;
        Rect rect;
        if (DwmGetWindowAttribute((nint)hwnd.Value, 9, out rect, Marshal.SizeOf<Rect>()) != 0)
        {
            if (!global::Windows.Win32.PInvoke.GetWindowRect(hwnd, out var native)) return false;
            rect = new Rect { Left = native.left, Top = native.top, Right = native.right, Bottom = native.bottom };
        }
        return rect.Left <= display.Left && rect.Top <= display.Top && rect.Right >= display.Right && rect.Bottom >= display.Bottom;
    }
    public static void SetStartup(bool enabled, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("CyclopWindows", $"\"{executable}\" --startup");
        else key.DeleteValue("CyclopWindows", false);
    }
}

/// <summary>Event-driven foreground/window changes; no process/media polling.</summary>
public sealed class ForegroundWatcher : IDisposable
{
    private delegate void WinEvent(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    private readonly WinEvent _callback;
    private readonly nint _foregroundHook, _locationHook;
    public event Action? Changed;
    public unsafe ForegroundWatcher()
    {
        _callback = (_, evt, hwnd, obj, child, thread, time) =>
        {
            if (evt == 3 || obj == 0 && hwnd == (nint)global::Windows.Win32.PInvoke.GetForegroundWindow().Value) Changed?.Invoke();
        };
        _foregroundHook = SetWinEventHook(3, 3, 0, _callback, 0, 0, 2);
        _locationHook = SetWinEventHook(0x800B, 0x800B, 0, _callback, 0, 0, 2);
    }
    public void Dispose() { if (_foregroundHook != 0) UnhookWinEvent(_foregroundHook); if (_locationHook != 0) UnhookWinEvent(_locationHook); }
}
