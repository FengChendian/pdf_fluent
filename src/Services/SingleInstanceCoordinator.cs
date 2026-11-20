using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace pdf_fluent.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Semaphore _instanceSemaphore;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listenerTask;
    private bool _disposed;

    public SingleInstanceCoordinator() : this($"FluentPDF.{Process.GetCurrentProcess().SessionId}")
    {
    }

    internal SingleInstanceCoordinator(string instanceName)
    {
        _pipeName = $"{instanceName}.pipe";
        _instanceSemaphore = new Semaphore(1, 1, $@"Local\{instanceName}.single-instance", out bool isPrimary);
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    public void StartListening(Func<string[], Task> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimary) throw new InvalidOperationException("Only the primary instance can listen.");
        if (_listenerTask is not null) throw new InvalidOperationException("The listener has already started.");
        _listenerTask = Task.Run(() => ListenAsync(handler));
    }

    public async Task<bool> TrySendAsync(string[] args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPrimary) throw new InvalidOperationException("The primary instance cannot send to itself.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                await using var client = new NamedPipeClientStream(
                    ".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                try
                {
                    await client.ConnectAsync(200, timeout.Token);
                    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(args);
                    await client.WriteAsync(payload, timeout.Token);
                    return true;
                }
                catch (TimeoutException)
                {
                }
                catch (IOException)
                {
                    await Task.Delay(100, timeout.Token);
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync(Func<string[], Task> handler)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_shutdown.Token);
                byte[] payload = new byte[4096];
                using var payloadStream = new MemoryStream();
                int bytesRead;
                while ((bytesRead = await server.ReadAsync(payload, _shutdown.Token)) > 0)
                    payloadStream.Write(payload, 0, bytesRead);
                string[]? args = JsonSerializer.Deserialize<string[]>(payloadStream.ToArray());
                if (args is not null) await handler(args);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Single-instance listener failed: {ex}");
                await Task.Delay(100, _shutdown.Token);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        if (_listenerTask is not null)
        {
            try
            {
                _listenerTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
        _shutdown.Dispose();
        _instanceSemaphore.Dispose();
    }
}
