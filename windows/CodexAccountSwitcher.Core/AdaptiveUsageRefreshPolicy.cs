namespace CodexAccountSwitcher.Core;

/// <summary>
/// Schedules only additional foreground/recent-session usage reads.
/// The Swift account controller remains the sole owner of the normal
/// 300-second poll, all real requests, and account-switch refreshes.
///
/// No account data, disk state, network request, or credential is owned here.
/// </summary>
public sealed class AdaptiveUsageRefreshPolicy
{
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ResultGrace = TimeSpan.FromSeconds(40);
    public static readonly TimeSpan BaselineInterval = TimeSpan.FromMinutes(5);

    private Guid? activeProfile;
    private DateTimeOffset baselineAt;
    private DateTimeOffset? lastFreshAt;
    private DateTimeOffset? lastExtraRequestAt;
    private bool waitingForObservation;
    private int missedObservations;

    public AdaptiveUsageRefreshPolicy(DateTimeOffset startedAt)
    {
        baselineAt = startedAt;
    }

    public int ConsecutiveMisses => missedObservations;
    public Guid? ActiveProfile => activeProfile;
    public DateTimeOffset? LastExtraRequestAt => lastExtraRequestAt;

    public static TimeSpan IntervalForMisses(int misses) => misses switch
    {
        <= 0 => ActiveInterval,
        1 => TimeSpan.FromMinutes(2),
        2 => TimeSpan.FromMinutes(4),
        _ => BaselineInterval
    };

    public bool TrySchedule(DateTimeOffset now, bool hasRecentCodexWork, AccountSnapshot snapshot)
    {
        var allowed = !snapshot.IsMutating && !snapshot.IsAddingAccount &&
            snapshot.ActiveIdentityConfirmed && string.IsNullOrEmpty(snapshot.Error);
        Guid? selected = allowed ? snapshot.ActiveAccountID : null;
        if (activeProfile != selected)
        {
            activeProfile = selected;
            baselineAt = now;
            lastFreshAt = null;
            lastExtraRequestAt = null;
            waitingForObservation = false;
            missedObservations = 0;
        }

        // No additional request during ambiguous or mutating account state.
        // No additional request merely because Codex is installed/running.
        if (selected is null || !hasRecentCodexWork) return false;

        // Swift may still be fetching another profile. Wait for a fresh
        // observation, not merely the completion ACK for the refresh command.
        if (waitingForObservation)
        {
            if (lastExtraRequestAt is not DateTimeOffset requestAt ||
                now - requestAt < ResultGrace) return false;
            waitingForObservation = false;
            missedObservations = Math.Min(missedObservations + 1, 3);
        }

        var interval = IntervalForMisses(missedObservations);
        DateTimeOffset anchor = baselineAt;
        if (lastFreshAt is DateTimeOffset fresh && fresh > anchor) anchor = fresh;
        if (lastExtraRequestAt is DateTimeOffset extra && extra > anchor) anchor = extra;

        if (now - anchor < interval) return false;

        lastExtraRequestAt = now;
        waitingForObservation = true;
        return true;
    }

    /// <summary>Only call after the existing verified active-account gate accepts a fresh sample.</summary>
    public void Observed(Guid profileID, DateTimeOffset now)
    {
        if (activeProfile != profileID) return;
        lastFreshAt = now;
        waitingForObservation = false;
        missedObservations = 0;
    }

    public void RequestTransportFailed(DateTimeOffset now)
    {
        if (!waitingForObservation || lastExtraRequestAt is not DateTimeOffset at ||
            now < at) return;
        waitingForObservation = false;
        missedObservations = Math.Min(missedObservations + 1, 3);
    }
}
