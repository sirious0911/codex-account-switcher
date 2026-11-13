namespace CodexAccountSwitcher.Core;

/// <summary>
/// Joins a persisted production reservation to the reviewed Windows Telegram
/// subprocess adapter. Only an explicit, exact-event UI consent and verified
/// sole-sender ownership may call this in a future approved production build.
/// Current App.xaml.cs neither constructs nor invokes this class.
/// </summary>
internal sealed class WindowsAmanoApprovedDeliveryCoordinator
{
    private readonly WindowsAmanoProductionOutbox outbox;
    private readonly WindowsExistingAmanoBotProcessAdapter transport;

    internal WindowsAmanoApprovedDeliveryCoordinator(
        WindowsAmanoProductionOutbox outbox,
        WindowsExistingAmanoBotProcessAdapter transport)
    {
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    internal async Task<WindowsAmanoApprovalResult> ExecuteApprovedOnceAsync(
        WindowsExistingAmanoBotInvocation? route,
        TelegramNotificationLane lane,
        TelegramNotificationPreferences? preferences,
        WindowsTelegramLocalSenderLease? localSender,
        bool userApprovedExactEvent,
        bool otherHostsNotSendingConfirmed,
        CancellationToken cancellationToken = default)
    {
        // All requirements must be checked before any persistent reservation.
        var laneEnabled = lane switch
        {
            TelegramNotificationLane.FiveHourRecovery =>
                preferences?.Normalize().WantsFiveHour == true,
            TelegramNotificationLane.WeeklyRecovery =>
                preferences?.Normalize().WantsWeekly == true,
            _ => false
        };
        if (!userApprovedExactEvent || !otherHostsNotSendingConfirmed ||
            !laneEnabled || route is null ||
            localSender?.State != WindowsLocalSenderLeaseState.LocalClaimHeld ||
            (lane == TelegramNotificationLane.FiveHourRecovery) != route.Silent ||
            !WindowsExistingAmanoBotProcessAdapter.ValidRoute(route))
            return new(WindowsAmanoApprovalStatus.Blocked);

        WindowsAmanoOutboxItem? reservation;
        try
        {
            // Preserve the test-vs-real distinction across restarts and direct
            // callers. A route may NEVER change a test to a recovery claim.
            if (!outbox.Read().Any(item => item.Lane == lane &&
                item.EventKey == route.EventKey &&
                item.IsTest == route.IsTest &&
                item.Status == WindowsAmanoOutboxStatus.AwaitingApproval))
                return new(WindowsAmanoApprovalStatus.NotPending);
            reservation = outbox.ReserveOnce(lane, route.EventKey,
                userApprovedExactEvent, verifiedLocalSender: true,
                crossHostSenderHandoffConfirmed: otherHostsNotSendingConfirmed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or System.Text.Json.JsonException)
        {
            // A malformed file or ambiguous disk write must never fall back
            // to direct sending or creation of a new outbox.
            return new(WindowsAmanoApprovalStatus.LedgerUnavailable);
        }
        if (reservation?.AttemptToken is not { Length: 32 } token)
            return new(WindowsAmanoApprovalStatus.NotPending);

        WindowsAmanoDeliveryResult result;
        try
        {
            // This code is unreachable from current automatic quota callbacks.
            result = await transport.ExecuteOnceAsync(route,
                new WindowsAmanoInvocationGate(
                    SingleSendApproved: true,
                    SoleSenderVerified: true,
                    DurableReservationConfirmed: true),
                cancellationToken);
        }
        catch (Exception)
        {
            result = new(WindowsAmanoDeliveryStatus.Uncertain);
        }

        try
        {
            if (result.Status == WindowsAmanoDeliveryStatus.Acknowledged &&
                result.MessageId is long messageId && messageId > 0 &&
                outbox.Complete(lane, route.EventKey, token, messageId))
                return new(WindowsAmanoApprovalStatus.Acknowledged, messageId);

            // Once reserved, no retry: response loss after a successful
            // Telegram send is indistinguishable from a failed send.
            outbox.MarkUncertain(lane, route.EventKey, token);
        }
        catch (Exception)
        {
            // Leave a durable RESERVED state, which cannot be reclaimed.
        }
        return new(WindowsAmanoApprovalStatus.Uncertain);
    }
}

internal enum WindowsAmanoApprovalStatus
{
    Blocked,
    NotPending,
    LedgerUnavailable,
    Acknowledged,
    Uncertain
}

internal sealed record WindowsAmanoApprovalResult(
    WindowsAmanoApprovalStatus Status,
    long? TelegramMessageId = null);
