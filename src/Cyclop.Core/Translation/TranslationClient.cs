using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cyclop.Core.Translation;

/// <summary>Owns the lazy offline worker process and its UTF-8 JSON Lines protocol.</summary>
public sealed class TranslationClient : IDisposable
{
    private const int MaxOutstandingRequests = 8;
    private const int MaxResponseCharacters = 8 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);
    private readonly string _workerExe;
    private readonly string _modelsRoot;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
    private Process? _process;
    private int _outstanding;
    private int _disposed;

    public TranslationClient(string workerExe, string modelsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExe);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        _workerExe = Path.GetFullPath(workerExe);
        _modelsRoot = Path.GetFullPath(modelsRoot);
    }

    public async Task<string> TranslateAsync(string text, string direction, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(text);
        if (direction is not ("en-ru" or "ru-en"))
            throw new ArgumentOutOfRangeException(nameof(direction), "Direction must be 'en-ru' or 'ru-en'.");
        if (Encoding.UTF8.GetByteCount(text) > 2 * 1024 * 1024)
            throw new ArgumentException("Text exceeds the 2 MiB worker request limit.", nameof(text));
        if (Interlocked.Increment(ref _outstanding) > MaxOutstandingRequests)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("The offline translator queue is full.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var process = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, new PendingRequest(process, completion))) throw new InvalidOperationException("Could not register translation request.");
            var requestSent = false;

            try
            {
                await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (process.HasExited) throw new IOException("The translation worker exited before the request was sent.");
                    var request = JsonSerializer.Serialize(new WorkerRequest(id, text, direction));
                    requestSent = true; // A canceled write may already have emitted a partial JSON line.
                    await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Invalidate(process, new IOException("The worker was restarted after a canceled request."));
                    throw;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    Invalidate(process, ex);
                    throw;
                }
                finally { _writeGate.Release(); }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);
                WorkerResponse response;
                try { response = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Translation inference itself is synchronous in the worker.
                    // Kill this process generation so canceled UI input cannot
                    // accumulate behind a long request; the next call restarts it.
                    if (requestSent) Invalidate(process, new IOException("The worker was restarted after a canceled request."));
                    throw;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Invalidate(process, new TimeoutException("The offline translation timed out."));
                    throw new TimeoutException("The offline translation timed out.");
                }

                if (response.Error is not null) throw new InvalidOperationException(response.Error);
                return response.Result ?? string.Empty;
            }
            finally { _pending.TryRemove(id, out _); }
        }
        finally { Interlocked.Decrement(ref _outstanding); }
    }

    private async Task<Process> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_process is { HasExited: false } running) return running;
            _process?.Dispose();
            if (!File.Exists(_workerExe)) throw new FileNotFoundException("The offline translation worker was not found.", _workerExe);
            Directory.CreateDirectory(_modelsRoot);

            var start = new ProcessStartInfo
            {
                FileName = _workerExe,
                WorkingDirectory = Path.GetDirectoryName(_workerExe)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            start.ArgumentList.Add(_modelsRoot);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            if (!process.Start())
            {
                process.Dispose();
                throw new IOException("The offline translation worker could not be started.");
            }
            _process = process;
            _ = ReadResponsesAsync(process);
            _ = DrainDiagnosticsAsync(process);
            _ = WatchExitAsync(process);
            return process;
        }
        finally { _startGate.Release(); }
    }

    private async Task ReadResponsesAsync(Process process)
    {
        try
        {
            var reader = new BoundedLineReader(process.StandardOutput, MaxResponseCharacters);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                try
                {
                    var response = JsonSerializer.Deserialize<WorkerResponse>(line);
                    if (response?.Id is not null && _pending.TryGetValue(response.Id, out var pending) && ReferenceEquals(pending.Process, process))
                        pending.Completion.TrySetResult(response);
                }
                catch (JsonException) { Invalidate(process, new IOException("The translation worker returned invalid protocol data.")); return; }
            }
            Invalidate(process, new IOException("The translation worker closed its output stream."));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        { Invalidate(process, ex); }
    }

    private static async Task DrainDiagnosticsAsync(Process process)
    {
        var buffer = new char[4096];
        try { while (await process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) > 0) { } }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
    }

    private async Task WatchExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            Invalidate(process, new IOException("The translation worker exited and will restart on the next request."));
        }
        catch (InvalidOperationException) { }
    }

    private void Invalidate(Process process, Exception error)
    {
        var wasCurrent = ReferenceEquals(Interlocked.CompareExchange(ref _process, null, process), process);
        foreach (var pair in _pending)
            if (ReferenceEquals(pair.Value.Process, process)) pair.Value.Completion.TrySetException(error);
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
        if (wasCurrent) process.Dispose();
        else
        {
            try { if (process.HasExited) process.Dispose(); }
            catch (InvalidOperationException) { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var process = Interlocked.Exchange(ref _process, null);
        if (process is not null)
        {
            foreach (var pair in _pending) pair.Value.Completion.TrySetException(new ObjectDisposedException(nameof(TranslationClient)));
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
            process.Dispose();
        }
        // Keep the small semaphores alive so in-flight methods can leave their
        // finally blocks safely after the process has been stopped.
    }

    private sealed record PendingRequest(Process Process, TaskCompletionSource<WorkerResponse> Completion);

    private sealed class BoundedLineReader(StreamReader reader, int maxCharacters)
    {
        private readonly char[] _buffer = new char[4096];
        private int _offset;
        private int _count;

        public async Task<string?> ReadLineAsync()
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_offset == _count)
                {
                    _count = await reader.ReadAsync(_buffer.AsMemory()).ConfigureAwait(false);
                    _offset = 0;
                    if (_count == 0) return line.Length == 0 ? null : line.ToString();
                }

                var newline = Array.IndexOf(_buffer, '\n', _offset, _count - _offset);
                var end = newline >= 0 ? newline : _count;
                var length = end - _offset;
                if (line.Length + length > maxCharacters)
                    throw new InvalidDataException("The translation worker response exceeded the protocol limit.");
                line.Append(_buffer, _offset, length);
                _offset = newline >= 0 ? newline + 1 : _count;
                if (newline >= 0)
                {
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    return line.ToString();
                }
            }
        }
    }

    private sealed record WorkerRequest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("direction")] string Direction);
    private sealed record WorkerResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("result")] string? Result,
        [property: JsonPropertyName("error")] string? Error);
}
