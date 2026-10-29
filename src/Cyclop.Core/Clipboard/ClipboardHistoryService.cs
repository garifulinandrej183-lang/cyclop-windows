using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cyclop.Core.Data;

namespace Cyclop.Core.Clipboard;

/// <summary>Bounded encrypted clipboard history. Search and deduplication happen only in memory.</summary>
public sealed class ClipboardHistoryService : IAsyncDisposable
{
    private readonly EncryptedClipboardRepository _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ClipboardEntry> _items = [];
    private readonly Dictionary<Guid, byte[]> _digests = [];
    private volatile ClipboardEntry[] _snapshot = [];
    public ClipboardHistoryService(EncryptedClipboardRepository repository, ClipboardCaptureOptions? options = null)
    { _repository = repository ?? throw new ArgumentNullException(nameof(repository)); Options = options ?? new(); }

    public ClipboardCaptureOptions Options { get; }
    public IReadOnlyList<ClipboardEntry> Items => _snapshot;
    public event EventHandler? ItemsChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _items.Clear(); _digests.Clear();
            foreach (var row in await _repository.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var payload = Decode(row.Plaintext);
                    if (!IsValid(payload, out var encoded)) { CryptographicOperations.ZeroMemory(encoded); continue; }
                    CryptographicOperations.ZeroMemory(encoded);
                    var entry = new ClipboardEntry(row.Id, row.CreatedUtc, payload, PreviewOf(payload), row.IsPinned);
                    _items.Add(entry); _digests[entry.Id] = Digest(payload);
                }
                finally { CryptographicOperations.ZeroMemory(row.Plaintext); }
            }
            await EnforceBoundsAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            PublishSnapshot();
        }
        finally { _gate.Release(); }
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called only after the Windows privacy policy permits access and the user enabled history.</summary>
    public async Task<bool> CaptureAsync(ClipboardPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!Options.ExplicitlyEnabled || Options.IsPaused) return false;
        if (!IsValid(payload, out var json)) { CryptographicOperations.ZeroMemory(json); return false; }
        var digest = Digest(payload);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var duplicate = _items.FirstOrDefault(i => _digests.TryGetValue(i.Id, out var prior) && CryptographicOperations.FixedTimeEquals(prior, digest));
            if (duplicate is not null)
            {
                _items.Remove(duplicate);
                var renewed = duplicate with { CreatedUtc = now };
                _items.Insert(0, renewed);
                await _repository.UpsertAsync(renewed.Id, now, renewed.IsPinned, json, cancellationToken).ConfigureAwait(false);
                _digests[renewed.Id] = digest;
            }
            else
            {
                var entry = new ClipboardEntry(Guid.NewGuid(), now, payload, PreviewOf(payload), false);
                _items.Insert(0, entry); _digests[entry.Id] = digest;
                await _repository.UpsertAsync(entry.Id, now, false, json, cancellationToken).ConfigureAwait(false);
            }
            await EnforceBoundsAsync(now, cancellationToken).ConfigureAwait(false);
            PublishSnapshot();
        }
        finally { _gate.Release(); CryptographicOperations.ZeroMemory(json); }
        ItemsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public IReadOnlyList<ClipboardEntry> Search(string? query)
    {
        var needle = query?.Trim();
        if (string.IsNullOrEmpty(needle)) return Items;
        return _snapshot.Where(item => item.Preview.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ||
            (item.Payload.Text?.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
            (item.Payload.Files?.Any(path => path.Contains(needle, StringComparison.CurrentCultureIgnoreCase)) ?? false)).ToArray();
    }

    /// <summary>Applies updated count/age settings immediately; old unpinned rows are removed before pinned rows.</summary>
    public async Task PruneAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await EnforceBoundsAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false); PublishSnapshot(); }
        finally { _gate.Release(); }
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> PinAsync(Guid id, bool pinned, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _items.FindIndex(x => x.Id == id); if (index < 0) return false;
            if (pinned && _items.Count(x => x.IsPinned) >= Math.Clamp(Options.MaxPinnedItems, 0, 200)) return false;
            _items[index] = _items[index] with { IsPinned = pinned };
            await _repository.SetPinnedAsync(id, pinned, cancellationToken).ConfigureAwait(false);
            await EnforceBoundsAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            PublishSnapshot();
        }
        finally { _gate.Release(); }
        ItemsChanged?.Invoke(this, EventArgs.Empty); return true;
    }

    public ClipboardPayload? GetPayload(Guid id) => _snapshot.FirstOrDefault(x => x.Id == id)?.Payload;
    public Task<bool> RestoreAsync(Guid id, Func<ClipboardPayload, CancellationToken, Task> restore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restore);
        var entry = _snapshot.FirstOrDefault(x => x.Id == id); if (entry is null) return Task.FromResult(false);
        return RestoreCoreAsync(entry.Payload, restore, cancellationToken);
    }
    private static async Task<bool> RestoreCoreAsync(ClipboardPayload payload, Func<ClipboardPayload, CancellationToken, Task> restore, CancellationToken ct)
    { await restore(payload, ct).ConfigureAwait(false); return true; }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _items.RemoveAll(x => x.Id == id); Forget(id); await _repository.DeleteAsync(id, cancellationToken).ConfigureAwait(false); PublishSnapshot(); }
        finally { _gate.Release(); }
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _items.Clear(); foreach (var digest in _digests.Values) CryptographicOperations.ZeroMemory(digest); _digests.Clear(); await _repository.ClearAsync(cancellationToken).ConfigureAwait(false); PublishSnapshot(); }
        finally { _gate.Release(); }
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task EnforceBoundsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var maxAge = Options.MaxAge < TimeSpan.Zero ? TimeSpan.Zero : Options.MaxAge;
        var maxItems = Math.Clamp(Options.MaxItems, 0, 500);
        var remove = _items.Where(x => x.CreatedUtc < now - maxAge).Select(x => x.Id).ToHashSet();
        while (_items.Count - remove.Count > maxItems)
        {
            var trim = _items.LastOrDefault(x => !x.IsPinned && !remove.Contains(x.Id)) ?? _items.LastOrDefault(x => !remove.Contains(x.Id));
            if (trim is null) break;
            remove.Add(trim.Id);
        }
        var retainedBytes = _items.Where(x => !remove.Contains(x.Id)).Sum(x => MemoryBytes(x.Payload));
        var budget = Math.Clamp(Options.MaxTotalPayloadBytes, 1024, 128L * 1024 * 1024);
        while (retainedBytes > budget)
        {
            var trim = _items.LastOrDefault(x => !x.IsPinned && !remove.Contains(x.Id)) ?? _items.LastOrDefault(x => !remove.Contains(x.Id));
            if (trim is null) break;
            remove.Add(trim.Id); retainedBytes -= MemoryBytes(trim.Payload);
        }
        if (remove.Count == 0) return;
        _items.RemoveAll(x => remove.Contains(x.Id)); foreach (var id in remove) Forget(id);
        await _repository.DeleteManyAsync(remove, ct).ConfigureAwait(false);
    }

    private void Forget(Guid id) { if (_digests.Remove(id, out var digest)) CryptographicOperations.ZeroMemory(digest); }
    private void PublishSnapshot() => _snapshot = _items.ToArray();
    private bool IsValid(ClipboardPayload p, out byte[] json)
    {
        json = [];
        switch (p.Kind)
        {
            case ClipboardPayloadKind.Text when p.Text is { Length: > 0 } text:
                if (Encoding.UTF8.GetByteCount(text) > Math.Clamp(Options.MaxPayloadBytes, 1024, 32 * 1024 * 1024)) return false;
                break;
            case ClipboardPayloadKind.Files when p.Files is { Count: > 0 and <= 256 } files:
                if (files.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 32_768)) return false;
                break;
            case ClipboardPayloadKind.Png when p.Png is { Length: > 8 and <= 16 * 1024 * 1024 } png:
                if (!png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
                break;
            default: return false;
        }
        if (MemoryBytes(p) > Math.Clamp(Options.MaxTotalPayloadBytes, 1024, 128L * 1024 * 1024)) return false;
        json = Encode(p);
        return json.Length <= Math.Clamp(Options.MaxPayloadBytes, 1024, 32 * 1024 * 1024);
    }
    private static long MemoryBytes(ClipboardPayload payload) => payload.Kind switch
    {
        ClipboardPayloadKind.Png => payload.Png!.LongLength,
        ClipboardPayloadKind.Text => Math.Max(payload.Text!.Length * 2L, Encoding.UTF8.GetByteCount(payload.Text)),
        ClipboardPayloadKind.Files => payload.Files!.Sum(path => Math.Max(path.Length * 2L, Encoding.UTF8.GetByteCount(path))),
        _ => 0
    };

    private static byte[] Encode(ClipboardPayload p) => JsonSerializer.SerializeToUtf8Bytes(new PayloadDto(p.Kind, p.Text, p.Files, p.Png));
    private static ClipboardPayload Decode(byte[] data)
    {
        var dto = JsonSerializer.Deserialize<PayloadDto>(data) ?? throw new InvalidDataException("Clipboard entry is invalid.");
        return dto.Kind switch { ClipboardPayloadKind.Text => ClipboardPayload.FromText(dto.Text ?? ""), ClipboardPayloadKind.Files => ClipboardPayload.FromFiles(dto.Files ?? []), ClipboardPayloadKind.Png => ClipboardPayload.FromPng(dto.Png ?? []), _ => throw new InvalidDataException("Unknown clipboard entry type.") };
    }
    private static byte[] Digest(ClipboardPayload p)
    {
        var data = p.Kind == ClipboardPayloadKind.Png ? p.Png! : p.Kind == ClipboardPayloadKind.Text ? Encoding.UTF8.GetBytes("text\0" + p.Text) : Encoding.UTF8.GetBytes("files\0" + string.Join("\0", p.Files!.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase)));
        try { return SHA256.HashData(data); } finally { if (p.Kind != ClipboardPayloadKind.Png) CryptographicOperations.ZeroMemory(data); }
    }
    private static string PreviewOf(ClipboardPayload p)
    {
        if (p.Kind == ClipboardPayloadKind.Text)
        {
            var text = (p.Text ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
            return text.Length > 240 ? text[..240] : text;
        }
        if (p.Kind == ClipboardPayloadKind.Files) return string.Join("; ", p.Files!.Select(Path.GetFileName).Take(5));
        return p.Kind == ClipboardPayloadKind.Png ? "Image" : "";
    }
    private sealed record PayloadDto(ClipboardPayloadKind Kind, string? Text, IReadOnlyList<string>? Files, byte[]? Png);
    public async ValueTask DisposeAsync() { foreach (var digest in _digests.Values) CryptographicOperations.ZeroMemory(digest); _digests.Clear(); await _repository.DisposeAsync().ConfigureAwait(false); _gate.Dispose(); }
}
