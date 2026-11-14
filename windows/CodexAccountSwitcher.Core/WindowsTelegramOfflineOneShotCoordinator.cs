using System.Globalization;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// TEST-ONLY integration of the real 5H/7D durable mock workers and an
/// injected fake response. The constructor accepts ONLY a direct child of
/// OS Temp with the dedicated synthetic prefix. There is no Process.Start,
/// HTTP, credential reader, script execution or production send option.
/// This cannot authorize Telegram messages, even on a successful fake ACK.
/// </summary>
internal sealed class WindowsTelegramOfflineOneShotCoordinator
{
    private const string MockOwner = "MOCK_WINDOWS";
    private const string FixturePrefix = "codex-switcher-telegram-offline-";
    private readonly FiveHour100RecoveryWorker five;
    private readonly Weekly100RecoveryWorker week;

    public WindowsTelegramOfflineOneShotCoordinator(string syntheticDataHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(syntheticDataHome);
        var normalized = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(syntheticDataHome));
        var parent = Path.GetDirectoryName(normalized);
        var temporary = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(parent, temporary,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(normalized).StartsWith(
                FixturePrefix, StringComparison.Ordinal) ||
            Path.GetFileName(normalized).Length <= FixturePrefix.Length)
            throw new ArgumentException(
                "Offline coordinator requires a direct OS-TEMP test fixture.",
                nameof(syntheticDataHome));
        if (Directory.Exists(normalized) &&
            (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Offline fixture is a reparse point.");
        five = new FiveHour100RecoveryWorker(normalized);
        week = new Weekly100RecoveryWorker(normalized);
    }

    public WindowsTelegramOfflineOneShotResult SimulateOnce(
        TelegramNotificationLane lane,
        string? eventKey,
        TelegramNotificationPreferences? preferences,
        bool syntheticApproval,
        bool syntheticLocalOwnerVerified,
        Func<WindowsTelegramOfflineSyntheticReply?>? invokeFake)
    {
        if (!OperatingSystem.IsWindows() ||
            !syntheticApproval || !syntheticLocalOwnerVerified ||
            invokeFake is null || !ValidKey(eventKey) ||
            !NotificationRequested(preferences, lane))
            return new(WindowsTelegramOfflineOneShotStatus.Blocked);

        bool reserved = false;
        try
        {
            if (lane == TelegramNotificationLane.FiveHourRecovery)
            {
                var alert = five.ReadMockOutbox().SingleOrDefault(e =>
                    e.Key == eventKey && e.Kind == "FIVE_HOUR_100_RECOVERED" &&
                    e.Lane == "fiveHour" && e.RemainingPercent == 100 &&
                    e.Status == "MOCK_PENDING");
                if (alert is null)
                    return new(WindowsTelegramOfflineOneShotStatus.Suppressed);
                // The existing worker atomically commits MOCK_RESERVED to disk.
                reserved = five.ReserveMockDelivery(eventKey!, MockOwner) is not null;
            }
            else if (lane == TelegramNotificationLane.WeeklyRecovery)
            {
                var alert = week.ReadMockOutbox().SingleOrDefault(e =>
                    e.Key == eventKey && e.Kind == "WEEKLY_100_RECOVERED" &&
                    e.Lane == "week" && e.RemainingPercent == 100 &&
                    e.Status == "MOCK_PENDING");
                if (alert is null)
                    return new(WindowsTelegramOfflineOneShotStatus.Suppressed);
                reserved = week.ReserveMockDelivery(eventKey!, MockOwner) is not null;
            }
            if (!reserved)
                return new(WindowsTelegramOfflineOneShotStatus.Suppressed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException)
        {
            // No invocation occurred; a competing reservation or torn file is
            // never an invitation to retry or overwrite the existing ledger.
            return new(WindowsTelegramOfflineOneShotStatus.MockUncertain);
        }

        try
        {
            // Only after durable reservation. This callback is a fake value
            // supplier; no production transport or executable is reachable.
            var fake = invokeFake();
            if (MatchesFakeReply(fake, lane, eventKey!) &&
                CompleteMock(lane, eventKey!))
                return new(WindowsTelegramOfflineOneShotStatus.MockAcknowledged);
        }
        catch (Exception)
        {
            // Any simulated crash, timeout or unexpected reply remains
            // uncertain. Do not expose exception messages.
        }
        try { MarkUncertain(lane, eventKey!); }
        catch (Exception) { /* The reserved ledger remains non-retryable. */ }
        return new(WindowsTelegramOfflineOneShotStatus.MockUncertain);
    }

    private bool CompleteMock(TelegramNotificationLane lane, string key) =>
        lane == TelegramNotificationLane.FiveHourRecovery
            ? five.CompleteMockDelivery(key, MockOwner)
            : week.CompleteMockDelivery(key, MockOwner);

    private void MarkUncertain(TelegramNotificationLane lane, string key)
    {
        if (lane == TelegramNotificationLane.FiveHourRecovery)
            five.MarkMockUncertain(key, MockOwner);
        else
            week.MarkMockUncertain(key, MockOwner);
    }

    private static bool NotificationRequested(
        TelegramNotificationPreferences? preferences, TelegramNotificationLane lane)
    {
        var normalized = preferences?.Normalize();
        return lane switch
        {
            TelegramNotificationLane.FiveHourRecovery =>
                normalized?.WantsFiveHour == true,
            TelegramNotificationLane.WeeklyRecovery =>
                normalized?.WantsWeekly == true,
            _ => false
        };
    }

    private static bool MatchesFakeReply(
        WindowsTelegramOfflineSyntheticReply? result,
        TelegramNotificationLane lane, string key)
    {
        if (result is null || result.ExitCode != 0 ||
            !string.IsNullOrEmpty(result.StandardError) ||
            result.StandardOutput is not { Length: > 0 and <= 160 } output)
            return false;
        if (output.EndsWith("\r\n", StringComparison.Ordinal))
            output = output[..^2];
        else if (output.EndsWith("\n", StringComparison.Ordinal))
            output = output[..^1];
        if (output.Contains('\r') || output.Contains('\n') || output.Contains('\0'))
            return false;
        var parts = output.Split('|');
        var prefix = lane == TelegramNotificationLane.FiveHourRecovery
            ? "CODEX_FIVEHOUR100_TELEGRAM_ACK_V1"
            : "CODEX_WEEKLY100_TELEGRAM_ACK_V1";
        return parts.Length == 3 && parts[0] == prefix && parts[1] == key &&
            parts[2].Length is >= 1 and <= 19 && parts[2][0] != '0' &&
            parts[2].All(c => c is >= '0' and <= '9') &&
            long.TryParse(parts[2], NumberStyles.None,
                CultureInfo.InvariantCulture, out var id) && id > 0;
    }

    private static bool ValidKey(string? key) =>
        key is { Length: 64 } &&
        key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed record WindowsTelegramOfflineSyntheticReply(
    int ExitCode, string? StandardOutput, string? StandardError = null);

internal enum WindowsTelegramOfflineOneShotStatus
{
    Blocked,
    Suppressed,
    MockAcknowledged,
    MockUncertain
}

internal sealed record WindowsTelegramOfflineOneShotResult(
    WindowsTelegramOfflineOneShotStatus Status)
{
    public bool ActualTelegramSendAuthorized => false;
    public bool ActualTelegramMessageAcknowledged => false;
}
