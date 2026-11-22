namespace CodexAccountSwitcher.Core;

/// <summary>
/// Schedules foreground activity and short pre/post scheduled reset checks.
/// The Swift account controller remains the sole owner of the normal
/// 300-second poll, all real requests, and account-switch refreshes.
///
/// No account data, disk state, network request, or credential is owned here.
/// </summary>
public sealed class AdaptiveUsageRefreshPolicy
{
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ResetWatchInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ResultGrace = TimeSpan.FromSeconds(40);
    public static readonly TimeSpan BaselineInterval = TimeSpan.FromMinutes(5);

    private Guid? activeProfile;
    private DateTimeOffset baselineAt;
    private DateTimeOffset? lastFreshAt;
    private DateTimeOffset? lastExtraRequestAt;
    private bool waitingForObservation;
    private int missedObservations;
    private DateTimeOffset? fiveResetAt;
    private DateTimeOffset? weeklyResetAt;
    public static readonly TimeSpan PreResetWatch = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan PostResetWatch = TimeSpan.FromMinutes(10);

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
        1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(2),
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
            fiveResetAt = null;
            weeklyResetAt = null;
        }

        // The scheduled window may request additional reads without active
        // Codex use. It never bypasses confirmed active-account identity.
        bool Near(DateTimeOffset? reset) => reset is DateTimeOffset at &&
            now >= at - PreResetWatch && now <= at + PostResetWatch;
        var watchingReset = Near(fiveResetAt) || Near(weeklyResetAt);
        if (selected is null || (!hasRecentCodexWork && !watchingReset)) return false;

        // Swift may still be fetching another profile. Wait for a fresh
        // observation, not merely the completion ACK for the refresh command.
        if (waitingForObservation)
        {
            if (lastExtraRequestAt is not DateTimeOffset requestAt ||
                now - requestAt < ResultGrace) return false;
            waitingForObservation = false;
            missedObservations = Math.Min(missedObservations + 1, 3);
        }

        // After a missing provider observation, preserve exponential backoff
        // even near reset. Never flood at 15-second intervals on errors.
        var interval = watchingReset && missedObservations == 0
            ? ResetWatchInterval
            : IntervalForMisses(missedObservations);
        DateTimeOffset anchor = baselineAt;
        if (lastFreshAt is DateTimeOffset fresh && fresh > anchor) anchor = fresh;
        if (lastExtraRequestAt is DateTimeOffset extra && extra > anchor) anchor = extra;

        if (now - anchor < interval) return false;

        lastExtraRequestAt = now;
        waitingForObservation = true;
        return true;
    }

    /// <summary>Only call after the existing verified active-account gate accepts a fresh sample.</summary>
    public void Observed(Guid profileID, DateTimeOffset now,
        DateTimeOffset? fiveHourNextResetAt = null,
        DateTimeOffset? sevenDayNextResetAt = null)
    {
        if (activeProfile is null)
        {
            // The caller already passed the confirmed active-account gate.
            activeProfile = profileID;
            baselineAt = now;
        }
        if (activeProfile != profileID) return;
        lastFreshAt = now;
        waitingForObservation = false;
        missedObservations = 0;

        // Accept only bounded, future provider reset timestamps; unknown
        // windows cannot replace a verified schedule with a guessed value.
        if (fiveHourNextResetAt is DateTimeOffset five &&
            five > now - PostResetWatch && five <= now.AddHours(6))
            fiveResetAt = five;
        if (sevenDayNextResetAt is DateTimeOffset seven &&
            seven > now - PostResetWatch && seven <= now.AddDays(8))
            weeklyResetAt = seven;
    }

    public void RequestTransportFailed(DateTimeOffset now)
    {
        if (!waitingForObservation || lastExtraRequestAt is not DateTimeOffset at ||
            now < at) return;
        waitingForObservation = false;
        missedObservations = Math.Min(missedObservations + 1, 3);
    }
}
