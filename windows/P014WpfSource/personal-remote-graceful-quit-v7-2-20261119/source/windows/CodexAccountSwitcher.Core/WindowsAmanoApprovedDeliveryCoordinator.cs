namespace CodexAccountSwitcher.Core;

/// <summary>
/// Joins a persisted production reservation to the reviewed Windows Telegram
/// subprocess adapter. Only an explicit, exact-event UI consent and verified
/// local sender claim may call this for an exact-event manual approval or
/// a separately, explicitly armed automatic Windows session. It cannot prove
/// that a different Windows/Mac host is not sending.
/// </summary>
internal sealed class WindowsAmanoApprovedDeliveryCoordinator
{
    private readonly WindowsAmanoProductionOutbox outbox;
    private readonly WindowsExistingAmanoBotProcessAdapter transport;
    // Null preserves the existing explicitly confirmed Windows-local sender.
    // Distributed operation requires a separately enrolled, signed fence.
    private readonly IWindowsAmanoSharedAuthoritySendFence? sharedFence;
    private readonly Func<TelegramNotificationPreferences?>? readLivePreferences;
    private readonly Func<bool>? liveSendConsent;

    internal WindowsAmanoApprovedDeliveryCoordinator(
        WindowsAmanoProductionOutbox outbox,
        WindowsExistingAmanoBotProcessAdapter transport,
        IWindowsAmanoSharedAuthoritySendFence? sharedFence = null,
        Func<TelegramNotificationPreferences?>? readLivePreferences = null,
        Func<bool>? liveSendConsent = null)
    {
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        // A distributed reservation can outlive a Settings change during
        // the awaited HTTPS call. Without a fresh source of truth, fail closed.
        if (sharedFence is not null && readLivePreferences is null)
            throw new ArgumentException(
                "Distributed sending requires a live preferences reader.",
                nameof(readLivePreferences));
        if (sharedFence is not null && liveSendConsent is null)
            throw new ArgumentException(
                "Distributed sending requires a live consent check.",
                nameof(liveSendConsent));
        this.sharedFence = sharedFence;
        this.readLivePreferences = readLivePreferences;
        this.liveSendConsent = liveSendConsent;
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

        bool SenderStillAuthorized()
        {
            if (cancellationToken.IsCancellationRequested ||
                localSender?.State != WindowsLocalSenderLeaseState.LocalClaimHeld)
                return false;
            if (sharedFence is null) return true;
            try
            {
                var current = readLivePreferences!()?.Normalize();
                var original = preferences?.Normalize();
                return current is not null && current == original &&
                    liveSendConsent!() &&
                    (lane == TelegramNotificationLane.FiveHourRecovery
                        ? current.WantsFiveHour : current.WantsWeekly);
            }
            catch (Exception)
            {
                // A failed registry read or changed preference is never a
                // reason to continue a previously approved send.
                return false;
            }
        }

        WindowsAmanoOutboxItem? reservation;
        try
        {
            // Preserve the test-vs-real distinction across restarts and direct
            // callers. A route may NEVER change a test to a recovery claim.
            if (!outbox.Read().Any(item => item.Lane == lane &&
                item.EventKey == route.EventKey &&
                item.IsTest == route.IsTest &&
                // Important 5H records never use the older Monitor or 7D
                // Alert sender. No remote reservation and no actual send.
                !item.FiveHourZeroImportant &&
                item.Status == WindowsAmanoOutboxStatus.AwaitingApproval))
                return new(WindowsAmanoApprovalStatus.NotPending);

            if (sharedFence is not null)
            {
                // The remote reservation takes a durable in-flight fence BEFORE
                // the local one-shot claim and external subprocess can start.
                // No remote reservation = NO send (including TEST messages).
                if (route.IsTest) return new(WindowsAmanoApprovalStatus.Blocked);
                bool reserved;
                try
                {
                    reserved = await sharedFence.ReserveBeforeSendAsync(
                        lane, route.EventKey, cancellationToken);
                }
                catch (Exception)
                {
                    // A lost response may still have committed remotely.
                    // Never retry the uncertain reservation.
                    return new(WindowsAmanoApprovalStatus.Blocked);
                }
                if (!reserved) return new(WindowsAmanoApprovalStatus.Blocked);
            }

            // The remote await may have allowed Settings/Viewer/Session OFF.
            // A successful server reservation is not authorization to send.
            if (!SenderStillAuthorized())
                return new(WindowsAmanoApprovalStatus.Blocked);

            reservation = outbox.ReserveOnce(lane, route.EventKey,
                userApprovedExactEvent, verifiedLocalSender: SenderStillAuthorized(),
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
            // Repeat immediately before handing control to the subprocess.
            // Preserve any local reservation as terminal uncertain, not retryable.
            if (!SenderStillAuthorized())
            {
                try { outbox.MarkUncertain(lane, route.EventKey, token); }
                catch (Exception) { /* A RESERVED item is also nonretryable. */ }
                return new(WindowsAmanoApprovalStatus.Blocked);
            }
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
            {
                if (sharedFence is not null)
                {
                    bool settled;
                    try
                    {
                        // Only after transport COMPLETES and local ACK persists.
                        settled = await sharedFence.AcknowledgeAfterProcessExitAsync(
                            route.EventKey, messageId, cancellationToken);
                    }
                    catch (Exception) { settled = false; }
                    if (!settled)
                        return new(WindowsAmanoApprovalStatus.Uncertain);
                    // A negative remote settlement leaves the distributed
                    // reservation frozen, preventing a second sender takeover.
                }
                return new(WindowsAmanoApprovalStatus.Acknowledged, messageId);
            }

            // Once reserved, no retry: response loss after a successful
            // Telegram send is indistinguishable from a failed send.
            // In distributed mode DO NOT release the remote in-flight fence
            // here: a failed/cancelled PowerShell subprocess may not yet be
            // confirmed terminated. Operator reconciliation remains required.
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
