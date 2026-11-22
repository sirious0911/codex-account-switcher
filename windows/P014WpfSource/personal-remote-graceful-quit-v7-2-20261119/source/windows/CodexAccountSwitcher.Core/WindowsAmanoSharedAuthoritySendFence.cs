namespace CodexAccountSwitcher.Core;

/// <summary>
/// Explicit, non-default distributed sender fence at the shared Windows
/// manual/automatic Telegram delivery boundary. A remote lease is never
/// sufficient: an event must be reserved under a persistent in-flight hold.
/// </summary>
internal interface IWindowsAmanoSharedAuthoritySendFence
{
    Task<bool> ReserveBeforeSendAsync(
        TelegramNotificationLane lane, string eventKey,
        CancellationToken cancellationToken);

    // Called only after the Telegram subprocess has EXITED and a durable
    // local outbox ACK has been recorded. Never call while work is in flight.
    Task<bool> AcknowledgeAfterProcessExitAsync(
        string eventKey, long messageId, CancellationToken cancellationToken);
}

internal sealed class WindowsAmanoSignedSharedAuthoritySendFence
    : IWindowsAmanoSharedAuthoritySendFence
{
    private readonly WindowsSenderAuthorityClient client;
    private readonly long epoch;

    internal WindowsAmanoSignedSharedAuthoritySendFence(
        WindowsSenderAuthorityClient authenticatedClient, long verifiedEpoch)
    {
        client = authenticatedClient ??
            throw new ArgumentNullException(nameof(authenticatedClient));
        if (verifiedEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(verifiedEpoch));
        epoch = verifiedEpoch;
    }

    public async Task<bool> ReserveBeforeSendAsync(
        TelegramNotificationLane lane, string eventKey,
        CancellationToken cancellationToken)
    {
        var result = await client.ReserveEventAsync(
            epoch, eventKey, lane, cancellationToken);
        // Old Authority servers do not return SendFenceDurable. Refuse them:
        // they may allow a new owner while a previous child is still sending.
        return result.Code == "EVENT_RESERVED_NO_SEND" &&
            result.Epoch == epoch && result.EventKey == eventKey &&
            result.SendFenceDurable &&
            !result.ActualTelegramSendAuthorized;
    }

    public async Task<bool> AcknowledgeAfterProcessExitAsync(
        string eventKey, long messageId, CancellationToken cancellationToken)
    {
        var result = await client.AcknowledgeAsync(
            epoch, eventKey, messageId, cancellationToken);
        // Bot API acceptance is not proof that the human received a message.
        // A missing or uncertain reply means NO automatic retry.
        return result.Code == "EVENT_MARKED_ACKNOWLEDGED" &&
            !result.ActualTelegramSendAuthorized;
    }
}
