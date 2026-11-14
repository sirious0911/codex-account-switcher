using System;
using System.Diagnostics;
using System.Windows.Threading;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher;

/// <summary>
/// Windows-only adaptive addition to the existing 5-minute Swift timer:
/// 1-minute opportunistic refresh for recent session-file/foreground evidence,
/// with 2/4/5 minute retry backoff if no fresh active sample is observed.
/// Does not modify account identity, login, storage, or quota/reset policy.
/// </summary>
internal sealed class AdaptiveUsageRefreshService : IDisposable
{
    private readonly CoreClient client;
    private readonly CodexWorkActivityDetector workDetector;
    private readonly AdaptiveUsageRefreshPolicy policy;
    private readonly DispatcherTimer timer;
    private bool dispatching;
    private bool disposed;

    internal AdaptiveUsageRefreshService(CoreClient client, CodexWorkActivityDetector? detector = null)
    {
        this.client = client;
        workDetector = detector ?? new CodexWorkActivityDetector();
        policy = new AdaptiveUsageRefreshPolicy(DateTimeOffset.UtcNow);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        timer.Tick += OnTimer;
    }

    internal void Start() => timer.Start();

    internal void ObserveFreshActiveSample(Guid profileId)
    {
        if (disposed) return;
        policy.Observed(profileId, DateTimeOffset.UtcNow);
    }

    private async void OnTimer(object? sender, EventArgs args)
    {
        if (disposed || dispatching) return;
        var now = DateTimeOffset.UtcNow;
        var activeWork = workDetector.HasRecentWork(now);
        if (!policy.TrySchedule(now, activeWork, client.State)) return;

        dispatching = true;
        try
        {
            // The Swift core remains the only API caller. The new refreshActive
            // host command selects the confirmed active account after
            // synchronizing identity and resets the normal 5-minute timer.
            await client.CommandAsync("refreshActive");
        }
        catch (Exception error)
        {
            policy.RequestTransportFailed(DateTimeOffset.UtcNow);
            Trace.TraceWarning("Adaptive Codex refresh unavailable: " + error.GetType().Name);
        }
        finally { dispatching = false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
    }
}
