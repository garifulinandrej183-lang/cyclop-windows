using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Cyclop.Core.Shelf;

/// <summary>Persists file references and separately managed screenshot files. Removing cards never removes referenced originals.</summary>
public sealed class FileShelfService : IAsyncDisposable
{
    private readonly string _indexPath;
    private readonly string _managedDirectory;
    private readonly IImageFingerprint _fingerprint;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ShelfItem> _items = [];
    private readonly Dictionary<string, byte[]> _imageHashes = new(StringComparer.OrdinalIgnoreCase);
    private volatile ShelfItem[] _snapshot = [];
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _managedWatcher;
    private FileSystemWatcher? _managedParentWatcher;
    private readonly SemaphoreSlim _managedSignal = new(0, 1);
    private readonly CancellationTokenSource _managedLifetime = new();
    private readonly object _managedWatchSync = new();
    private Task? _managedWorker;
    private bool _managedPruneNeedsPersist;
    private CancellationTokenSource? _watchLifetime;
    private readonly object _watchSync = new();
    private readonly SemaphoreSlim _watchSignal = new(0, 1);
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queuedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _processingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requeuePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _watchSeenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, DateTime Written)> _watchSeenStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _watchSeenOrder = new();
    private bool _reconcileRequested;
    private DateTime _watchStartedUtc;
    private Task? _worker;
    private volatile bool _disposed;

    public FileShelfService(string indexPath, string managedImageDirectory, IImageFingerprint? fingerprint = null)
    {
        _indexPath = Path.GetFullPath(indexPath); _managedDirectory = Path.GetFullPath(managedImageDirectory);
        _fingerprint = fingerprint ?? new ByteImageFingerprint();
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!); Directory.CreateDirectory(_managedDirectory);
        StartManagedFolderWatchers();
    }

    public IReadOnlyList<ShelfItem> Items => _snapshot;
    public string ManagedScreenshotFolder => _managedDirectory;
    public int MaxCards { get; set; } = 60;
    public int MaxManagedImages { get; set; } = 100;
    public event EventHandler? ItemsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _items.Clear(); foreach (var hash in _imageHashes.Values) CryptographicOperations.ZeroMemory(hash); _imageHashes.Clear();
            if (!File.Exists(_indexPath)) { PublishSnapshot(); loaded = true; return; }
            try
            {
                var rows = JsonSerializer.Deserialize<List<ShelfItem>>(await File.ReadAllBytesAsync(_indexPath, cancellationToken).ConfigureAwait(false)) ?? [];
                foreach (var row in rows.Take(Math.Clamp(MaxCards, 1, 500)))
                {
                    var path = Normalize(row.Path);
                    if (_items.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                    _items.Add(row with { Path = path });
                    if (row.Kind == ShelfItemKind.ManagedImage) TryIndexImage(path);
                }
                if (PruneMissingManagedImagesCore()) _managedPruneNeedsPersist = true;
                if (_managedPruneNeedsPersist)
                {
                    await PersistAsync(cancellationToken).ConfigureAwait(false);
                    _managedPruneNeedsPersist = false;
                }
                PublishSnapshot();
                loaded = true;
            }
            catch (JsonException) { /* Preserve a damaged index for manual recovery; do not overwrite it during load. */ }
        }
        finally { _gate.Release(); }
        if (loaded) ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var normalized = paths.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Length == 0) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var path in normalized)
            {
                if (_items.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                _items.Insert(0, new(Guid.NewGuid(), path, ShelfItemKind.FileReference, DateTimeOffset.UtcNow));
            }
            TrimCards(); await PersistAsync(cancellationToken).ConfigureAwait(false); PublishSnapshot();
        }
        finally { _gate.Release(); }
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves image bytes as an ordinary managed PNG and adds a managed card. Intended for explicit image-save opt-in.</summary>
    public async Task<ShelfItem?> AddImageAsync(ReadOnlyMemory<byte> imageBytes, string? sourceName = null, CancellationToken cancellationToken = default)
    {
        if (imageBytes.Length is <= 8 or > 64 * 1024 * 1024) return null;
        byte[] png;
        byte[] hash;
        try { (png, hash) = await Task.Run(() => (_fingerprint.EncodeAsPng(imageBytes), _fingerprint.GetPixelHash(imageBytes)), cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException or IOException or NotSupportedException or ExternalException) { return null; }
        if (png.Length is <= 8 or > 64 * 1024 * 1024) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ShelfItem? added;
        var shelfChanged = false;
        try
        {
            if (_disposed) return null;
            if (PruneMissingManagedImagesCore())
            {
                _managedPruneNeedsPersist = true;
                PublishSnapshot();
                shelfChanged = true;
            }
            if (_managedPruneNeedsPersist)
            {
                await PersistAsync(cancellationToken).ConfigureAwait(false);
                _managedPruneNeedsPersist = false;
                PublishSnapshot();
            }
            var duplicate = _items.FirstOrDefault(x => x.Kind == ShelfItemKind.ManagedImage && _imageHashes.TryGetValue(x.Path, out var prior) && CryptographicOperations.FixedTimeEquals(prior, hash));
            if (duplicate is not null) added = duplicate;
            else
            {
                Directory.CreateDirectory(_managedDirectory);
                AttachManagedWatcher();
                var safeName = Sanitize(Path.GetFileNameWithoutExtension(sourceName ?? "Screenshot"));
                var path = Path.Combine(_managedDirectory, $"CyclopScreenshot-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{safeName}-{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(path, png, cancellationToken).ConfigureAwait(false);
                var item = new ShelfItem(Guid.NewGuid(), path, ShelfItemKind.ManagedImage, DateTimeOffset.UtcNow);
                _items.Insert(0, item); _imageHashes[path] = hash.ToArray();
                TrimCards(); await PersistAsync(cancellationToken).ConfigureAwait(false);
                await CleanupManagedImagesCoreAsync(cancellationToken).ConfigureAwait(false);
                PublishSnapshot(); added = _items.Any(x => x.Id == item.Id) ? item : null;
                shelfChanged = true;
            }
        }
        finally
        {
            _gate.Release();
            CryptographicOperations.ZeroMemory(hash);
            if (shelfChanged) ItemsChanged?.Invoke(this, EventArgs.Empty);
        }
        return added;
    }

    /// <summary>Imports an image from the selected watched folder to managed storage; source file is left untouched.</summary>
    public async Task<ShelfItem?> ImportWatchedImageAsync(string path, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length is <= 0 or > 64 * 1024 * 1024) return null;
                var bytes = await ReadImageFileBoundedAsync(path, cancellationToken).ConfigureAwait(false);
                if (bytes is null) return null;
                return await AddImageAsync(bytes, Path.GetFileName(path), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5) return null;
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
        return null;
    }

    private static async Task<byte[]?> ReadImageFileBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        if (stream.Length is <= 0 or > 64 * 1024 * 1024) return null;
        var expectedLength = stream.Length;
        var bytes = new byte[checked((int)expectedLength)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return stream.Length == expectedLength ? bytes : null;
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var item = _items.FirstOrDefault(x => x.Id == id); if (item is null) return;
            _items.Remove(item); if (item.Kind == ShelfItemKind.ManagedImage && _imageHashes.Remove(item.Path, out var hash)) CryptographicOperations.ZeroMemory(hash);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            PublishSnapshot();
        }
        finally { _gate.Release(); }
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClearCardsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _items.Clear(); foreach (var hash in _imageHashes.Values) CryptographicOperations.ZeroMemory(hash); _imageHashes.Clear(); await PersistAsync(cancellationToken).ConfigureAwait(false); PublishSnapshot(); }
        finally { _gate.Release(); }
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CleanupManagedImagesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await CleanupManagedImagesCoreAsync(cancellationToken).ConfigureAwait(false); await PersistAsync(cancellationToken).ConfigureAwait(false); PublishSnapshot(); }
        finally { _gate.Release(); }
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes managed screenshot cards whose files have been deleted outside Cyclop and saves the updated index.</summary>
    public async Task PruneMissingManagedImagesAsync(CancellationToken cancellationToken = default)
    {
        var notify = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            var changed = PruneMissingManagedImagesCore();
            if (changed) _managedPruneNeedsPersist = true;
            if (!_managedPruneNeedsPersist) return;
            PublishSnapshot();
            notify = true;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            _managedPruneNeedsPersist = false;
            if (Directory.Exists(_managedDirectory)) AttachManagedWatcher();
        }
        finally
        {
            _gate.Release();
            if (notify) ItemsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool PruneMissingManagedImagesCore()
    {
        var changed = false;
        foreach (var item in _items.Where(x => x.Kind == ShelfItemKind.ManagedImage).ToArray())
        {
            try { _ = File.GetAttributes(item.Path); }
            catch (FileNotFoundException) { RemoveMissingManagedItem(item); changed = true; }
            catch (DirectoryNotFoundException)
            {
                if (ManagedFolderIsMissingButParentAvailable()) { RemoveMissingManagedItem(item); changed = true; }
            }
            catch (IOException) { /* Unknown I/O failures do not prove the file was deleted. */ }
            catch (UnauthorizedAccessException) { /* An inaccessible file may still exist. */ }
        }
        return changed;
    }

    private bool ManagedFolderIsMissingButParentAvailable()
    {
        try { _ = File.GetAttributes(_managedDirectory); return false; }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            try { _ = File.GetAttributes(Path.GetDirectoryName(_managedDirectory)!); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void RemoveMissingManagedItem(ShelfItem item)
    {
        _items.Remove(item);
        if (_imageHashes.Remove(item.Path, out var hash)) CryptographicOperations.ZeroMemory(hash);
    }

    private void StartManagedFolderWatchers()
    {
        _managedWorker = Task.Run(() => ManagedFolderLoopAsync(_managedLifetime.Token));
        try
        {
            var parent = Path.GetDirectoryName(_managedDirectory)!;
            var watcher = new FileSystemWatcher(parent)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName
            };
            watcher.Created += (_, e) => OnManagedParentChanged(e.FullPath);
            watcher.Deleted += (_, e) => OnManagedParentChanged(e.FullPath);
            watcher.Renamed += (_, e) => { OnManagedParentChanged(e.OldFullPath); OnManagedParentChanged(e.FullPath); };
            watcher.Error += (_, _) => { AttachManagedWatcher(); SignalManagedWorker(); };
            _managedParentWatcher = watcher;
            watcher.EnableRaisingEvents = true;
            AttachManagedWatcher();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            _managedParentWatcher?.Dispose();
            _managedParentWatcher = null;
            // LoadAsync and the public prune method remain available as fallbacks.
        }
    }

    private void OnManagedParentChanged(string path)
    {
        if (!string.Equals(Path.GetFullPath(path), _managedDirectory, StringComparison.OrdinalIgnoreCase)) return;
        // The path may already refer to a new directory while the old watcher follows its renamed predecessor.
        AttachManagedWatcher(reset: true);
        SignalManagedWorker();
    }

    private void AttachManagedWatcher(bool reset = false)
    {
        lock (_managedWatchSync)
        {
            if (_disposed) return;
            if (_managedWatcher is not null)
            {
                if (!reset && Directory.Exists(_managedDirectory)) return;
                _managedWatcher.Dispose();
                _managedWatcher = null;
            }
            if (!Directory.Exists(_managedDirectory)) return;
            try
            {
                var watcher = new FileSystemWatcher(_managedDirectory)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    Filter = "CyclopScreenshot-*.png"
                };
                watcher.Deleted += (_, _) => SignalManagedWorker();
                watcher.Renamed += (_, _) => SignalManagedWorker();
                watcher.Error += (_, _) =>
                {
                    lock (_managedWatchSync)
                    {
                        if (ReferenceEquals(_managedWatcher, watcher))
                        {
                            watcher.Dispose();
                            _managedWatcher = null;
                        }
                    }
                    AttachManagedWatcher();
                    SignalManagedWorker();
                };
                watcher.EnableRaisingEvents = true;
                _managedWatcher = watcher;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            { SignalManagedWorker(); }
        }
    }

    private async Task ManagedFolderLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await _managedSignal.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            try { await Task.Delay(180, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            try { await PruneMissingManagedImagesAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave the prune marked dirty; a later filesystem event or UI fallback can persist it.
            }
        }
    }

    private void SignalManagedWorker()
    {
        if (_disposed) return;
        try { _managedSignal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    public async Task ConfigureWatchAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        StopWatch();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _watchLifetime?.Dispose(); _watchLifetime = null;
        while (_watchSignal.Wait(0)) { }
        var folder = Path.GetFullPath(folderPath);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Screenshot folder does not exist.");
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_watchSync)
        {
            _watchLifetime = lifetime; _pendingPaths.Clear(); _queuedPaths.Clear(); _processingPaths.Clear(); _requeuePaths.Clear();
            _watchSeenPaths.Clear(); _watchSeenStamps.Clear(); _watchSeenOrder.Clear(); _reconcileRequested = false; _watchStartedUtc = DateTime.UtcNow;
        }
        var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite, Filter = "*.*", InternalBufferSize = 16 * 1024 };
        watcher.Created += (_, e) => QueuePath(e.FullPath); watcher.Changed += (_, e) => QueuePath(e.FullPath); watcher.Renamed += (_, e) => QueuePath(e.FullPath);
        watcher.Error += (_, _) => { lock (_watchSync) { _reconcileRequested = true; SignalWorker(); } };
        _watcher = watcher;
        _worker = Task.Run(() => WatchLoopAsync(folder, lifetime.Token), CancellationToken.None);
        watcher.EnableRaisingEvents = true;
        try
        {
            foreach (var existing in Directory.EnumerateFiles(folder))
            {
                lock (_watchSync)
                {
                    var fullPath = Path.GetFullPath(existing);
                    if (!_queuedPaths.Contains(fullPath)) RememberPath(fullPath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { lock (_watchSync) { _reconcileRequested = true; SignalWorker(); } }
        await Task.Yield();
    }

    public void StopWatch()
    {
        _watcher?.Dispose(); _watcher = null;
        lock (_watchSync) { _watchLifetime?.Cancel(); SignalWorker(); }
    }

    private async Task WatchLoopAsync(string folder, CancellationToken ct)
    {
        while (true)
        {
            try { await _watchSignal.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            bool reconcile;
            lock (_watchSync) { reconcile = _reconcileRequested; _reconcileRequested = false; }
            if (reconcile)
            {
                try
                {
                    foreach (var candidate in Directory.EnumerateFiles(folder).Where(IsImagePath))
                    {
                        var fullPath = Path.GetFullPath(candidate);
                        lock (_watchSync)
                        {
                            if (_queuedPaths.Contains(fullPath)) continue;
                            var info = new FileInfo(fullPath);
                            if (_watchSeenStamps.TryGetValue(fullPath, out var stamp) && stamp == (info.Length, info.LastWriteTimeUtc)) continue;
                            if (info.CreationTimeUtc <= _watchStartedUtc && info.LastWriteTimeUtc <= _watchStartedUtc)
                            { RememberPath(fullPath); continue; }
                            if (_pendingPaths.Count >= 512) { _reconcileRequested = true; continue; }
                            _queuedPaths.Add(fullPath);
                            _pendingPaths.Add(fullPath);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { lock (_watchSync) { _reconcileRequested = true; SignalWorker(); } }
            }

            string[] work;
            lock (_watchSync) { work = _pendingPaths.ToArray(); _pendingPaths.Clear(); foreach (var path in work) _processingPaths.Add(path); }
            foreach (var path in work.Where(IsImagePath).Take(512))
            {
                var imported = false;
                try
                {
                    var stable = await WaitForStableAsync(path, ct).ConfigureAwait(false);
                    if (stable) imported = await ImportWatchedImageAsync(path, ct).ConfigureAwait(false) is not null;
                }
                catch (OperationCanceledException) { }
                finally { CompleteWatchedPath(path, imported); }
            }
            bool again;
            lock (_watchSync) again = _pendingPaths.Count > 0 || _reconcileRequested;
            if (again) SignalWorker();
        }
    }

    private async Task<bool> WaitForStableAsync(string path, CancellationToken ct)
    {
        long last = -1;
        for (var attempt = 0; attempt < 12 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                var info = new FileInfo(path); if (!info.Exists || info.Length <= 0) return false;
                if (info.Length == last) return true; last = info.Length;
            }
            catch (IOException) { }
            try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private void QueuePath(string path)
    {
        lock (_watchSync)
        {
            if (_watchLifetime is null || _watchLifetime.IsCancellationRequested) return;
            var fullPath = Path.GetFullPath(path);
            if (!IsImagePath(fullPath)) return;
            // A screenshot application may overwrite an existing filename.
            // Queued/processing sets coalesce events; pixel hashes deduplicate content.
            if (_processingPaths.Contains(fullPath)) { _requeuePaths.Add(fullPath); return; }
            if (_queuedPaths.Contains(fullPath)) return;
            if (_pendingPaths.Count < 512) { _queuedPaths.Add(fullPath); _pendingPaths.Add(fullPath); }
            else _reconcileRequested = true;
            SignalWorker();
        }
    }
    private void SignalWorker()
    {
        if (_disposed) return;
        try { _watchSignal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }
    private void CompleteWatchedPath(string path, bool imported)
    {
        lock (_watchSync)
        {
            _processingPaths.Remove(path);
            var changedAgain = _requeuePaths.Remove(path);
            _queuedPaths.Remove(path);
            if (imported && !changedAgain) RememberPath(path);
            else if (changedAgain)
            {
                if (_pendingPaths.Count < 512) { _queuedPaths.Add(path); _pendingPaths.Add(path); SignalWorker(); }
                else _reconcileRequested = true;
            }
        }
    }
    private void RememberPath(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            _watchSeenStamps[path] = (info.Length, info.LastWriteTimeUtc);
        }
        catch (IOException) { return; }
        if (!_watchSeenPaths.Add(path)) return;
        _watchSeenOrder.Enqueue(path);
        while (_watchSeenOrder.Count > 20_000) { var old = _watchSeenOrder.Dequeue(); _watchSeenPaths.Remove(old); _watchSeenStamps.Remove(old); }
    }
    private static bool IsImagePath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".bmp" or ".jpg" or ".jpeg" or ".gif" or ".tif" or ".tiff";
    private string Normalize(string path) => Path.GetFullPath(path);
    private static string Sanitize(string value) { var invalid = Path.GetInvalidFileNameChars(); var chars = value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray(); var result = new string(chars).Trim(); return string.IsNullOrWhiteSpace(result) ? "Screenshot" : result[..Math.Min(result.Length, 48)]; }
    private void TrimCards() { var max = Math.Clamp(MaxCards, 1, 500); while (_items.Count > max) { var last = _items[^1]; _items.RemoveAt(_items.Count - 1); if (last.Kind == ShelfItemKind.ManagedImage && _imageHashes.Remove(last.Path, out var hash)) CryptographicOperations.ZeroMemory(hash); } }

    private Task CleanupManagedImagesCoreAsync(CancellationToken ct)
    {
        var max = Math.Clamp(MaxManagedImages, 0, 1000);
        var explicitFileReferences = _items.Where(x => x.Kind == ShelfItemKind.FileReference)
            .Select(x => Path.GetFullPath(x.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(_managedDirectory, "CyclopScreenshot-*.png")
            .Where(IsManagedGeneratedImage)
            .Where(x => !explicitFileReferences.Contains(Path.GetFullPath(x)))
            .OrderByDescending(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var file in files.Skip(max))
        {
            ct.ThrowIfCancellationRequested();
            var old = _items.FirstOrDefault(x => x.Kind == ShelfItemKind.ManagedImage && string.Equals(x.Path, file, StringComparison.OrdinalIgnoreCase));
            if (old is not null) _items.Remove(old);
            if (_imageHashes.Remove(file, out var hash)) CryptographicOperations.ZeroMemory(hash);
            try { File.Delete(file); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    private static bool IsManagedGeneratedImage(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.LastIndexOf('-');
        return dash > 0 && Guid.TryParseExact(name[(dash + 1)..], "N", out _);
    }

    private void TryIndexImage(string path)
    { try { _imageHashes[path] = _fingerprint.GetPixelHashFromFile(path); } catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or InvalidDataException or ExternalException) { } }

    private void PublishSnapshot() => _snapshot = _items.ToArray();

    private async Task PersistAsync(CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(_items);
        var temporary = _indexPath + ".new";
        await File.WriteAllBytesAsync(temporary, data, ct).ConfigureAwait(false);
        File.Move(temporary, _indexPath, overwrite: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; StopWatch();
        _managedLifetime.Cancel(); _managedWatcher?.Dispose(); _managedWatcher = null; _managedParentWatcher?.Dispose(); _managedParentWatcher = null;
        if (_worker is not null) try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (_managedWorker is not null) try { await _managedWorker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await _gate.WaitAsync().ConfigureAwait(false);
        try { foreach (var hash in _imageHashes.Values) CryptographicOperations.ZeroMemory(hash); _imageHashes.Clear(); }
        finally { _gate.Release(); }
        _watchLifetime?.Dispose(); _watchLifetime = null; _watchSignal.Dispose(); _managedLifetime.Dispose(); _managedSignal.Dispose();
    }
}
