namespace CodexAccountSwitcher.Core;

/// <summary>
/// Only delivers *newly staged* REAL 5H/7D recovery events after the user
/// explicitly armed this Windows process session. Reuses the exact same
/// durable once-only reservation and legacy Amano Telegram process adapter
/// as the per-event manual approval flow.
/// </summary>
internal sealed class WindowsTelegramAutoRecoveryDispatcher
{
    private readonly WindowsTelegramAutoSendSession session;
    private readonly WindowsAmanoProductionOutbox outbox;
    private readonly WindowsAmanoApprovedDeliveryCoordinator coordinator;
    private readonly SemaphoreSlim serial = new(1, 1);

    internal WindowsTelegramAutoRecoveryDispatcher(
        WindowsTelegramAutoSendSession session,
        WindowsAmanoProductionOutbox outbox,
        WindowsExistingAmanoBotProcessAdapter transport)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        coordinator = new WindowsAmanoApprovedDeliveryCoordinator(
            outbox, transport ?? throw new ArgumentNullException(nameof(transport)));
    }

    internal async Task<WindowsAmanoApprovalResult> DeliverNewlyStagedAsync(
        TelegramNotificationLane lane,
        string eventKey,
        DateTimeOffset observedAt,
        bool wasJustStaged,
        TelegramNotificationPreferences? preferences,
        WindowsTelegramLocalSenderLease? localLease,
        string? bundledHostPath,
        string? existingBotDirectory)
    {
        if (!wasJustStaged || observedAt == default ||
            !session.AllowsNewRecovery(lane, isTest: false,
                preferences, localLease))
            return new(WindowsAmanoApprovalStatus.Blocked);

        await serial.WaitAsync();
        try
        {
            // The session may be disarmed while a different event waits for
            // the first send. Never consume a reservation after disarming.
            if (!session.AllowsNewRecovery(lane, isTest: false,
                    preferences, localLease))
                return new(WindowsAmanoApprovalStatus.Blocked);
            var route = WindowsExistingAmanoBotRoute.Prepare(
                bundledHostPath, existingBotDirectory, lane,
                eventKey, observedAt);
            if (route is null || route.IsTest ||
                !WindowsExistingAmanoBotProcessAdapter.ValidRoute(route))
                return new(WindowsAmanoApprovalStatus.Blocked);

            // No review of old queued entries. The production ledger will
            // fail closed on duplicate, uncertain or already-ACKed keys.
            if (!outbox.Read().Any(item =>
                item.Lane == lane && item.EventKey == eventKey &&
                item.ObservedAt == observedAt && !item.IsTest &&
                item.Status == WindowsAmanoOutboxStatus.AwaitingApproval))
                return new(WindowsAmanoApprovalStatus.NotPending);

            return await coordinator.ExecuteApprovedOnceAsync(
                route, lane, preferences, localLease,
                userApprovedExactEvent: session.Armed,
                otherHostsNotSendingConfirmed: session.Armed);
        }
        finally
        {
            serial.Release();
        }
    }
}
