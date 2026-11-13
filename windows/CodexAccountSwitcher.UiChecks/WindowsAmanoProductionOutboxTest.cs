using System;
using System.IO;
using System.Linq;
using CodexAccountSwitcher.Core;

internal static class WindowsAmanoProductionOutboxTest
{
    public static void Run(string output)
    {
        var data = Path.Combine(output, "amano-production-outbox-isolated");
        if (Directory.Exists(data))
            throw new InvalidOperationException("Production outbox fixture must start absent.");
        var outbox = new WindowsAmanoProductionOutbox(data);
        Check(outbox.Read().Count == 0 && !Directory.Exists(data),
            "Read-only inspection must not create production data.");
        var when = new DateTimeOffset(2026, 11, 13, 1, 0, 0, TimeSpan.Zero);
        var five = new string('a', 64);
        var seven = new string('b', 64);
        var preferences = new TelegramNotificationPreferences(
            TelegramNotificationDeviceRole.Sender, true, true, true);

        Check(!outbox.Stage(TelegramNotificationLane.FiveHourRecovery, five,
                when, true, TelegramNotificationPreferences.Default) &&
            !outbox.Stage(TelegramNotificationLane.FiveHourRecovery, five,
                when, false, preferences) &&
            !outbox.Stage(TelegramNotificationLane.FiveHourRecovery,
                "not-a-64-character-event-key", when, true, preferences) &&
            !Directory.Exists(data),
            "Viewer/OFF, missing route and malformed event must not write.");

        Check(outbox.Stage(TelegramNotificationLane.FiveHourRecovery,
                five, when, true, preferences) &&
            outbox.Stage(TelegramNotificationLane.WeeklyRecovery,
                seven, when.AddMinutes(1), true, preferences) &&
            !outbox.Stage(TelegramNotificationLane.FiveHourRecovery,
                five, when, true, preferences),
            "Two lanes must stage once and deduplicate by event identity.");
        var restarted = new WindowsAmanoProductionOutbox(data);
        var snapshot = restarted.Read();
        Check(snapshot.Count == 2 &&
            snapshot.All(item => item.Status == WindowsAmanoOutboxStatus.AwaitingApproval &&
                item.AttemptToken is null && item.TelegramMessageId is null),
            "Pending event data must survive process restart without being sent.");

        Check(restarted.ReserveOnce(TelegramNotificationLane.FiveHourRecovery,
                five, false, true, true) is null &&
            restarted.ReserveOnce(TelegramNotificationLane.FiveHourRecovery,
                five, true, false, true) is null &&
            restarted.ReserveOnce(TelegramNotificationLane.FiveHourRecovery,
                five, true, true, false) is null &&
            restarted.Read().All(item =>
                item.Status == WindowsAmanoOutboxStatus.AwaitingApproval),
            "All three send reservation prerequisites must be present.");

        var reservedFive = restarted.ReserveOnce(
            TelegramNotificationLane.FiveHourRecovery,
            five, true, true, true);
        Check(reservedFive is { Status: WindowsAmanoOutboxStatus.Reserved } &&
            reservedFive.AttemptToken is { Length: 32 } &&
            new WindowsAmanoProductionOutbox(data).Read()
                .Single(item => item.EventKey == five)
                .Status == WindowsAmanoOutboxStatus.Reserved,
            "A reservation must be durably written before any external send.");

        Check(restarted.ReserveOnce(TelegramNotificationLane.FiveHourRecovery,
                five, true, true, true) is null &&
            !restarted.Complete(TelegramNotificationLane.FiveHourRecovery,
                five, Guid.NewGuid().ToString("N"), 42) &&
            !restarted.Complete(TelegramNotificationLane.FiveHourRecovery,
                five, reservedFive!.AttemptToken, 0),
            "No second reservation, wrong token ACK or nonpositive message ID.");

        Check(restarted.Complete(TelegramNotificationLane.FiveHourRecovery,
                five, reservedFive!.AttemptToken, 42) &&
            !restarted.Complete(TelegramNotificationLane.FiveHourRecovery,
                five, reservedFive.AttemptToken, 42) &&
            new WindowsAmanoProductionOutbox(data).Read()
                .Single(item => item.EventKey == five).TelegramMessageId == 42,
            "ACK must be durable, positive and one-shot.");

        var reservedSeven = restarted.ReserveOnce(
            TelegramNotificationLane.WeeklyRecovery,
            seven, true, true, true);
        Check(reservedSeven is { Status: WindowsAmanoOutboxStatus.Reserved },
            "A 7D event has its own independent claim.");
        Check(restarted.MarkUncertain(TelegramNotificationLane.WeeklyRecovery,
                seven, reservedSeven!.AttemptToken) &&
            !restarted.MarkUncertain(TelegramNotificationLane.WeeklyRecovery,
                seven, reservedSeven.AttemptToken) &&
            new WindowsAmanoProductionOutbox(data).Read()
                .Single(item => item.EventKey == seven)
                .Status == WindowsAmanoOutboxStatus.Uncertain,
            "Ambiguous 7D result must be terminal and non-retryable.");
        Check(new WindowsAmanoProductionOutbox(data).ReserveOnce(
                TelegramNotificationLane.WeeklyRecovery,
                seven, true, true, true) is null,
            "Restart after unknown result must never send twice.");

        var pendingKey = new string('c', 64);
        Check(restarted.Stage(TelegramNotificationLane.FiveHourRecovery,
                pendingKey, when, true, preferences),
            "Additional synthetic event must be stageable.");
        var crashClaim = restarted.ReserveOnce(
            TelegramNotificationLane.FiveHourRecovery,
            pendingKey, true, true, true);
        Check(crashClaim is not null &&
            new WindowsAmanoProductionOutbox(data).ReserveOnce(
                TelegramNotificationLane.FiveHourRecovery, pendingKey,
                true, true, true) is null &&
            new WindowsAmanoProductionOutbox(data).Read()
                .Single(item => item.EventKey == pendingKey)
                .Status == WindowsAmanoOutboxStatus.Reserved,
            "Crash after reservation cannot cause an automatic replay.");

        var corrupt = Path.Combine(data, "telegram-production-outbox", "state.json");
        File.WriteAllText(corrupt, "{ malformed JSON");
        var failedClosed = false;
        try { _ = new WindowsAmanoProductionOutbox(data).Read(); }
        catch (System.Text.Json.JsonException) { failedClosed = true; }
        Check(failedClosed, "A damaged production ledger must not silently reset.");

        Check(!File.Exists(Path.Combine(data, "telegram-token.dpapi")) &&
            !File.Exists(Path.Combine(data, "telegram-chat-id.txt")) &&
            !Directory.Exists(Path.Combine(data, "usage-monitor-fivehour100")),
            "Production ledger must not copy bot credentials or edit mock workers.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
