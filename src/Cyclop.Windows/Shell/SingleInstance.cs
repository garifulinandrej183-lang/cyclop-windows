using System.IO.Pipes;
using System.Security.Principal;

namespace Cyclop.Windows.Shell;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();
    public bool IsPrimary { get; }
    public event Action? OpenRequested;
    public Func<string>? DiagnosticsRequested { get; set; }
    public SingleInstance()
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        _pipe = "CyclopWindows-" + sid;
        _mutex = new Mutex(true, @"Local\CyclopWindows-" + sid, out var created);
        IsPrimary = created;
        if (created) _ = ListenAsync();
    }
    public async Task SignalPrimaryAsync()
    {
        try { using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.Out);
            await pipe.ConnectAsync(1500); await pipe.WriteAsync(new byte[] { 1 }); }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
    }
    public async Task<string> GetDiagnosticsAsync()
    {
        using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut);
        await pipe.ConnectAsync(1500); await pipe.WriteAsync(new byte[] { 2 }); await pipe.FlushAsync();
        using var reader = new StreamReader(pipe);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                var signal = new byte[1];
                if (await server.ReadAsync(signal, _stop.Token) > 0)
                {
                    if (signal[0] == 2)
                    {
                        var bytes = System.Text.Encoding.UTF8.GetBytes(DiagnosticsRequested?.Invoke() ?? "{}");
                        await server.WriteAsync(bytes, _stop.Token); await server.FlushAsync(_stop.Token);
                    }
                    else OpenRequested?.Invoke();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { await Task.Delay(250, _stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
        }
    }
    public void Dispose() { _stop.Cancel(); if (IsPrimary) _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
