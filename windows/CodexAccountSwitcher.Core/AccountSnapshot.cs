using System.Text.Json;

namespace CodexAccountSwitcher.Core;

// Wire DTOs only. Account policy and storage live in Sources/SwitcherCore.
public sealed record Profile(Guid Id, string DisplayName, string? Email);
public sealed record Usage(int RemainingPercent, DateTimeOffset ResetsAt,
    int? FiveHourRemainingPercent, DateTimeOffset? FiveHourResetsAt, int? ResetCreditsRemaining = null);
public sealed record SubscriptionSnapshot(DateTimeOffset ActiveUntil, DateTimeOffset LastChecked);
// An explicitly labeled plan-specific window (e.g. Free 30D), never a 7D value.
public sealed record NonWeeklyUsage(int RemainingPercent, int WindowDurationMins,
    DateTimeOffset ResetsAt, int? ResetCreditsRemaining, DateTimeOffset ObservedAt)
{
    public string WindowLabel => WindowDurationMins > 0 && WindowDurationMins % 1440 == 0
        ? $"{WindowDurationMins / 1440}D"
        : WindowDurationMins > 0 && WindowDurationMins % 60 == 0
        ? $"{WindowDurationMins / 60}H" : $"{WindowDurationMins}m";
}
public sealed record AccountRow(Profile Profile, string Initials, Usage? Usage, string? UsageError,
    string UsageStatus = "loaded", SubscriptionSnapshot? Subscription = null,
    NonWeeklyUsage? NonWeeklyUsage = null);
public sealed record Preferences(string Language = "system", bool ShowsMenuBarPercentage = true, bool ShowsFiveHourUsage = false,
    string StatusBarUsageWindow = "fiveHour", bool AutoResetEnabled = false);
public sealed record AccountSnapshot(AccountRow[] Accounts, Guid? ActiveAccountID, Preferences Settings,
    bool IsMutating, bool IsAddingAccount, bool ActiveIdentityConfirmed, string? Error, Dictionary<string, string> Strings)
{
    public static AccountSnapshot Empty { get; } = new([], null, new(), false, false, true, null, []);
    public string Text(string key) => Strings.GetValueOrDefault(key, key);
    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNameCaseInsensitive = true };
}

public interface IAccountClient
{
    AccountSnapshot State { get; }
    event Action? Changed;
    Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null);
}
