
namespace CodexAccountSwitcher.Core;

// Pure decision only. This type never opens a bot token, resolves a chat,
// constructs a sender or grants an automatic-send permission.
internal static class FiveHourPreResetEvidence
{
    private static readonly TimeSpan MaximumPreResetAge = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinimumCycle = TimeSpan.FromMinutes(270);
    private static readonly TimeSpan MaximumCycle = TimeSpan.FromMinutes(345);

    internal static bool IsValidOrMissing(FiveHour100Alert? alert)
    {
        if (alert is null) return false;
        var r = alert.PreResetRemainingPercent;
        var at = alert.PreResetObservedAt;
        var end = alert.PreResetWindowEndsAt;
        if (r is null && at is null && end is null) return true;
        if (r is not double remaining || at is not DateTimeOffset observed ||
            end is not DateTimeOffset ending || !double.IsFinite(remaining) ||
            remaining is < 0 or > 100) return false;
        if (observed >= ending || ending - observed > MaximumPreResetAge ||
            ending > alert.ObservedAt ||
            alert.ResetAt - ending < MinimumCycle ||
            alert.ResetAt - ending > MaximumCycle) return false;
        return true;
    }

    internal static bool IsVerifiedZero(FiveHour100Alert? alert) =>
        IsValidOrMissing(alert) &&
        alert?.PreResetRemainingPercent == 0 &&
        alert.PreResetObservedAt is not null &&
        alert.PreResetWindowEndsAt is not null;
}
