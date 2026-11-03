using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Read-only projection of the Swift-host account snapshot for the Windows
/// monitor page. Uses existing usage values; no separate polling, account
/// selection, event rules, or messaging is introduced.
/// </summary>
public sealed record MonitorAccountRow(
    string AccountLabel,
    bool IsActive,
    int? FiveHourRemainingPercent,
    DateTimeOffset? FiveHourResetsAt,
    int? WeeklyRemainingPercent,
    DateTimeOffset? WeeklyResetsAt,
    int? ResetCreditsRemaining,
    string UsageStatus,
    bool HasUsageError);

// Tray percentages must describe the configured time window, never a
// fallback from an unrelated bucket (e.g. Free with no five-hour quota).
// A stale or unconfirmed active identity is not entitled to show a numeric badge.
public static class TrayUsageProjection
{
    public static int? ActivePercent(AccountSnapshot state)
    {
        if (!state.Settings.ShowsMenuBarPercentage || state.IsMutating ||
            !state.ActiveIdentityConfirmed || !string.IsNullOrEmpty(state.Error) ||
            state.ActiveAccountID is null) return null;

        var usage = state.Accounts.FirstOrDefault(row =>
            row.Profile.Id == state.ActiveAccountID)?.Usage;
        if (usage is null) return null;

        return state.Settings.StatusBarUsageWindow == "fiveHour"
            ? usage.FiveHourRemainingPercent
            : usage.RemainingPercent;
    }
}

public static class MonitorOverview
{
    public static IReadOnlyList<MonitorAccountRow> From(AccountSnapshot state)
    {
        return state.Accounts.Select(row =>
        {
            var usage = row.Usage;
            return new MonitorAccountRow(
                string.IsNullOrWhiteSpace(row.Profile.Email) ? row.Profile.DisplayName : row.Profile.Email,
                row.Profile.Id == state.ActiveAccountID && state.ActiveIdentityConfirmed,
                usage?.FiveHourRemainingPercent,
                usage?.FiveHourResetsAt,
                usage?.RemainingPercent,
                usage?.ResetsAt,
                usage?.ResetCreditsRemaining,
                row.UsageStatus,
                !string.IsNullOrWhiteSpace(row.UsageError));
        }).ToArray();
    }
}
