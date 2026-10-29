using System.Runtime.InteropServices;

namespace Cyclop.Windows.Shell;

/// <summary>Shell notification icon bound to the panel HWND. Does not add WinForms runtime.</summary>
public sealed class TrayIcon : IDisposable
{
    private const int Callback = 0x8001;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x00000010;
    private const uint DefaultSize = 0x00000040;
    private readonly nint _window;
    private readonly nint _icon;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private bool _disposed;
    public event Action? OpenRequested;
    public event Action? MenuRequested;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW", SetLastError = true)]
    private static extern nint LoadImage(nint module, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);

    public TrayIcon(nint window, string iconPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iconPath);
        _window = window;
        _icon = LoadImage(0, Path.GetFullPath(iconPath), ImageIcon, 0, 0, LoadFromFile | DefaultSize);
        if (_icon == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Unable to load tray icon from '{iconPath}'.");
        Add();
    }
    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1,
        Flags = 1 | 2 | 4 | 128, CallbackMessage = Callback, Icon = _icon,
        Tip = "Cyclop Windows", Info = "", InfoTitle = "", Version = 4
    };
    private void Add()
    {
        var data = Data();
        if (ShellNotifyIcon(NimAdd, ref data)) ShellNotifyIcon(NimSetVersion, ref data);
    }
    public void HandleMessage(int message, nint lParam)
    {
        if (_disposed) return;
        if ((uint)message == _taskbarCreated) { Add(); return; }
        if (message != Callback) return;
        var action = (int)((long)lParam & 0xFFFF);
        if (action is 0x007B or 0x0205) MenuRequested?.Invoke();
        else if (action is 0x0400 or 0x0401 or 0x0203) OpenRequested?.Invoke();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            var data = Data();
            ShellNotifyIcon(NimDelete, ref data);
        }
        finally
        {
            DestroyIcon(_icon);
        }
    }
}
