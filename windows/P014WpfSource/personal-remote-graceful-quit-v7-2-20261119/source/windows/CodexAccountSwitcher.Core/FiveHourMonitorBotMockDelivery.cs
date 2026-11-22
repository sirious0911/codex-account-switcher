namespace CodexAccountSwitcher.Core;

/// <summary>
/// Offline-only 5H lane proof: validates a fake Monitor Bot receipt against
/// its own ledger. Does not load any bot files, open a network socket,
/// start a PowerShell process or send a Telegram message.
/// </summary>
public sealed class FiveHourMonitorBotMockDelivery
{
    public FiveHourMonitorBotMockOutcome AcceptFakeReceiptOnce(
        FiveHour100RecoveryWorker ledger,
        FiveHour100Alert alert,
        WindowsAlertBotDryRunResult fakeProcessResult,
        TelegramSenderOwnershipDecision? senderOwnership,
        TelegramWeekly100SendConsent? simulatedOneMessageApproval)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var route=QuotaTelegramBotRouting.ForFiveHour(alert);
        if (!OperatingSystem.IsWindows() ||
            senderOwnership?.PermitsTemporaryWindowsSender != true ||
            simulatedOneMessageApproval is null ||
            route is null || route.Destination != QuotaTelegramBot.MonitorMuted ||
            route.EncryptedTokenFileName != "telegram-health-token.dpapi" ||
            route.ProposedSenderScriptName != "send-fivehour100-existing-monitor-bot.ps1" ||
            alert.Status != "MOCK_PENDING")
            return FiveHourMonitorBotMockOutcome.Blocked;

        if (!simulatedOneMessageApproval.TryConsume())
            return FiveHourMonitorBotMockOutcome.Blocked;

        FiveHour100Alert? reserved;
        try { reserved=ledger.ReserveMockDelivery(alert.Key,"MOCK_WINDOWS"); }
        catch (Exception ex) when (ex is IOException or
            InvalidDataException or UnauthorizedAccessException)
        {
            return FiveHourMonitorBotMockOutcome.Uncertain;
        }
        if (reserved is null)
            return FiveHourMonitorBotMockOutcome.Suppressed;

        try
        {
            if (TelegramFiveHour100ReceiptContract.TryParse(
                    fakeProcessResult, reserved.Key, out _) &&
                ledger.CompleteMockDelivery(reserved.Key,"MOCK_WINDOWS"))
                return FiveHourMonitorBotMockOutcome.MockAcknowledged;
        }
        catch (Exception ex) when (ex is IOException or
            InvalidDataException or UnauthorizedAccessException)
        {
            return FiveHourMonitorBotMockOutcome.Uncertain;
        }

        // Bad receipt / ambiguity is never interpreted as a delivery success
        // and never automatically retried after a restart.
        try { ledger.MarkMockUncertain(reserved.Key,"MOCK_WINDOWS"); }
        catch (Exception ex) when (ex is IOException or
            InvalidDataException or UnauthorizedAccessException) { }
        return FiveHourMonitorBotMockOutcome.Uncertain;
    }
}

public enum FiveHourMonitorBotMockOutcome
{
    Blocked,
    Suppressed,
    MockAcknowledged,
    Uncertain
}
