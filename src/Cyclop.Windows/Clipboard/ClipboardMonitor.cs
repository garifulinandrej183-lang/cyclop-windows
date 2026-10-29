using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cyclop.Core.Clipboard;
using Cyclop.Windows.Shelf;

namespace Cyclop.Windows.Clipboard;

public sealed class ClipboardChangedEventArgs(ClipboardPayload payload) : EventArgs
{
    public ClipboardPayload Payload { get; } = payload;
}

/// <summary>One message-only window listens for WM_CLIPBOARDUPDATE and reads clipboard data on the WPF STA.</summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _shouldReadPayload;
    private readonly Func<ClipboardPayloadKind, bool> _shouldReadKind;
    private readonly WpfImageFingerprint _images = new();
    private HwndSource? _source;
    private long _generation;
    private int _busyRetryCount;
    private volatile bool _disposed;
    public ClipboardMonitor(Dispatcher dispatcher, Func<bool> shouldReadPayload, Func<ClipboardPayloadKind, bool>? shouldReadKind = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _shouldReadPayload = shouldReadPayload ?? throw new ArgumentNullException(nameof(shouldReadPayload));
        _shouldReadKind = shouldReadKind ?? (_ => true);
    }
    public event EventHandler<ClipboardChangedEventArgs>? Changed;
    public bool IsRunning => _source is not null;
    public int BusyRetryCount => Volatile.Read(ref _busyRetryCount);

    public void Start()
    {
        _dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this); if (_source is not null) return;
        var parameters = new HwndSourceParameters("CyclopClipboardMonitor") { Width = 0, Height = 0, ParentWindow = (nint)(-3), WindowStyle = 0 };
        _source = new HwndSource(parameters); _source.AddHook(WindowProc);
        if (!AddClipboardFormatListener(_source.Handle)) { _source.RemoveHook(WindowProc); _source.Dispose(); _source = null; throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
    }

    public void Stop()
    {
        _dispatcher.VerifyAccess(); Interlocked.Increment(ref _generation);
        if (_source is null) return;
        RemoveClipboardFormatListener(_source.Handle); _source.RemoveHook(WindowProc); _source.Dispose(); _source = null;
    }

    public async Task RestoreAsync(ClipboardPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        BitmapSource? bitmap = null;
        if (payload.Kind == ClipboardPayloadKind.Png)
            bitmap = await Task.Run(() => _images.Decode(payload.Png!), cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Avoid entering WPF's synchronous retry loop for a clipboard
                    // that is already busy; our retry delays yield the dispatcher.
                    if (!OpenClipboard(_source?.Handle ?? 0)) throw new ExternalException("Windows clipboard is busy.", Marshal.GetLastWin32Error());
                    CloseClipboard();
                    var data = new DataObject(); data.SetData(ClipboardPrivacyPolicy.InternalFormat, true);
                    switch (payload.Kind)
                    {
                        case ClipboardPayloadKind.Text: data.SetText(payload.Text ?? ""); break;
                        case ClipboardPayloadKind.Files:
                            var files = new StringCollection(); files.AddRange(payload.Files?.ToArray() ?? []); data.SetFileDropList(files); break;
                        case ClipboardPayloadKind.Png: data.SetImage(bitmap!); break;
                    }
                    global::System.Windows.Clipboard.SetDataObject(data, copy: true);
                }, DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < 7 && (ex is ExternalException or InvalidOperationException))
            { await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1)), cancellationToken).ConfigureAwait(false); }
        }
    }

    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate) { handled = true; var generation = Interlocked.Increment(ref _generation); _ = CaptureWithRetryAsync(generation, 0); }
        return 0;
    }

    private async Task CaptureWithRetryAsync(long generation, int attempt)
    {
        if (!_dispatcher.CheckAccess()) { await _dispatcher.InvokeAsync(() => CaptureWithRetryAsync(generation, attempt)).Task.Unwrap().ConfigureAwait(false); return; }
        if (_disposed || generation != Interlocked.Read(ref _generation) || !_shouldReadPayload()) return;
        var sequence = GetClipboardSequenceNumber();
        try
        {
            if (ClipboardPrivacyPolicy.IsExcluded()) return;
            if (sequence != GetClipboardSequenceNumber()) return;
            var data = global::System.Windows.Clipboard.GetDataObject();
            ClipboardPayload? payload = null;
            if (data is not null && data.GetDataPresent(DataFormats.FileDrop))
            {
                if (_shouldReadKind(ClipboardPayloadKind.Files) && data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length is > 0 and <= 256) payload = ClipboardPayload.FromFiles(paths);
            }
            else if (data is not null && _shouldReadKind(ClipboardPayloadKind.Text) && (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text)))
            {
                var format = data.GetDataPresent(DataFormats.UnicodeText) ? DataFormats.UnicodeText : DataFormats.Text;
                if (data.GetData(format) is string text && !string.IsNullOrWhiteSpace(text)) payload = ClipboardPayload.FromText(text);
            }
            else if (data is not null && _shouldReadKind(ClipboardPayloadKind.Png))
            {
                var image = ReadImageObject(data);
                if (image is not null && sequence == GetClipboardSequenceNumber())
                {
                    var png = await Task.Run(() => image is BitmapSource bitmap ? _images.EncodeBitmapAsPng(bitmap) : _images.EncodeAsPng((byte[])image), CancellationToken.None).ConfigureAwait(false);
                    if (png.Length <= 16 * 1024 * 1024) payload = ClipboardPayload.FromPng(png);
                }
            }
            if (sequence == GetClipboardSequenceNumber() && payload is not null && generation == Interlocked.Read(ref _generation))
            {
                var ready = payload;
                await _dispatcher.InvokeAsync(() => Changed?.Invoke(this, new(ready!))).Task.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            if (attempt >= 7 || generation != Interlocked.Read(ref _generation)) return;
            Interlocked.Increment(ref _busyRetryCount);
            await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1))).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => CaptureWithRetryAsync(generation, attempt + 1)).Task.Unwrap().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or NotSupportedException or IOException) { }
    }

    private static object? ReadImageObject(System.Windows.IDataObject data)
    {
        foreach (var format in new[] { "PNG", DataFormats.Bitmap })
        {
            try
            {
                if (!data.GetDataPresent(format)) continue;
                var value = data.GetData(format);
                if (value is byte[] bytes && bytes.Length <= 64 * 1024 * 1024) return bytes;
                if (value is MemoryStream stream && stream.Length <= 64 * 1024 * 1024) return stream.ToArray();
                if (value is BitmapSource bitmap && (long)bitmap.PixelWidth * bitmap.PixelHeight <= WpfImageFingerprint.MaxPixels)
                {
                    if (!bitmap.IsFrozen)
                    {
                        if (!bitmap.CanFreeze) bitmap = new WriteableBitmap(bitmap);
                        bitmap.Freeze();
                    }
                    return bitmap;
                }
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException or NotSupportedException) { }
        }
        return null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    public void Dispose()
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) Stop(); else _dispatcher.Invoke(Stop);
        _disposed = true;
    }
}
