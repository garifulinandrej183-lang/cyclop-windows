using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Cyclop.Windows.Shell;

/// <summary>Reveals an existing Spotify window or asks the registered Windows URI handler to open Spotify.</summary>
public static class SpotifyLauncher
{
    private const string ProtocolName = "spotify";
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint GwOwner = 4;

    public static async Task<bool> TryOpenAsync(CancellationToken cancellationToken = default)
    {
        IntPtr existingWindow;
        try { existingWindow = FindSpotifyWindow(); }
        catch (Exception) { return false; }

        if (existingWindow != IntPtr.Zero)
        {
            if (GetForegroundWindow() == existingWindow) return true;
            ShowWindowAsync(existingWindow, IsIconic(existingWindow) ? SwRestore : SwShow);
            SetForegroundWindow(existingWindow);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (GetForegroundWindow() == existingWindow) return true;
                await Task.Delay(50, cancellationToken);
            }
            return GetForegroundWindow() == existingWindow;
        }

        if (!HasRegisteredProtocolHandler()) return false;

        try
        {
            using var launchedProcess = Process.Start(new ProcessStartInfo("spotify:")
            {
                UseShellExecute = true
            });
            // For packaged handlers Windows may not expose a process handle.
            // No exception means the Shell accepted the registered URI activation.
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasRegisteredProtocolHandler()
    {
        try
        {
            using var protocol = Registry.ClassesRoot.OpenSubKey(ProtocolName, writable: false);
            if (protocol is null) return false;
            // Standard protocol registration is the named empty "URL Protocol" value,
            // not a subkey. ShellExecute resolves the actual command/package handler.
            return protocol.GetValue("URL Protocol", defaultValue: null) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IntPtr FindSpotifyWindow()
    {
        var processIds = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("Spotify"))
        {
            using (process)
            {
                try { processIds.Add(checked((uint)process.Id)); }
                catch (Exception) { }
            }
        }

        if (processIds.Count == 0) return IntPtr.Zero;

        var foreground = GetForegroundWindow();
        var foregroundMatch = IntPtr.Zero;
        var visibleWindow = IntPtr.Zero;
        var minimizedWindow = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (GetWindow(window, GwOwner) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (!processIds.Contains(processId)) return true;
            // Ignore ownerless background/helper windows belonging to Chromium subprocesses.
            if (GetWindowTextLength(window) == 0 || (GetWindowLongPtr(window, -20).ToInt64() & 0x80) != 0) return true;

            if (window == foreground)
            {
                foregroundMatch = window;
                return false;
            }

            if (!IsWindowVisible(window))
            {
                // A client closed to tray is reopened through its own URI handler,
                // not by showing an arbitrary hidden Chromium helper window.
                return true;
            }
            else if (IsIconic(window))
            {
                if (minimizedWindow == IntPtr.Zero) minimizedWindow = window;
            }
            else if (visibleWindow == IntPtr.Zero)
            {
                visibleWindow = window;
            }

            return true;
        }, IntPtr.Zero);

        return foregroundMatch != IntPtr.Zero
            ? foregroundMatch
            : visibleWindow != IntPtr.Zero
                ? visibleWindow
                : minimizedWindow;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
