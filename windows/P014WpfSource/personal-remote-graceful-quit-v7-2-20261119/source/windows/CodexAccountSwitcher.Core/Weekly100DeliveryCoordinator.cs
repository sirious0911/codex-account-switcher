namespace CodexAccountSwitcher.Core;

/// <summary>
/// Explicit one-shot send coordinator. The Windows app never constructs or
/// invokes this class. Tests run it with an in-memory fake HTTP handler.
/// Only one device may be elected owner before this is integrated for live use.
/// </summary>
public sealed class Weekly100DeliveryCoordinator
{
    private const string Owner = "MOCK_WINDOWS";

    public async Task<Weekly100DispatchOutcome> TryDispatchOnceAsync(
        Weekly100RecoveryWorker ledger,
        TelegramWeekly100Sender sender,
        string eventKey,
        string? botToken,
        string? chatID,
        TelegramWeekly100SendConsent? approval,
        TelegramSenderOwnershipDecision? ownership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(sender);

        // Nothing is reserved or sent unless all input-level gates pass.
        if (!sender.IsEnabled || approval is null ||
            ownership?.PermitsTemporaryWindowsSender != true ||
            cancellationToken.IsCancellationRequested ||
            !TelegramWeekly100Sender.HasValidAddress(botToken,chatID))
            return Weekly100DispatchOutcome.Blocked;

        // Persisted under the OS-wide file lock BEFORE any HTTP request.
        // If a process crashes, MOCK_RESERVED is deliberately not retried.
        var reserved = ledger.ReserveMockDelivery(eventKey, Owner);
        if (reserved is null)
            return Weekly100DispatchOutcome.Suppressed;

        try
        {
            var result = await sender.SendOnceAsync(
                reserved, botToken, chatID, approval,
                cancellationToken).ConfigureAwait(false);
            if (result == TelegramWeekly100SendOutcome.Acknowledged)
            {
                // A committed acknowledgment is the only positive result.
                return ledger.CompleteMockDelivery(eventKey, Owner)
                    ? Weekly100DispatchOutcome.Acknowledged
                    : Weekly100DispatchOutcome.Uncertain;
            }
        }
        catch (Exception error) when (error is IOException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            // Cannot safely conclude whether an already-started HTTP request
            // was delivered. Never reissue the sendMessage call automatically.
        }

        // Blocked-after-reservation, timeout, 429, unexpected JSON or crash
        // leave the event non-retryable. Never claim a successful Telegram send.
        try { ledger.MarkMockUncertain(eventKey, Owner); }
        catch (IOException) { /* Durable reservation is still non-retryable. */ }
        return Weekly100DispatchOutcome.Uncertain;
    }
}

public enum Weekly100DispatchOutcome
{
    Blocked,
    Suppressed,
    Acknowledged,
    Uncertain
}

public enum TelegramSenderOwnerRole
{
    MacMiniSole = 0,
    TemporaryWindows = 1
}

/// <summary>
/// A product-level owner-selection condition, not an OS attestation or
/// distributed lease. Default Mac-only plan forbids Windows delivery.
/// Caller cannot treat self-reported flags as independently verified evidence.
/// </summary>
public sealed record TelegramSenderOwnershipDecision(
    TelegramSenderOwnerRole Role = TelegramSenderOwnerRole.MacMiniSole,
    bool TemporaryWindowsModeExplicitlyApproved = false,
    bool MacSenderInactivityIndependentlyConfirmed = false)
{
    public bool PermitsTemporaryWindowsSender =>
        Role == TelegramSenderOwnerRole.TemporaryWindows &&
        TemporaryWindowsModeExplicitlyApproved &&
        MacSenderInactivityIndependentlyConfirmed;
}
