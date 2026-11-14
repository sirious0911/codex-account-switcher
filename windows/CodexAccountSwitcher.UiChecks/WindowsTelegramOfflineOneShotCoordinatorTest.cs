using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CodexAccountSwitcher.Core;

internal static class WindowsTelegramOfflineOneShotCoordinatorTest
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "codex-switcher-telegram-offline-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root))
            throw new InvalidOperationException("Offline fixture must not exist.");
        Directory.CreateDirectory(root);
        try
        {
            const string owner = "MOCK_WINDOWS";
            var now = DateTimeOffset.UtcNow;
            var key5 = new string('a', 64);
            var key7 = new string('b', 64);
            var crashedKey = new string('c', 64);
            var weeklySuccessKey = new string('d', 64);
            var timeoutKey = new string('e', 64);
            var fiveAlert = new FiveHour100Alert(key5, "FIVE_HOUR_100_RECOVERED",
                "fiveHour", 100, now, now.AddHours(5), "MOCK_PENDING");
            var weekAlert = new Weekly100Alert(key7, "WEEKLY_100_RECOVERED",
                "week", 100, now, now.AddDays(7), "MOCK_PENDING");

            var fiveDir = Path.Combine(root, "usage-monitor-fivehour100");
            var weekDir = Path.Combine(root, "usage-monitor-weekly100");
            Directory.CreateDirectory(fiveDir);
            Directory.CreateDirectory(weekDir);
            var fiveState = new FiveHour100DurableState
            {
                Outbox =
                [
                    fiveAlert,
                    fiveAlert with { Key = crashedKey },
                    fiveAlert with { Key = timeoutKey }
                ],
                EmittedKeys = [key5, crashedKey, timeoutKey]
            };
            var weekState = new Weekly100DurableState
            {
                Outbox = [weekAlert, weekAlert with { Key = weeklySuccessKey }],
                EmittedKeys = [key7, weeklySuccessKey]
            };
            File.WriteAllText(Path.Combine(fiveDir, "state.json"),
                JsonSerializer.Serialize(fiveState));
            File.WriteAllText(Path.Combine(weekDir, "state.json"),
                JsonSerializer.Serialize(weekState));

            var preferences = new TelegramNotificationPreferences(
                TelegramNotificationDeviceRole.Sender, true, true, true);
            var adapter = new WindowsTelegramOfflineOneShotCoordinator(root);
            int invocationCount = 0;
            WindowsTelegramOfflineSyntheticReply Fake5()
            {
                invocationCount++;
                Check(new FiveHour100RecoveryWorker(root).ReadMockOutbox()
                        .Single(e => e.Key == key5).Status == "MOCK_RESERVED",
                    "The disk reservation must precede invoking any fake transport.");
                return new(0,
                    "CODEX_FIVEHOUR100_TELEGRAM_ACK_V1|" + key5 + "|142\r\n");
            }

            var noApproval = adapter.SimulateOnce(
                TelegramNotificationLane.FiveHourRecovery, key5, preferences,
                false, true, Fake5);
            Check(noApproval.Status == WindowsTelegramOfflineOneShotStatus.Blocked &&
                invocationCount == 0, "Absent synthetic approval must not reserve.");
            var noOwner = adapter.SimulateOnce(
                TelegramNotificationLane.FiveHourRecovery, key5, preferences,
                true, false, Fake5);
            Check(noOwner.Status == WindowsTelegramOfflineOneShotStatus.Blocked &&
                invocationCount == 0, "Unverified synthetic owner must not reserve.");
            var viewer = adapter.SimulateOnce(
                TelegramNotificationLane.FiveHourRecovery, key5,
                TelegramNotificationPreferences.Default,
                true, true, Fake5);
            Check(viewer.Status == WindowsTelegramOfflineOneShotStatus.Blocked &&
                invocationCount == 0, "Viewer/OFF must not consume the event.");

            var complete = adapter.SimulateOnce(
                TelegramNotificationLane.FiveHourRecovery, key5, preferences,
                true, true, Fake5);
            Check(complete.Status == WindowsTelegramOfflineOneShotStatus.MockAcknowledged &&
                !complete.ActualTelegramSendAuthorized &&
                !complete.ActualTelegramMessageAcknowledged &&
                invocationCount == 1,
                "A correct synthetic 5H typed receipt completes only the mock record.");
            var restoredFive = new FiveHour100RecoveryWorker(root);
            Check(restoredFive.ReadMockOutbox().Single(e => e.Key == key5)
                    .Status == "MOCK_COMPLETE",
                "Mock completion must survive reopening the durable 5H ledger.");

            var restart = new WindowsTelegramOfflineOneShotCoordinator(root);
            var suppressed = restart.SimulateOnce(
                TelegramNotificationLane.FiveHourRecovery, key5, preferences,
                true, true, Fake5);
            Check(suppressed.Status == WindowsTelegramOfflineOneShotStatus.Suppressed &&
                invocationCount == 1,
                "Restart must never automatically invoke a second 5H attempt.");

            var failed7 = restart.SimulateOnce(
                TelegramNotificationLane.WeeklyRecovery, key7, preferences,
                true, true, () =>
                {
                    invocationCount++;
                    Check(new Weekly100RecoveryWorker(root).ReadMockOutbox()
                            .Single(e => e.Key == key7).Status == "MOCK_RESERVED",
                        "7D reservation must be persisted before fake execution.");
                    return new WindowsTelegramOfflineSyntheticReply(0,
                        "CODEX_FIVEHOUR100_TELEGRAM_ACK_V1|" + key7 + "|143");
                });
            Check(failed7.Status == WindowsTelegramOfflineOneShotStatus.MockUncertain &&
                new Weekly100RecoveryWorker(root).ReadMockOutbox()
                    .Single(e => e.Key == key7).Status == "MOCK_UNCERTAIN",
                "Wrong-lane receipt must be nonretryably uncertain.");
            var noRetry7 = new WindowsTelegramOfflineOneShotCoordinator(root)
                .SimulateOnce(TelegramNotificationLane.WeeklyRecovery,
                    key7, preferences, true, true,
                    () => { invocationCount++; return new(0,
                        "CODEX_WEEKLY100_TELEGRAM_ACK_V1|" + key7 + "|2"); });
            Check(noRetry7.Status == WindowsTelegramOfflineOneShotStatus.Suppressed &&
                invocationCount == 2,
                "An uncertain 7D response must suppress all automatic retry.");

            var success7 = new WindowsTelegramOfflineOneShotCoordinator(root)
                .SimulateOnce(TelegramNotificationLane.WeeklyRecovery,
                    weeklySuccessKey, preferences, true, true,
                    () =>
                    {
                        invocationCount++;
                        Check(new Weekly100RecoveryWorker(root).ReadMockOutbox()
                                .Single(e => e.Key == weeklySuccessKey).Status == "MOCK_RESERVED",
                            "7D successful fake ACK must be preceded by a disk claim.");
                        return new WindowsTelegramOfflineSyntheticReply(0,
                            "CODEX_WEEKLY100_TELEGRAM_ACK_V1|" + weeklySuccessKey + "|144");
                    });
            Check(success7.Status == WindowsTelegramOfflineOneShotStatus.MockAcknowledged &&
                new Weekly100RecoveryWorker(root).ReadMockOutbox()
                    .Single(e => e.Key == weeklySuccessKey).Status == "MOCK_COMPLETE",
                "A correct 7D fake ACK completes only the durable synthetic event.");

            var timeout5 = new WindowsTelegramOfflineOneShotCoordinator(root)
                .SimulateOnce(TelegramNotificationLane.FiveHourRecovery,
                    timeoutKey, preferences, true, true,
                    () =>
                    {
                        invocationCount++;
                        throw new TimeoutException("synthetic test only");
                    });
            Check(timeout5.Status == WindowsTelegramOfflineOneShotStatus.MockUncertain &&
                new FiveHour100RecoveryWorker(root).ReadMockOutbox()
                    .Single(e => e.Key == timeoutKey).Status == "MOCK_UNCERTAIN",
                "A fake 5H timeout must persist an uncertain nonretryable state.");

            Check(restoredFive.ReserveMockDelivery(crashedKey, owner) is not null,
                "Crash simulation must first persist the existing mock claim.");
            var afterCrash = new WindowsTelegramOfflineOneShotCoordinator(root)
                .SimulateOnce(TelegramNotificationLane.FiveHourRecovery,
                    crashedKey, preferences, true, true,
                    () => { invocationCount++; return new(0,
                        "CODEX_FIVEHOUR100_TELEGRAM_ACK_V1|" + crashedKey + "|3"); });
            Check(afterCrash.Status == WindowsTelegramOfflineOneShotStatus.Suppressed &&
                invocationCount == 4 &&
                restoredFive.ReadMockOutbox().Single(e => e.Key == crashedKey)
                    .Status == "MOCK_RESERVED",
                "Crash after reservation must leave the event reserved, never replayed.");

            var invalid = adapter.SimulateOnce(
                TelegramNotificationLane.FiveHourRecovery, "NOT_HEX",
                preferences, true, true,
                () => { invocationCount++; return null; });
            Check(invalid.Status == WindowsTelegramOfflineOneShotStatus.Blocked &&
                invocationCount == 4, "Malformed event keys fail before a fake invocation.");

            var realHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Codex Account Switcher Personal");
            var realRejected = false;
            try { _ = new WindowsTelegramOfflineOneShotCoordinator(realHome); }
            catch (ArgumentException) { realRejected = true; }
            Check(realRejected, "Operating user data home must never be accepted.");

            Check(!Directory.Exists(Path.Combine(root, "telegram-bot")) &&
                !File.Exists(Path.Combine(root, "telegram-token.dpapi")),
                "Offline coordination must not read or create Telegram credentials.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
