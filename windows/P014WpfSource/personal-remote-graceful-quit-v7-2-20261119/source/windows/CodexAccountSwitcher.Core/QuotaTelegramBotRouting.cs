namespace CodexAccountSwitcher.Core;

/// <summary>
/// Pure, deterministic map from a confirmed quota-reset event to the user's
/// existing two Telegram bots. No process, HTTP, config reads or notifications.
/// Telegram mute/notification sound is controlled in the Telegram client.
/// </summary>
public static class QuotaTelegramBotRouting
{
    public static QuotaTelegramRoute? ForFiveHour(FiveHour100Alert? alert) =>
        alert is { Kind: "FIVE_HOUR_100_RECOVERED", Lane: "fiveHour",
            RemainingPercent: 100, Status: "MOCK_PENDING" or "MOCK_RESERVED" } && IsEventKey(alert.Key)
            ? new QuotaTelegramRoute(
                QuotaTelegramBot.MonitorMuted,
                "telegram-health-token.dpapi",
                "telegram-chat-id.txt",
                "send-fivehour100-existing-monitor-bot.ps1",
                FormatFiveHour(alert))
            : null;

    // STEP3-A planning only: the existing 5H sender remains MonitorMuted.
    // An important 5H alert needs a separately reviewed script, receipt
    // contract and one-message user approval before live routing.
    // No URI, token, script path or automatic sender is returned here.
    public static QuotaTelegramBot? PreviewFiveHourZeroDestination(
        FiveHour100Alert? alert) =>
        alert is { Kind: "FIVE_HOUR_100_RECOVERED", Lane: "fiveHour",
            RemainingPercent: 100, Status: "MOCK_PENDING" or "MOCK_RESERVED" } &&
        IsEventKey(alert.Key)
            ? FiveHourPreResetEvidence.IsVerifiedZero(alert)
                ? QuotaTelegramBot.AlertImportant
                : QuotaTelegramBot.MonitorMuted
            : null;

    public static QuotaTelegramRoute? ForWeekly(Weekly100Alert? alert) =>
        alert is { Kind: "WEEKLY_100_RECOVERED", Lane: "week",
            RemainingPercent: 100, Status: "MOCK_PENDING" or "MOCK_RESERVED" } && IsEventKey(alert.Key)
            ? new QuotaTelegramRoute(
                QuotaTelegramBot.AlertImportant,
                "telegram-token.dpapi",
                "telegram-chat-id.txt",
                "send-weekly100-existing-alert-bot.ps1",
                TelegramWeekly100Sender.FormatText(alert))
            : null;

    private static string FormatFiveHour(FiveHour100Alert alert)
    {
        var observed = alert.ObservedAt.ToOffset(TimeSpan.FromHours(9))
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return $"Codex 5시간 한도 초기화 확인 (관측 {observed} KST)";
    }

    private static bool IsEventKey(string? key) =>
        key?.Length == 64 &&
        key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public enum QuotaTelegramBot
{
    MonitorMuted,
    AlertImportant
}

public sealed record QuotaTelegramRoute(
    QuotaTelegramBot Destination,
    string EncryptedTokenFileName,
    string ChatIdFileName,
    string ProposedSenderScriptName,
    string Text);


