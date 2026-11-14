namespace CodexAccountSwitcher.Core;

/// <summary>
/// Opt-in V2 observed-frame to authenticated PUT pipeline. The existing
/// Windows app does not instantiate this class. Its event callback only
/// schedules a bounded task and never performs blocking network work.
/// All pending attempts are cancelled on ANY account state transition.
/// </summary>
public sealed class VerifiedQuotaV2PublishRuntimeAdapter : IDisposable
{
    private readonly object sync = new();
    private readonly IVerifiedQuotaEventSource source;
    private readonly QuotaRelayV2HttpsPublisher publisher;
    private readonly VerifiedQuotaV2EventBridge dataProjection;
    private CancellationTokenSource epoch = new();
    private bool disposed;
    private int inFlight;
    private Task? pending;

    public VerifiedQuotaV2PublishRuntimeAdapter(
        IVerifiedQuotaEventSource source,
        string enrolledSourceDevice,
        QuotaRelayV2HttpsPublisher publisher,
        Func<DateTimeOffset>? trustedClock = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        dataProjection = new VerifiedQuotaV2EventBridge(
            source, enrolledSourceDevice, trustedClock);
        dataProjection.FrameReady += OnFrameReady;
        source.Changed += OnAccountChanged;
        source.SwitchRequested += OnSwitchRequested;
    }

    /// <summary>
    /// Solely for offline synthetic integration verification.
    /// Production startup never creates this adapter or enables transmission.
    /// </summary>
    public Task? LastAttempt
    {
        get { lock (sync) return pending; }
    }

    private void OnFrameReady(string frame)
    {
        lock (sync)
        {
            if (disposed || epoch.IsCancellationRequested ||
                Interlocked.CompareExchange(ref inFlight, 1, 0) != 0)
                return;
            var token = epoch.Token;
            // Task.Run guarantees no HTTP body is sent synchronously from
            // the upstream SwiftHost monitor callback under its lock.
            pending = Task.Run(async () =>
            {
                try
                {
                    if (!token.IsCancellationRequested)
                        await publisher.PublishOnceAsync(frame, token)
                            .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // A switched account invalidates this one-shot attempt.
                }
                catch (Exception)
                {
                    // Supplemental monitor: never log raw quota, credentials,
                    // bearer headers, account hashes or remote URLs.
                }
                finally
                {
                    Interlocked.Exchange(ref inFlight, 0);
                }
            });
        }
    }

    private void OnAccountChanged()
    {
        // Even if the active profile UUID stays unchanged, a provider
        // identity/authorization epoch might have changed. Fail closed.
        lock (sync)
        {
            if (!disposed) Invalidate();
        }
    }

    private void OnSwitchRequested()
    {
        lock (sync)
        {
            if (!disposed) Invalidate();
        }
    }

    private void Invalidate()
    {
        var previous = epoch;
        epoch = new CancellationTokenSource();
        previous.Cancel();
        // Do not reuse an observation across identity epochs.
        previous.Dispose();
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            source.Changed -= OnAccountChanged;
            source.SwitchRequested -= OnSwitchRequested;
            dataProjection.FrameReady -= OnFrameReady;
            epoch.Cancel();
            epoch.Dispose();
        }
        dataProjection.Dispose();
        // The injected publisher is owned by its caller and not disposed here.
    }
}
