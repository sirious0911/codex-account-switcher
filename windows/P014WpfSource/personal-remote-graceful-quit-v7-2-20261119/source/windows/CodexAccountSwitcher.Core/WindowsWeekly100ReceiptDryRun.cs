using System.Diagnostics;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Simulates a FUTURE receipt-producing PowerShell sender with a fake process
/// invoker ONLY. No production process runner, Telegram call or credential read
/// exists here. All confirmed results are MOCK_COMPLETE, not real deliveries.
/// </summary>
public sealed class WindowsWeekly100ReceiptDryRun
{
    private const string Owner = "MOCK_WINDOWS";

    public async Task<Weekly100ReceiptDryRunOutcome> SimulateOnceAsync(
        Weekly100RecoveryWorker ledger,
        IWindowsAlertBotDryRunInvoker fakeInvoker,
        string receiptScriptPath,
        string legacyBotDirectory,
        string eventKey,
        TelegramSenderOwnershipDecision? ownerDecision,
        TelegramWeekly100SendConsent? consent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(fakeInvoker);

        // This gate is informational, not independent attestation that Mac is
        // off or the user approved a live message. Never reuse as live sender.
        if (!OperatingSystem.IsWindows() ||
            ownerDecision?.PermitsTemporaryWindowsSender != true ||
            consent is null || cancellationToken.IsCancellationRequested ||
            string.IsNullOrWhiteSpace(receiptScriptPath) ||
            string.IsNullOrWhiteSpace(legacyBotDirectory))
            return Weekly100ReceiptDryRunOutcome.Blocked;

        var legacy = new WindowsTelegramAlertBotScriptBinding(legacyBotDirectory);
        if (legacy.InspectFiles() !=
            WindowsTelegramAlertBotFilesStatus.FilesPresentUnverified ||
            !Path.IsPathFullyQualified(receiptScriptPath) ||
            !File.Exists(receiptScriptPath) ||
            !receiptScriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            return Weekly100ReceiptDryRunOutcome.Blocked;

        // Consume a one-time synthetic approval before reserving the event.
        if (!consent.TryConsume())
            return Weekly100ReceiptDryRunOutcome.Blocked;

        Weekly100Alert? reserved;
        try { reserved = ledger.ReserveMockDelivery(eventKey, Owner); }
        catch (Exception e) when (e is IOException or
            UnauthorizedAccessException or InvalidDataException)
        {
            return Weekly100ReceiptDryRunOutcome.Uncertain;
        }
        if (reserved is null)
            return Weekly100ReceiptDryRunOutcome.Suppressed;

        ProcessStartInfo? invocation = null;
        try
        {
            var executable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(executable))
            {
                MarkUncertain(ledger, eventKey);
                return Weekly100ReceiptDryRunOutcome.Uncertain;
            }
            invocation = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(receiptScriptPath)!
            };
            invocation.ArgumentList.Add("-NoProfile");
            invocation.ArgumentList.Add("-NonInteractive");
            invocation.ArgumentList.Add("-File");
            invocation.ArgumentList.Add(receiptScriptPath);
            invocation.ArgumentList.Add("-LegacyBotDirectory");
            invocation.ArgumentList.Add(Path.GetFullPath(legacyBotDirectory));
            invocation.ArgumentList.Add("-EventKey");
            invocation.ArgumentList.Add(reserved.Key);
            invocation.ArgumentList.Add("-Message");
            invocation.ArgumentList.Add(TelegramWeekly100Sender.FormatText(reserved));
            invocation.ArgumentList.Add("-ApprovedSingleSend");
            invocation.ArgumentList.Add("-WindowsSenderVerified");

            // This is ONLY a caller-supplied fake. No Process.Start is present.
            var result = await fakeInvoker.InvokeAsync(invocation, cancellationToken)
                .ConfigureAwait(false);

            // The emitted receipt binds the Telegram message ID to the exact
            // event key. An exit code, "ok", or legacy success output alone
            // is NOT enough. The underlying script is not executed here.
            if (TelegramWeekly100ReceiptContract.TryParse(
                    result, reserved.Key, out _))
            {
                try
                {
                    return ledger.CompleteMockDelivery(eventKey, Owner)
                        ? Weekly100ReceiptDryRunOutcome.MockAcknowledged
                        : Weekly100ReceiptDryRunOutcome.Uncertain;
                }
                catch (Exception e) when (e is IOException or
                    UnauthorizedAccessException or InvalidDataException)
                {
                    return Weekly100ReceiptDryRunOutcome.Uncertain;
                }
            }
        }
        catch (Exception e) when (e is IOException or
            UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or ArgumentException or
            System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            // Never include exception.Message, stdout, stderr or the URI in
            // persistence/logging. A request may have been delivered.
        }
        MarkUncertain(ledger, eventKey);
        return Weekly100ReceiptDryRunOutcome.Uncertain;
    }

    private static void MarkUncertain(Weekly100RecoveryWorker ledger, string key)
    {
        try { ledger.MarkMockUncertain(key, Owner); }
        catch (Exception e) when (e is IOException or
            UnauthorizedAccessException or InvalidDataException)
        {
            // The persisted RESERVED event still suppresses retries.
        }
    }
}

public enum Weekly100ReceiptDryRunOutcome
{
    Blocked,
    Suppressed,
    MockAcknowledged,
    Uncertain
}
