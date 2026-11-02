using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Sends the existing SwiftHost monitor observation to the independent Windows minibar.
/// No extra Codex request, credential read, persisted queue or external network connection.
/// </summary>
public sealed class SwitcherLocalPipeSender : IAsyncDisposable
{
    public const string DefaultPipeName = "codex-usage-monitor-switcher-v1";
    public const int MaxFrameBytes = 8192;

    private readonly string pipeName;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<ObservationFrame> pending =
        Channel.CreateBounded<ObservationFrame>(new BoundedChannelOptions(1)
        {
            SingleReader = false, // Invalidate() also drains the queue.
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly Task worker;
    private long lastObservedAt;
    private long minObservedAt;
    private long generation;
    private NamedPipeClientStream? currentConnection;
    private int disposed;

    private sealed record ObservationFrame(long ObservedAtMs, long Generation, byte[]? Bytes);

    public SwitcherLocalPipeSender(string pipeName = DefaultPipeName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Local IPC publisher requires Windows.");
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 100 ||
            pipeName.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_')))
            throw new ArgumentException("Invalid Windows pipe name.", nameof(pipeName));
        this.pipeName = pipeName;
        worker = Task.Run(RunAsync);
    }

    /// <summary>
    /// End the prior publisher epoch and disconnect the remote minibar immediately.
    /// This is invoked before account switching, when the selected profile changes,
    /// or whenever the active identity is no longer confirmed.
    /// </summary>
    public void Invalidate()
    {
        var nextGeneration = Interlocked.Increment(ref generation);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Interlocked.Exchange(ref minObservedAt, now);
        Interlocked.Exchange(ref lastObservedAt, now);
        while (pending.Reader.TryRead(out _)) { }
        // Wake the channel reader even when it is idle and no future usage arrives.
        pending.Writer.TryWrite(new ObservationFrame(0, nextGeneration, null));
        Interlocked.Exchange(ref currentConnection, null)?.Dispose();
    }

    /// <summary>
    /// Never blocks the UI on pipe discovery or writes. Returns false for stale,
    /// duplicate or oversize observations, leaving the core monitor unaffected.
    /// </summary>
    public bool Publish(CodexMonitorWireSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (Volatile.Read(ref disposed) != 0) return false;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var epoch = Interlocked.Read(ref generation);
        if (sample.ObservedAtMilliseconds <= Volatile.Read(ref minObservedAt) ||
            sample.ObservedAtMilliseconds <= 0 ||
            sample.ObservedAtMilliseconds < now - 15_000 ||
            sample.ObservedAtMilliseconds > now + 5_000)
            return false;

        byte[] bytes;
        try
        {
            using var raw = JsonDocument.Parse(sample.RawJson, new JsonDocumentOptions { MaxDepth = 12 });
            if (raw.RootElement.ValueKind != JsonValueKind.Object) return false;
            bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "monitorObservation",
                observedAtMs = sample.ObservedAtMilliseconds,
                raw = raw.RootElement
            });
        }
        catch (JsonException) { return false; }
        if (bytes.Length > MaxFrameBytes) return false;

        // Retain only the latest newly observed sample. No disk-backed outbox.
        while (true)
        {
            long prior = Volatile.Read(ref lastObservedAt);
            if (sample.ObservedAtMilliseconds <= prior) return false;
            if (Interlocked.CompareExchange(ref lastObservedAt, sample.ObservedAtMilliseconds, prior) == prior)
                break;
        }

        var terminated = new byte[bytes.Length + 1];
        bytes.CopyTo(terminated, 0);
        terminated[^1] = (byte)'\n';
        if (Interlocked.Read(ref generation) != epoch) return false;
        return pending.Writer.TryWrite(new ObservationFrame(sample.ObservedAtMilliseconds, epoch, terminated));
    }

    private async Task RunAsync()
    {
        var ct = lifetime.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                Interlocked.Exchange(ref currentConnection, pipe);
                try
                {
                    await pipe.ConnectAsync(500, ct).ConfigureAwait(false);
                    while (!ct.IsCancellationRequested)
                    {
                        var frame = await pending.Reader.ReadAsync(ct).ConfigureAwait(false);
                        if (frame.Bytes is null) break;
                        // A disconnected/replaced generation must never replay queued values.
                        if (frame.Generation != Interlocked.Read(ref generation) ||
                            frame.ObservedAtMs <= Volatile.Read(ref minObservedAt) ||
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - frame.ObservedAtMs > 15_000)
                            continue;
                        await pipe.WriteAsync(frame.Bytes, ct).ConfigureAwait(false);
                        await pipe.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Interlocked.CompareExchange(ref currentConnection, null, pipe);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ChannelClosedException) { break; }
            catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // No listener, pipe restart or permission mismatch: never block the switcher.
            }

            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Invalidate();
        lifetime.Cancel();
        pending.Writer.TryComplete();
        await worker.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
