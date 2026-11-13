using AIOPatcher.Contracts;
using Microsoft.Extensions.Logging;

namespace AIOPatcher.Agent.Jobs;

/// <summary>
/// Sammelt Logzeilen eines Jobs und schickt sie alle paar Sekunden gebündelt an den Server.
/// Ist der Server kurz weg, bleiben die Zeilen im Puffer und gehen mit dem nächsten Versuch raus.
/// </summary>
public sealed class JobLog : IAsyncDisposable
{
    private const int MaxBufferedLines = 20000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly ServerClient _client;
    private readonly Guid _jobId;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly List<JobLogLine> _buffer = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Task _flushLoop;
    private int _dropped;

    public JobLog(ServerClient client, Guid jobId, ILogger logger)
    {
        _client = client;
        _jobId = jobId;
        _logger = logger;
        _flushLoop = Task.Run(FlushLoopAsync);
    }

    public void Write(string text)
    {
        lock (_lock)
        {
            if (_buffer.Count >= MaxBufferedLines)
            {
                _dropped++;
                return;
            }

            _buffer.Add(new JobLogLine(DateTime.UtcNow, text));
        }
    }

    /// <summary>Alles Gepufferte jetzt senden (z. B. direkt vor einem Neustart).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        // Nur ein Flush gleichzeitig, sonst kommen Zeilen in falscher Reihenfolge an.
        await _flushGate.WaitAsync(ct);
        try
        {
            await FlushCoreAsync(ct);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task FlushCoreAsync(CancellationToken ct)
    {
        List<JobLogLine> batch;
        lock (_lock)
        {
            if (_dropped > 0)
            {
                _buffer.Add(new JobLogLine(DateTime.UtcNow, $"[agent] {_dropped} Logzeilen verworfen (Puffer voll)."));
                _dropped = 0;
            }

            if (_buffer.Count == 0)
            {
                return;
            }

            batch = [.. _buffer];
            _buffer.Clear();
        }

        try
        {
            foreach (var chunk in batch.Chunk(1000))
            {
                await _client.AppendJobLogAsync(_jobId, new JobLogRequest(chunk), ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or ServerRejectedException or TaskCanceledException)
        {
            _logger.LogWarning("Job-Log konnte nicht gesendet werden: {Message}", ex.Message);
            lock (_lock)
            {
                _buffer.InsertRange(0, batch);
            }
        }
    }

    private async Task FlushLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(FlushInterval, _stop.Token);
                await FlushAsync(_stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _flushLoop;
        using var finalFlush = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await FlushAsync(finalFlush.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Job-Log konnte vor Ende nicht vollständig gesendet werden.");
        }

        _stop.Dispose();
        _flushGate.Dispose();
    }
}
