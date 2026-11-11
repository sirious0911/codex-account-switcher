namespace CodexAccountSwitcher.Core;

/// <summary>
/// Non-operating, in-memory bridge between the EXISTING Windows SwiftHost
/// observation stream and the strict P3 V2 active-profile data encoder.
/// This class never starts a process, opens a socket, stores credentials,
/// accesses Telegram or publishes a quota frame to an external destination.
/// Production startup does not construct it.
/// </summary>
public sealed class VerifiedQuotaV2EventBridge : IDisposable
{
    private readonly object sync = new();
    private readonly IVerifiedQuotaEventSource source;
    private readonly VerifiedActiveQuotaV2FrameEncoder encoder;
    private readonly Func<DateTimeOffset> trustedClock;
    private bool disposed;
    private long framesProduced;

    /// <summary>
    /// A synchronous, in-memory notification containing only a sanitized V2
    /// view frame. No HTTP publisher, queue or retry is provided by this type.
    /// Do not attach external transport until a separate operating Gate.
    /// </summary>
    public event Action<string>? FrameReady;

    public long FramesProduced
    {
        get { lock (sync) return framesProduced; }
    }

    public VerifiedQuotaV2EventBridge(
        IVerifiedQuotaEventSource source,
        string enrolledSourceDeviceId,
        Func<DateTimeOffset>? clock = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        encoder = new VerifiedActiveQuotaV2FrameEncoder(enrolledSourceDeviceId);
        trustedClock = clock ?? (() => DateTimeOffset.UtcNow);
        source.Changed += OnStateChanged;
        source.SwitchRequested += OnSwitchRequested;
        source.MonitorObserved += OnMonitorObserved;
        OnStateChanged();
    }

    private void OnStateChanged()
    {
        lock (sync)
        {
            if (disposed) return;
            try
            {
                encoder.ObserveSnapshot(source.State, trustedClock());
            }
            catch (Exception)
            {
                // A broken monitor must not interrupt the main account UI.
                // No logs may contain a profile or provider identifier.
                encoder.SwitchRequested(trustedClock());
            }
        }
    }

    private void OnSwitchRequested()
    {
        lock (sync)
        {
            if (disposed) return;
            encoder.SwitchRequested(trustedClock());
        }
    }

    private void OnMonitorObserved(CodexMonitorWireSample sample)
    {
        lock (sync)
        {
            if (disposed || FrameReady is null) return;
            try
            {
                if (!encoder.TryBuild(sample, trustedClock(), out var frame) ||
                    frame is null)
                    return;
                framesProduced++;
                // This event is intentionally synchronous so the active
                // identity cannot be switched in between validation and
                // callback invocation. No asynchronous send may occur here.
                FrameReady?.Invoke(frame);
            }
            catch (Exception)
            {
                // Supplemental monitoring must never break SwiftHost UI.
                // One event is consumed once; no transport retry exists.
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            FrameReady = null;
            source.Changed -= OnStateChanged;
            source.SwitchRequested -= OnSwitchRequested;
            source.MonitorObserved -= OnMonitorObserved;
        }
    }
}
