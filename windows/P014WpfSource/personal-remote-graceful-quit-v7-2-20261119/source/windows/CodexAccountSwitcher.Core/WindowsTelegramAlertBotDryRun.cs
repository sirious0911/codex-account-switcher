using System.Diagnostics;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Fake-process-only dry-run of the original Amano Windows Alert Bot script.
/// There is deliberately no production Process.Start implementation in the app.
/// The injected invoker is supplied only by isolated tests.
/// </summary>
public sealed class WindowsTelegramAlertBotDryRun
{
    private const string MockOwner = "MOCK_WINDOWS";

    public async Task<WindowsAlertBotDryRunOutcome> SimulateOnceAsync(
        Weekly100RecoveryWorker ledger,
        WindowsTelegramAlertBotScriptBinding script,
        IWindowsAlertBotDryRunInvoker invoker,
        string eventKey,
        TelegramSenderOwnershipDecision? ownership,
        TelegramWeekly100SendConsent? approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(invoker);

        // No policy approval, no single-use consent, no valid script files:
        // never mutate even the mock outbox.
        if (!OperatingSystem.IsWindows() ||
            ownership?.PermitsTemporaryWindowsSender != true ||
            approval is null || cancellationToken.IsCancellationRequested ||
            script.InspectFiles() !=
                WindowsTelegramAlertBotFilesStatus.FilesPresentUnverified)
            return WindowsAlertBotDryRunOutcome.Blocked;

        // The exact approval can be used for only one simulated attempt.
        if (!approval.TryConsume())
            return WindowsAlertBotDryRunOutcome.Blocked;

        // Persisted before invocation; a crash or exception cannot silently
        // trigger a second attempt on the same event.
        Weekly100Alert? reserved;
        try { reserved = ledger.ReserveMockDelivery(eventKey, MockOwner); }
        catch (Exception error) when (error is IOException or
            UnauthorizedAccessException or InvalidDataException)
        {
            // A competing process may hold the ledger lock. No attempt is
            // started and the event is not falsely marked as delivered.
            return WindowsAlertBotDryRunOutcome.Uncertain;
        }
        if (reserved is null)
            return WindowsAlertBotDryRunOutcome.Suppressed;

        ProcessStartInfo? command;
        try
        {
            if (!script.TryPrepare(reserved, ownership, out command) ||
                command is null)
            {
                TryMarkUncertain(ledger, eventKey);
                return WindowsAlertBotDryRunOutcome.Uncertain;
            }
        }
        catch (Exception error) when (error is IOException or
            UnauthorizedAccessException or InvalidDataException)
        {
            TryMarkUncertain(ledger, eventKey);
            return WindowsAlertBotDryRunOutcome.Uncertain;
        }

        try
        {
            var result = await invoker.InvokeAsync(command, cancellationToken)
                .ConfigureAwait(false);
            // The legacy script exposes only a PowerShell exit status, not an
            // authenticated Bot API message_id. Exit 0 is NOT delivery proof.
            // Deliberately discard stdout/stderr, which could contain the token
            // inside a Telegram request URI on failures.
            TryMarkUncertain(ledger, eventKey);
            return result.ExitCode == 0
                ? WindowsAlertBotDryRunOutcome.ExitZeroWithoutDeliveryReceipt
                : WindowsAlertBotDryRunOutcome.Uncertain;
        }
        catch (Exception error) when (error is IOException or
            UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            TryMarkUncertain(ledger, eventKey);
            return WindowsAlertBotDryRunOutcome.Uncertain;
        }
    }

    private static void TryMarkUncertain(Weekly100RecoveryWorker ledger, string key)
    {
        try { ledger.MarkMockUncertain(key, MockOwner); }
        catch (Exception error) when (error is IOException or
            UnauthorizedAccessException or InvalidDataException)
        {
            // A persisted RESERVED event remains non-retryable. Do not expose
            // storage/PowerShell error messages in an automated notification.
        }
    }
}

/// <summary>
/// Test-only caller abstraction: the fork intentionally provides no real
/// process runner yet. Implementing one requires an additional live Gate.
/// </summary>
public interface IWindowsAlertBotDryRunInvoker
{
    Task<WindowsAlertBotDryRunResult> InvokeAsync(
        ProcessStartInfo preparedCommand, CancellationToken cancellationToken);
}

public sealed record WindowsAlertBotDryRunResult(
    int ExitCode, string? StandardOutput = null, string? StandardError = null);

public enum WindowsAlertBotDryRunOutcome
{
    Blocked,
    Suppressed,
    ExitZeroWithoutDeliveryReceipt,
    Uncertain
}
