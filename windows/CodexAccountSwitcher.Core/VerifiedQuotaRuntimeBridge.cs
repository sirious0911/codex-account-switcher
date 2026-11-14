namespace CodexAccountSwitcher.Core;

/// <summary>
/// The events needed by an explicitly attached P3 telemetry observer.
/// The operating app does NOT attach this bridge until a separate deployment
/// decision; no default sender is created by this type.
/// </summary>
public interface IVerifiedQuotaEventSource
{
    AccountSnapshot State { get; }
    event Action? Changed;
    event Action<CodexMonitorWireSample>? MonitorObserved;
    event Action? SwitchRequested;
}

/// <summary>
/// Event-only adapter of the existing Windows CoreClient. It does not start
/// another SwiftHost process or request an account operation.
/// </summary>
public sealed class CoreClientQuotaEventAdapter(CoreClient client)
    : IVerifiedQuotaEventSource
{
    public AccountSnapshot State => client.State;
    public event Action? Changed
    {
        add => client.Changed += value;
        remove => client.Changed -= value;
    }
    public event Action<CodexMonitorWireSample>? MonitorObserved
    {
        add => client.MonitorObserved += value;
        remove => client.MonitorObserved -= value;
    }
    public event Action? SwitchRequested
    {
        add => client.SwitchRequested += value;
        remove => client.SwitchRequested -= value;
    }
}

/// <summary>
/// Bridges the existing Windows observation stream to the independently
/// authorized read-only dispatch callback. Profile changes are processed
/// synchronously, before a switch command can reach the Swift core.
/// </summary>
public sealed class VerifiedQuotaRuntimeBridge : IDisposable
{
    private readonly IVerifiedQuotaEventSource source;
    private readonly VerifiedQuotaDispatchCoordinator coordinator;
    private bool disposed;

    public VerifiedQuotaRuntimeBridge(
        IVerifiedQuotaEventSource source,
        VerifiedQuotaDispatchCoordinator coordinator)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.coordinator = coordinator ??
            throw new ArgumentNullException(nameof(coordinator));

        source.Changed += OnStateChanged;
        source.SwitchRequested += OnSwitchRequested;
        source.MonitorObserved += OnObservation;
        OnStateChanged();
    }

    private void OnStateChanged()
    {
        if (disposed) return;
        coordinator.ObserveSnapshot(source.State, DateTimeOffset.UtcNow);
    }

    private void OnSwitchRequested()
    {
        if (disposed) return;
        coordinator.SwitchRequested(DateTimeOffset.UtcNow);
    }

    private void OnObservation(CodexMonitorWireSample sample)
    {
        if (disposed) return;
        // The coordinator's single-flight gate prevents an unbounded queue.
        // A failed callback does not affect the existing local monitor.
        _ = coordinator.DispatchAsync(sample, DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        source.Changed -= OnStateChanged;
        source.SwitchRequested -= OnSwitchRequested;
        source.MonitorObserved -= OnObservation;
        coordinator.Dispose();
    }
}
