namespace CodexAccountSwitcher.Core;

/// <summary>
/// In-memory receipt rehearsal over durable synthetic 5H and 7D ledgers.
/// Does not create processes, read credentials, or invoke a network transport.
/// All caller-supplied owner/lease decisions are only synthetic fixtures.
/// </summary>
public sealed class WindowsTwoBotReceiptDryRun
{
    private const string Owner="MOCK_WINDOWS";

    public WindowsTwoBotReceiptDryRunOutcome SimulateOnce(
        FiveHour100RecoveryWorker? five,
        Weekly100RecoveryWorker? week,
        TelegramNotificationLane lane,
        string? eventKey,
        TelegramNotificationPreferences? settings,
        WindowsDualBotLocalGateResult? localPreflight,
        TelegramSenderOwnershipDecision? owner,
        TelegramHandoffInspection? sharedLeaseSimulation,
        TelegramWeekly100SendConsent? syntheticConsent,
        WindowsAlertBotDryRunResult? syntheticReceipt)
    {
        if (!OperatingSystem.IsWindows() ||
            localPreflight?.BothLocalArtifactsVerified!=true ||
            owner?.PermitsTemporaryWindowsSender!=true ||
            sharedLeaseSimulation is not {
                Reason: TelegramHandoffHoldReason.MockLeaseWouldQualify,
                WouldPassSharedLeaseSimulation: true
            } ||
            syntheticConsent is null ||
            eventKey is null || eventKey.Length!=64 ||
            !eventKey.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            TelegramSenderDispatchPolicy.Inspect(settings,lane).Reason!=
                TelegramDispatchHoldReason.SingleSenderLeaseUnverified ||
            (lane==TelegramNotificationLane.FiveHourRecovery && five is null) ||
            (lane==TelegramNotificationLane.WeeklyRecovery && week is null))
            return WindowsTwoBotReceiptDryRunOutcome.Blocked;

        if (!syntheticConsent.TryConsume())
            return WindowsTwoBotReceiptDryRunOutcome.Blocked;
        try
        {
            if (lane==TelegramNotificationLane.FiveHourRecovery)
            {
                var item=five!.ReadMockOutbox().SingleOrDefault(e=>e.Key==eventKey);
                if (item is null || item.Status!="MOCK_PENDING")
                    return WindowsTwoBotReceiptDryRunOutcome.Suppressed;
                if (QuotaTelegramBotRouting.ForFiveHour(item)?.Destination!=
                    QuotaTelegramBot.MonitorMuted)
                    return WindowsTwoBotReceiptDryRunOutcome.Blocked;
                var reserved=five.ReserveMockDelivery(eventKey,Owner);
                if (reserved is null) return WindowsTwoBotReceiptDryRunOutcome.Suppressed;
                if (TelegramFiveHour100ReceiptContract.TryParse(
                    syntheticReceipt,reserved.Key,out _) &&
                    five.CompleteMockDelivery(eventKey,Owner))
                    return WindowsTwoBotReceiptDryRunOutcome.MockAcknowledged;
                five.MarkMockUncertain(eventKey,Owner);
                return WindowsTwoBotReceiptDryRunOutcome.Uncertain;
            }
            if (lane==TelegramNotificationLane.WeeklyRecovery)
            {
                var item=week!.ReadMockOutbox().SingleOrDefault(e=>e.Key==eventKey);
                if (item is null || item.Status!="MOCK_PENDING")
                    return WindowsTwoBotReceiptDryRunOutcome.Suppressed;
                if (QuotaTelegramBotRouting.ForWeekly(item)?.Destination!=
                    QuotaTelegramBot.AlertImportant)
                    return WindowsTwoBotReceiptDryRunOutcome.Blocked;
                var reserved=week.ReserveMockDelivery(eventKey,Owner);
                if (reserved is null) return WindowsTwoBotReceiptDryRunOutcome.Suppressed;
                if (TelegramWeekly100ReceiptContract.TryParse(
                    syntheticReceipt,reserved.Key,out _) &&
                    week.CompleteMockDelivery(eventKey,Owner))
                    return WindowsTwoBotReceiptDryRunOutcome.MockAcknowledged;
                week.MarkMockUncertain(eventKey,Owner);
                return WindowsTwoBotReceiptDryRunOutcome.Uncertain;
            }
            return WindowsTwoBotReceiptDryRunOutcome.Blocked;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException)
        {
            // If reservation was persisted, the event cannot be retried.
            return WindowsTwoBotReceiptDryRunOutcome.Uncertain;
        }
    }
}

public enum WindowsTwoBotReceiptDryRunOutcome
{
    Blocked,
    Suppressed,
    MockAcknowledged,
    Uncertain
}
