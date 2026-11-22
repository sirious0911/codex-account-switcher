namespace CodexAccountSwitcher.Core;

/// <summary>
/// Side-effect-free guard for same-user, local remote quit. A remote exit is
/// refused if account state, Telegram delivery or its durable ledger is busy.
/// Missing/invalid ledger is not equivalent to an empty ledger.
/// </summary>
public static class RemoteQuitReadiness
{
    public static string Check(bool exiting, bool pendingRemoteQuit,
        bool accountSwitchInProgress, bool telegramSendInFlight,
        bool outboxReadable, bool outboxUnsettled)
    {
        if (exiting || pendingRemoteQuit) return "EXIT_IN_PROGRESS";
        if (accountSwitchInProgress) return "ACCOUNT_SWITCH_BUSY";
        if (telegramSendInFlight) return "TELEGRAM_SEND_BUSY";
        if (!outboxReadable) return "OUTBOX_UNREADABLE";
        if (outboxUnsettled) return "OUTBOX_UNSETTLED";
        return "READY";
    }
}
