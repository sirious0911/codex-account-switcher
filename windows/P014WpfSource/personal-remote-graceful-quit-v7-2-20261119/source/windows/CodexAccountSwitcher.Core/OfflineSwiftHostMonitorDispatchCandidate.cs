namespace CodexAccountSwitcher.Core;

/// <summary>
/// OFFLINE, synthetic-only proposed source dispatcher for a SwiftHost stdout
/// monitorObservation JSON line. Existing CoreClient, WPF App, SQLite,
/// account credentials, network devices and Telegram are never touched.
/// A caller separately supplies validated synthetic AccountSnapshot epochs.
/// Does NOT handle snapshot/completed/platform messages; their operating
/// dispatch is expressly out of scope. Never use as a real transport.
/// </summary>
public sealed class OfflineSwiftHostMonitorDispatchCandidate
{
    private readonly object gate=new();
    private readonly OfflineMonitorViewerMiniBarPipeline pipeline;
    private readonly Action<QuotaViewerMiniBarPreview> previewObserver;
    private bool hostClosed;

    public OfflineSwiftHostMonitorDispatchCandidate(
        string sourceDeviceId,string syntheticIdentityKeyHex,
        Action<QuotaViewerMiniBarPreview> previewObserver)
    {
        ArgumentNullException.ThrowIfNull(previewObserver);
        pipeline=new OfflineMonitorViewerMiniBarPipeline(
            sourceDeviceId,syntheticIdentityKeyHex);
        this.previewObserver=previewObserver;
    }

    /// <summary>
    /// Explicitly simulates a user-initiated account switch. Emits one
    /// redacted invalidated preview to the synthetic observer. A subsequent
    /// synthetic mutation and settled snapshot are required to reopen.
    /// </summary>
    public OfflineSwiftHostDispatchResult BeginSwitch(long trustedNowUtcMs)
    {
        lock(gate)
        {
            if(hostClosed)return Result("HOLD_SWIFTHOST_CLOSED");
            var decision=pipeline.BeginSwitch(trustedNowUtcMs);
            return NotifyInvalidated(decision.Status,trustedNowUtcMs);
        }
    }

    /// <summary>Pure account-model injection; no real host/account lookup.
    /// Does not notify an observer until a new monitor frame is validated.
    /// On snapshot error, gate reads are suppressed by the pipeline.
    /// </summary>
    public OfflineSwiftHostDispatchResult ObserveSnapshot(
        AccountSnapshot snapshot,long trustedNowUtcMs,
        string syntheticSessionId)
    {
        lock(gate)
        {
            if(hostClosed)return Result("HOLD_SWIFTHOST_CLOSED");
            var decision=pipeline.ObserveSnapshot(
                snapshot,trustedNowUtcMs,syntheticSessionId);
            if(decision.Status=="OFFLINE_ACTIVE_PROFILE_READY_NO_SEND")
                return Result(decision.Status);
            // Explicit invalidation on host-account error/mutation, only
            // if there was a previously observable view. An invalid state
            // must never continue the former account's taskbar tooltip.
            return NotifyInvalidated(decision.Status,trustedNowUtcMs);
        }
    }

    /// <summary>
    /// Strict full-envelope JSON check comes BEFORE any active profile
    /// bridge, cache, UI-formatted model, or observer callback. Never log
    /// Sample.RawJson / accountId. Non-monitor messages are not handled
    /// here and must remain on CoreClient's separate operating path.
    /// </summary>
    public OfflineSwiftHostDispatchResult AcceptMonitorLine(
        string? rawStdoutLine,long trustedNowUtcMs)
    {
        lock(gate)
        {
            if(hostClosed)return Result("HOLD_SWIFTHOST_CLOSED");
            var inspected=SwiftHostStrictEnvelopeV1Review.Inspect(
                rawStdoutLine);
            if(inspected.Status!="OFFLINE_STRICT_WIRE_CANDIDATE_ONLY"||
               inspected.Sample is null)
                return Result(inspected.Status);
            var accepted=pipeline.TryIngest(
                inspected.Sample,trustedNowUtcMs);
            if(accepted.Status=="HOLD_PROVIDER_ACCOUNT_MIXED_IN_ACTIVE_EPOCH")
                return NotifyInvalidated(accepted.Status,trustedNowUtcMs);
            if(accepted.Status!="OFFLINE_ACTIVE_MONITOR_VIEW_STORED_CANDIDATE")
                return Result(accepted.Status);
            var view=pipeline.ReadPreview(
                DateTimeOffset.FromUnixTimeMilliseconds(trustedNowUtcMs));
            if(view.PreviewStatus!="OFFLINE_VIEW_FORMAT_PREVIEW_ONLY"||
               view.Readout.Status is not
                   (MiniBarDisplayStatus.FreshUnbound or
                    MiniBarDisplayStatus.AgedUnbound))
                return Result("HOLD_DISPATCH_VIEW_NOT_CURRENT");
            return Notify(
                "OFFLINE_MONITOR_PREVIEW_DELIVERED_NO_SEND",view,
                trustedNowUtcMs);
        }
    }

    /// <summary>
    /// Explicit synthetic host termination/read failure. Fail closed and
    /// stay permanently closed for this instance. Only a NEW isolated
    /// dispatcher instance can represent a restarted synthetic host.
    /// No automatic reconnect or replay of a previously accepted sample.
    /// </summary>
    public OfflineSwiftHostDispatchResult HostTerminated(long trustedNowUtcMs)
    {
        lock(gate)
        {
            if(hostClosed)return Result("HOLD_SWIFTHOST_CLOSED");
            hostClosed=true;
            pipeline.BeginSwitch(trustedNowUtcMs);
            var candidate=pipeline.ReadPreview(
                DateTimeOffset.FromUnixTimeMilliseconds(trustedNowUtcMs));
            try
            {
                previewObserver(candidate);
                return new("OFFLINE_HOST_TERMINATED_PREVIEW_INVALIDATED",
                    true);
            }
            catch(Exception)
            {
                // An offline test observer can fail; no raw data, exception
                // text, or account identity is returned or exposed.
                return Result("HOLD_OFFLINE_OBSERVER_FAILED");
            }
        }
    }

    /// <summary>Never exposes source raw account identifier or auth key.</summary>
    public QuotaViewerMiniBarPreview ReadPreview(DateTimeOffset trustedNow)
    {
        lock(gate)
        {
            return pipeline.ReadPreview(trustedNow);
        }
    }

    public bool IsHostClosed
    {
        get { lock(gate){return hostClosed;} }
    }

    private OfflineSwiftHostDispatchResult NotifyInvalidated(
        string reason,long trustedNowUtcMs)
    {
        var candidate=pipeline.ReadPreview(
            DateTimeOffset.FromUnixTimeMilliseconds(trustedNowUtcMs));
        // A bad AccountSnapshot need not alter underlying Gate.Selected
        // until the gate processes it. But no stale data may reach observer.
        if(candidate.Readout.Status is MiniBarDisplayStatus.FreshUnbound or
           MiniBarDisplayStatus.AgedUnbound)
            return Result(reason);
        return Notify(reason,candidate,trustedNowUtcMs);
    }

    private OfflineSwiftHostDispatchResult Notify(
        string status,QuotaViewerMiniBarPreview candidate,
        long trustedNowUtcMs)
    {
        try
        {
            previewObserver(candidate);
            return new(status,true);
        }
        catch(Exception)
        {
            // Do not retransmit automatically after observer failure.
            hostClosed=true;
            pipeline.BeginSwitch(trustedNowUtcMs);
            return Result("HOLD_OFFLINE_OBSERVER_FAILED");
        }
    }

    private static OfflineSwiftHostDispatchResult Result(string status)=>
        new(status,false);

    public bool RealAccountReadAuthorized=>false;
    public bool SenderTransitionAuthorized=>false;
    public bool ActualTelegramSendAuthorized=>false;
    public bool LiveCoreClientConnected=>false;
    public bool RealCrossHostSyncAuthorized=>false;
}

public sealed record OfflineSwiftHostDispatchResult(
    string Status,bool ObserverCalled)
{
    public bool ActualSendAuthorized=>false;
    public bool AutomaticRetryAllowed=>false;
    public bool ProductionReservationAuthorized=>false;
    public bool RealDeviceAuthenticated=>false;
    public bool TelegramAllowed=>false;
    public bool LiveUiConnected=>false;
}
