using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cyclop.Core.Clipboard;
using Cyclop.Core.Data;
using Cyclop.Core.Shelf;
using Cyclop.Windows.Clipboard;
using Cyclop.Windows.Shelf;

internal static class Program
{
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;
    private const uint CfUnicodeText = 13;
    private static nint _ownerHwnd;
    private static int _failures;
    private static uint _expectedClipboardSequence;
    private static bool _clipboardWasModified;

    [STAThread]
    private static int Main(string[] args)
    {
        DirectoryInfo? temporary = null;
        System.Windows.IDataObject? originalClipboard = null;
        var originalCaptured = false;
        try
        {
            if (args.Length > 0 && args[0].Equals("--shelf-only", StringComparison.OrdinalIgnoreCase))
            {
                temporary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CyclopClipboardSmoke-" + Guid.NewGuid().ToString("N")));
                RunShelfImageChecks(temporary.FullName);
            }
            else if (args.Length > 0 && args[0].Equals("--data-only", StringComparison.OrdinalIgnoreCase))
            {
                temporary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CyclopClipboardSmoke-" + Guid.NewGuid().ToString("N")));
                RunDpapiPersistence(temporary.FullName);
            }
            else if (args.Length > 0 && args[0].Equals("--safe", StringComparison.OrdinalIgnoreCase))
            {
                temporary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CyclopClipboardSmoke-" + Guid.NewGuid().ToString("N")));
                RunDpapiPersistence(temporary.FullName);
                RunShelfImageChecks(temporary.FullName);
            }
            else if (args.Length == 0 || !args[0].Equals("--live-clipboard", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Use --safe or explicitly opt in with --live-clipboard.");
            }
            else
            {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var owner = CreateMessageWindow();
            _ownerHwnd = owner.Handle;
            originalClipboard = GetOriginalClipboard();
            _expectedClipboardSequence = GetClipboardSequenceNumber();
            originalCaptured = true;

            RunPrivacyFormatChecks();
            temporary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CyclopClipboardSmoke-" + Guid.NewGuid().ToString("N")));
            RunDpapiPersistence(temporary.FullName);
            RunClipboardMonitorChecks(dispatcher);
            RunShelfImageChecks(temporary.FullName);
            }
        }
        catch (Exception ex) { Fail("Smoke harness", ex.GetType().Name); Console.Error.WriteLine(ex.StackTrace); }
        finally
        {
            if (originalCaptured)
            {
                try { RestoreOriginalClipboard(originalClipboard); }
                catch (Exception ex) { Fail("Restore original clipboard", ex.GetType().Name); }
            }
            if (temporary is not null)
            {
                try { temporary.Delete(recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail("Remove isolated temp data", ex.GetType().Name); }
            }
        }

        Console.WriteLine(_failures == 0
            ? "Windows clipboard smoke checks passed. No pre-existing clipboard content was printed or saved."
            : $"Windows clipboard smoke checks failed: {_failures}. Pre-existing clipboard content was not printed or saved.");
        return _failures == 0 ? 0 : 1;
    }

    private static void RunPrivacyFormatChecks()
    {
        Console.WriteLine("Running native clipboard marker checks.");
        SetNativeFormat("CanIncludeInClipboardHistory", [0, 0, 0, 0]);
        Check("CanInclude DWORD zero", ClipboardPrivacyPolicy.IsExcluded());
        SetNativeFormat("CanIncludeInClipboardHistory", [1, 0, 0, 0]);
        Check("CanInclude DWORD one", !ClipboardPrivacyPolicy.IsExcluded());
        SetNativeFormat("CanIncludeInClipboardHistory", [1]);
        Console.WriteLine("Checking malformed marker bytes.");
        Check("Malformed CanInclude format", ClipboardPrivacyPolicy.IsExcluded());
        SetNativeFormat("ExcludeClipboardContentFromMonitorProcessing", [1, 0, 0, 0]);
        Check("Exclude format marker", ClipboardPrivacyPolicy.IsExcluded());
        ClearNativeClipboard();
        Check("CanInclude format absent", !ClipboardPrivacyPolicy.IsExcluded());
    }

    private static void RunDpapiPersistence(string root)
    {
        const string sample = "Cyclop isolated DPAPI smoke sample 7d64b4";
        var database = Path.Combine(root, "history.db");
        var protector = new DpapiDataProtector();
        var repository = new EncryptedClipboardRepository(database, protector);
        var history = new ClipboardHistoryService(repository, new ClipboardCaptureOptions { ExplicitlyEnabled = true });
        try
        {
            history.InitializeAsync().GetAwaiter().GetResult();
            Check("DPAPI history capture", history.CaptureAsync(ClipboardPayload.FromText(sample)).GetAwaiter().GetResult());
        }
        finally { history.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        Check("History database hides sample text", !Contains(File.ReadAllBytes(database), Encoding.UTF8.GetBytes(sample)));
        repository = new EncryptedClipboardRepository(database, protector);
        history = new ClipboardHistoryService(repository, new ClipboardCaptureOptions { ExplicitlyEnabled = true });
        try
        {
            history.InitializeAsync().GetAwaiter().GetResult();
            Check("DPAPI history restore", history.Items.Count == 1 && history.Items[0].Payload.Text == sample);
        }
        finally { history.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void RunClipboardMonitorChecks(Dispatcher dispatcher)
    {
        using var monitor = new ClipboardMonitor(dispatcher, () => true);
        var captured = new List<ClipboardPayload>();
        monitor.Changed += (_, eventArgs) => captured.Add(eventArgs.Payload);
        monitor.Start();

        EnsureClipboardSequence();
        WaitWithDispatcher(monitor.RestoreAsync(ClipboardPayload.FromText("restore marker smoke")), TimeSpan.FromSeconds(3));
        RecordOwnedClipboardMutation();
        PumpFor(TimeSpan.FromMilliseconds(500));
        Check("Restored clipboard write is not recaptured", captured.Count == 0);

        EnsureClipboardSequence();
        System.Windows.Clipboard.SetText("external clipboard smoke text");
        RecordOwnedClipboardMutation();
        Check("WM_CLIPBOARDUPDATE captures text once", WaitForDispatcher(() => captured.Count == 1, TimeSpan.FromSeconds(3)) && captured[0].Text == "external clipboard smoke text");

        var retryBefore = monitor.BusyRetryCount;
        EnsureClipboardSequence();
        var lockedClipboard = new ManualResetEventSlim();
        var lockFinished = new ManualResetEventSlim();
        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try { WriteTextAndHoldClipboard("busy retry smoke", TimeSpan.FromMilliseconds(260), lockedClipboard); }
            catch (Exception ex) { workerError = ex; lockedClipboard.Set(); }
            finally { lockFinished.Set(); }
        }) { IsBackground = true, Name = "Cyclop clipboard smoke lock" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        if (!lockedClipboard.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Clipboard lock helper did not complete.");
        PumpFor(TimeSpan.FromMilliseconds(100));
        if (!lockFinished.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Clipboard lock helper did not release the clipboard.");
        worker.Join(TimeSpan.FromSeconds(1));
        if (workerError is not null) throw new InvalidOperationException("Clipboard lock helper failed.", workerError);
        Check("Busy clipboard read retried", WaitForDispatcher(() => captured.Count == 2, TimeSpan.FromSeconds(3)) && monitor.BusyRetryCount > retryBefore && monitor.BusyRetryCount - retryBefore <= 7);

        var bitmap = CreateBitmap();
        EnsureClipboardSequence();
        System.Windows.Clipboard.SetImage(bitmap);
        RecordOwnedClipboardMutation();
        var imageCaptured = WaitForDispatcher(() => captured.Count == 3, TimeSpan.FromSeconds(5));
        var fingerprint = new WpfImageFingerprint();
        var expected = fingerprint.GetPixelHash(Encode(bitmap, png: true));
        Check("Bitmap clipboard data arrives as bounded PNG", imageCaptured && captured[2].Kind == ClipboardPayloadKind.Png && fingerprint.GetPixelHash(captured[2].Png!).SequenceEqual(expected));
        monitor.Stop();
    }

    private static void RunShelfImageChecks(string root)
    {
        var folder = Path.Combine(root, "watched");
        var index = Path.Combine(root, "shelf.json");
        var managed = Path.Combine(root, "managed");
        Directory.CreateDirectory(folder);
        var fingerprint = new CountingFingerprint(new WpfImageFingerprint());
        var shelf = new FileShelfService(index, managed, fingerprint);
        try
        {
        shelf.LoadAsync().GetAwaiter().GetResult();
        using var added = new ManualResetEventSlim();
        shelf.ItemsChanged += (_, _) => { if (shelf.Items.Count > 0) added.Set(); };
        shelf.ConfigureWatchAsync(folder).GetAwaiter().GetResult();

        var bitmap = CreateBitmap();
        var fullBitmap = Encode(bitmap, png: false);
        var original = Path.Combine(folder, "synthetic.bmp");
        File.WriteAllBytes(original, fullBitmap[..54]);
        Thread.Sleep(850);
        using (var append = new FileStream(original, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) append.Write(fullBitmap.AsSpan(54));
        Check("Partially written watched bitmap is retried", added.Wait(TimeSpan.FromSeconds(8)) && shelf.Items.Count == 1 && shelf.Items[0].Kind == ShelfItemKind.ManagedImage);

        var beforeBurst = fingerprint.ImageOperations;
        File.WriteAllBytes(Path.Combine(folder, "burst-a.bmp"), fullBitmap);
        File.WriteAllBytes(Path.Combine(folder, "burst-b.bmp"), fullBitmap);
        Check("Watcher drains burst events and deduplicates", WaitUntil(() => fingerprint.ImageOperations >= beforeBurst + 4, TimeSpan.FromSeconds(10)) && shelf.Items.Count == 1);

        var clipboardImage = Encode(bitmap, png: true);
        var duplicate = shelf.AddImageAsync(clipboardImage, "synthetic clipboard image").GetAwaiter().GetResult();
        Check("Clipboard and watched image pixel-deduplicate", duplicate is not null && shelf.Items.Count == 1 && string.Equals(duplicate.Path, shelf.Items[0].Path, StringComparison.OrdinalIgnoreCase));
        Check("Watched source file remains untouched", File.Exists(original));
        File.WriteAllBytes(original, Encode(CreateBitmap(127), png: false));
        Check("Overwriting an existing screenshot name imports its new pixels", WaitUntil(() => shelf.Items.Count == 2, TimeSpan.FromSeconds(8)));
        shelf.StopWatch();
        }
        finally { shelf.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    private static System.Windows.IDataObject? GetOriginalClipboard()
    {
        var initialSequence = GetClipboardSequenceNumber();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (ClipboardPrivacyPolicy.IsExcluded()) throw new InvalidOperationException("Current clipboard opted out; probe stopped without reading its payload.");
                var source = System.Windows.Clipboard.GetDataObject();
                if (source is null) return null;
                var snapshot = new System.Windows.DataObject();
                foreach (var format in source.GetFormats(autoConvert: false))
                {
                    var value = source.GetData(format, autoConvert: false);
                    if (value is MemoryStream stream) value = new MemoryStream(stream.ToArray(), writable: false);
                    else if (value is Stream streamValue)
                    {
                        using var copy = new MemoryStream(); streamValue.CopyTo(copy); value = new MemoryStream(copy.ToArray(), writable: false);
                    }
                    else if (value is BitmapSource bitmap && bitmap.CanFreeze) bitmap.Freeze();
                    if (value is not null) snapshot.SetData(format, value, autoConvert: false);
                }
                if (GetClipboardSequenceNumber() != initialSequence)
                    throw new InvalidOperationException("Clipboard changed while its in-memory snapshot was being made; probe aborted before writing.");
                return snapshot;
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                if (attempt == 7) throw;
                if (ex is InvalidOperationException && ex.Message.StartsWith("Current clipboard opted out", StringComparison.Ordinal)) throw;
                PumpFor(TimeSpan.FromMilliseconds(40 * (attempt + 1)));
            }
        }
        return null;
    }

    private static void RestoreOriginalClipboard(System.Windows.IDataObject? original)
    {
        if (!_clipboardWasModified) return;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (GetClipboardSequenceNumber() != _expectedClipboardSequence)
            {
                Console.Error.WriteLine("SKIP restore: clipboard sequence changed externally; newer clipboard left untouched.");
                return;
            }
            try
            {
                if (original is null) System.Windows.Clipboard.Clear();
                else System.Windows.Clipboard.SetDataObject(original, copy: true);
                _expectedClipboardSequence = GetClipboardSequenceNumber();
                return;
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                if (attempt == 7) throw;
                PumpFor(TimeSpan.FromMilliseconds(40 * (attempt + 1)));
            }
        }
    }

    private static HwndSource CreateMessageWindow()
    {
        var parameters = new HwndSourceParameters("CyclopClipboardSmoke") { Width = 0, Height = 0, ParentWindow = (nint)(-3), WindowStyle = 0 };
        return new HwndSource(parameters);
    }

    private static BitmapSource CreateBitmap(byte variant = 0)
    {
        var bitmap = new WriteableBitmap(3, 2, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[]
        {
            0, 0, 255, 255,   0, 255, 0, 255,   255, 0, 0, 255,
            0, 255, 255, 255, 255, 0, 255, 255, 255, 255, 0, 255,
        };
        pixels[0] = variant;
        bitmap.WritePixels(new Int32Rect(0, 0, 3, 2), pixels, 12, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Encode(BitmapSource bitmap, bool png)
    {
        BitmapEncoder encoder = png ? new PngBitmapEncoder() : new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private static void SetNativeFormat(string name, byte[] payload)
    {
        EnsureClipboardSequence();
        if (!OpenClipboard(_ownerHwnd)) throw new ExternalException("Could not open clipboard for synthetic probe.", Marshal.GetLastWin32Error());
        nint allocation = 0;
        var emptied = false;
        try
        {
            EnsureClipboardSequence();
            if (!EmptyClipboard()) throw new ExternalException("Could not clear clipboard for synthetic probe.", Marshal.GetLastWin32Error());
            emptied = true;
            RecordOwnedClipboardMutation();
            var format = RegisterClipboardFormat(name);
            allocation = GlobalAlloc(GmemMoveable | GmemZeroInit, checked((nuint)Math.Max(payload.Length, 1)));
            if (allocation == 0) throw new OutOfMemoryException();
            var pointer = GlobalLock(allocation);
            if (pointer == 0) throw new ExternalException("Could not lock synthetic marker memory.", Marshal.GetLastWin32Error());
            try { if (payload.Length > 0) Marshal.Copy(payload, 0, pointer, payload.Length); }
            finally { GlobalUnlock(allocation); }
            if (SetClipboardData(format, allocation) == 0) throw new ExternalException("Could not set synthetic clipboard marker.", Marshal.GetLastWin32Error());
            allocation = 0;
        }
        finally
        {
            if (allocation != 0) GlobalFree(allocation);
            if (emptied) RecordOwnedClipboardMutation();
            CloseClipboard();
        }
    }

    private static void ClearNativeClipboard()
    {
        EnsureClipboardSequence();
        if (!OpenClipboard(_ownerHwnd)) throw new ExternalException("Could not open clipboard for synthetic probe.", Marshal.GetLastWin32Error());
        var emptied = false;
        try { EnsureClipboardSequence(); if (!EmptyClipboard()) throw new ExternalException("Could not clear clipboard for synthetic probe.", Marshal.GetLastWin32Error()); emptied = true; RecordOwnedClipboardMutation(); }
        finally { if (emptied) RecordOwnedClipboardMutation(); CloseClipboard(); }
    }

    private static void WriteTextAndHoldClipboard(string text, TimeSpan hold, ManualResetEventSlim acquired)
    {
        EnsureClipboardSequence();
        if (!OpenClipboard(_ownerHwnd)) throw new ExternalException("Could not acquire busy clipboard lock.", Marshal.GetLastWin32Error());
        nint allocation = 0;
        var emptied = false;
        try
        {
            EnsureClipboardSequence();
            if (!EmptyClipboard()) throw new ExternalException("Could not update busy clipboard.", Marshal.GetLastWin32Error());
            emptied = true;
            RecordOwnedClipboardMutation();
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            allocation = GlobalAlloc(GmemMoveable | GmemZeroInit, checked((nuint)bytes.Length));
            if (allocation == 0) throw new OutOfMemoryException();
            var pointer = GlobalLock(allocation);
            if (pointer == 0) throw new ExternalException("Could not lock synthetic text memory.", Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(allocation); }
            if (SetClipboardData(CfUnicodeText, allocation) == 0) throw new ExternalException("Could not set synthetic Unicode text.", Marshal.GetLastWin32Error());
            allocation = 0;
            acquired.Set();
            Thread.Sleep(hold);
        }
        finally
        {
            if (allocation != 0) GlobalFree(allocation);
            if (emptied) RecordOwnedClipboardMutation();
            CloseClipboard();
        }
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }

    private static bool WaitForDispatcher(Func<bool> condition, TimeSpan timeout)
    {
        if (condition()) return true;
        var frame = new DispatcherFrame();
        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow - start >= timeout) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame); return condition();
    }

    private static void PumpFor(TimeSpan duration) => _ = WaitForDispatcher(() => false, duration);

    private static void WaitWithDispatcher(Task task, TimeSpan timeout)
    {
        if (!WaitForDispatcher(() => task.IsCompleted, timeout)) throw new TimeoutException("Dispatcher operation timed out.");
        task.GetAwaiter().GetResult();
    }

    private static void Check(string name, bool passed)
    {
        if (passed) Console.WriteLine($"PASS {name}");
        else Fail(name, "condition was false");
    }

    private static void Fail(string name, string reason)
    {
        _failures++;
        Console.Error.WriteLine($"FAIL {name}: {reason}");
    }

    private static void RecordOwnedClipboardMutation()
    {
        _expectedClipboardSequence = GetClipboardSequenceNumber();
        _clipboardWasModified = true;
    }

    private static void EnsureClipboardSequence()
    {
        if (GetClipboardSequenceNumber() != _expectedClipboardSequence)
            throw new InvalidOperationException("Clipboard sequence changed externally; smoke test stopped without overwriting it.");
    }

    private sealed class CountingFingerprint(IImageFingerprint inner) : IImageFingerprint
    {
        private int _imageOperations;
        public int ImageOperations => Volatile.Read(ref _imageOperations);
        public byte[] GetPixelHash(ReadOnlyMemory<byte> imageBytes)
        { Interlocked.Increment(ref _imageOperations); return inner.GetPixelHash(imageBytes); }
        public byte[] GetPixelHashFromFile(string path)
        { Interlocked.Increment(ref _imageOperations); return inner.GetPixelHashFromFile(path); }
        public byte[] EncodeAsPng(ReadOnlyMemory<byte> imageBytes)
        { Interlocked.Increment(ref _imageOperations); return inner.EncodeAsPng(imageBytes); }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalFree(nint memory);
}
