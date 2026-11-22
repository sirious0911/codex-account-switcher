namespace CodexAccountSwitcher.Core;

/// <summary>
/// Strictly synthetic, in-process, read-only UI-format preview for a single
/// preconfigured remote source. It does NOT feed TaskbarQuotaPanel.Accept,
/// replace IntegratedMiniBarState, authenticate a host, or grant Sender rights.
/// The real minibar remains gated by ActiveMonitorPublicationGate.
/// </summary>
public static class QuotaViewerMiniBarOfflineAdapter
{
    private static readonly TimeSpan PreviewFreshFor=TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PreviewDisplayFor=TimeSpan.FromMinutes(6);

    public static QuotaViewerMiniBarPreview Project(
        QuotaViewV1MemoryCache viewerCache,
        string sourceDeviceId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(viewerCache);
        return Project(viewerCache.Read(sourceDeviceId,
            now.ToUnixTimeMilliseconds()),sourceDeviceId,now);
    }

    /// <summary>
    /// Explicit read-only projection of an already reviewed V1 observation.
    /// Required for the fully detached synthetic monitor-to-minibar trial.
    /// The sourceDeviceId must still match: a different device's cached
    /// quota can never be displayed in this source's preview slot.
    /// </summary>
    public static QuotaViewerMiniBarPreview Project(
        QuotaViewV1Inspection review,string sourceDeviceId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(review);
        if(review.Status!="OFFLINE_VIEW_CANDIDATE_ONLY" || review.View is null)
            return Unknown(sourceDeviceId,review.Status);

        var view=review.View;
        if(!string.Equals(view.SourceDeviceId,sourceDeviceId,
                StringComparison.Ordinal))
            return Unknown(sourceDeviceId,"HOLD_SOURCE_DEVICE_CONFLICT");
        DateTimeOffset observed;
        try { observed=DateTimeOffset.FromUnixTimeMilliseconds(view.CollectedAtUtcMs); }
        catch(ArgumentOutOfRangeException) {
            return Unknown(sourceDeviceId,"HOLD_INVALID_VIEW_TIMESTAMP");
        }
        var age=now-observed;
        // Conservative copy of the CURRENT taskbar presentation lifetimes.
        // Viewer cache retention (10 min) is never permission to show a
        // 6+ minute remote observation in the real taskbar's readout.
        if(age<TimeSpan.Zero || age>=PreviewDisplayFor)
            return Unknown(sourceDeviceId,"HOLD_PREVIEW_EXPIRED");

        var status=age<PreviewFreshFor
            ?MiniBarDisplayStatus.FreshUnbound
            :MiniBarDisplayStatus.AgedUnbound;
        var frame=new MiniBarReadout(
            status,
            view.FiveHour.RemainingPercent,
            view.SevenDay.RemainingPercent,
            null, // untrusted remote views NEVER supply reset credits
            observed,
            WindowReset(view.FiveHour),
            WindowReset(view.SevenDay),
            null);
        return new(
            sourceDeviceId,"OFFLINE_VIEW_FORMAT_PREVIEW_ONLY",
            frame,TrayQuotaReadout.From(frame));
    }

    private static DateTimeOffset? WindowReset(QuotaViewV1Window window)
    {
        // Never imply that an unknown quota has a valid next reset.
        if(window.RemainingPercent is null ||
           window.ResetAtUtcMs is not long timestamp)
            return null;
        try{return DateTimeOffset.FromUnixTimeMilliseconds(timestamp);}
        catch(ArgumentOutOfRangeException){return null;}
    }

    private static QuotaViewerMiniBarPreview Unknown(
        string? sourceDeviceId,string reason)
    {
        var status=reason is "HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET"
            or "HOLD_PROFILE_SWITCH_OR_IDENTITY"
            or "HOLD_LOCAL_SWITCH_PENDING"
            ?MiniBarDisplayStatus.Invalidated
            :MiniBarDisplayStatus.Unknown;
        var empty=new MiniBarReadout(status,null,null,null,null);
        return new(sourceDeviceId??"",reason,empty,TrayQuotaReadout.From(empty));
    }
}

/// <summary>
/// Preview-only. Every capability flag stays false even with a fresh value.
/// This DTO never enters App.xaml.cs or the live mini-bar publication gate.
/// </summary>
public sealed record QuotaViewerMiniBarPreview(
    string SourceDeviceId,string PreviewStatus,
    MiniBarReadout Readout,TrayQuotaReadout Tray)
{
    public bool AccountVerified=>false;
    public bool EventsAllowed=>false;
    public bool TelegramAllowed=>false;
    public bool SenderTransitionAuthorized=>false;
    public bool RealDeviceAuthenticated=>false;
    public bool LiveUiConnected=>false;
}
