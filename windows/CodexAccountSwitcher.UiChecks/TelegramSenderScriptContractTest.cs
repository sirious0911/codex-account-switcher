using System;
using System.IO;

internal static class TelegramSenderScriptContractTest
{
    /// <summary>
    /// Read source only: never execute a sender script, access DPAPI credentials,
    /// call Telegram or read the real user's bot/chat files.
    /// </summary>
    public static void Run()
    {
        var root = Directory.GetCurrentDirectory();
        var scripts = Path.Combine(root, "windows", "scripts");
        var healthPath = Path.Combine(scripts,
            "send-fivehour100-existing-monitor-bot.ps1");
        var alertPath = Path.Combine(scripts,
            "send-weekly100-existing-alert-bot.ps1");

        Verify(healthPath, "telegram-health-token.dpapi",
            "telegram-token.dpapi", "true", "CODEX_FIVEHOUR100_TELEGRAM_ACK_V1",
            "Codex 5시간 한도 복구");
        Verify(alertPath, "telegram-token.dpapi",
            "telegram-health-token.dpapi", "false", "CODEX_WEEKLY100_TELEGRAM_ACK_V1",
            "Codex 주간");
    }

    private static void Verify(
        string filePath, string requiredTokenName, string forbiddenTokenName,
        string expectedSilentValue, string receiptMarker, string messageHeading)
    {
        Assert(File.Exists(filePath), "Missing proposed Telegram sender source: " +
            Path.GetFileName(filePath));
        var script = File.ReadAllText(filePath);
        var approval = script.IndexOf(
            "if (-not $ApprovedSingleSend -or -not $WindowsSenderVerified)",
            StringComparison.Ordinal);
        var firstTokenFile = script.IndexOf(
            "$tokenFile = Join-Path", StringComparison.Ordinal);
        var firstNetworkCall = script.IndexOf(
            "Invoke-RestMethod @request", StringComparison.Ordinal);

        Assert(approval >= 0 && firstTokenFile > approval &&
            firstNetworkCall > firstTokenFile &&
            script.AsSpan(approval, firstTokenFile - approval).Contains("exit 40",
                StringComparison.Ordinal),
            "Explicit approval and sender ownership must fail closed before secrets.");
        Assert(script.Contains("'" + requiredTokenName + "'", StringComparison.Ordinal) &&
            !script.Contains("'" + forbiddenTokenName + "'", StringComparison.Ordinal) &&
            script.Contains("'telegram-chat-id.txt'", StringComparison.Ordinal),
            "Wrong bot token or recipient for the requested lane.");
        Assert(script.Contains("disable_notification = '" +
                    expectedSilentValue + "'", StringComparison.Ordinal) &&
            script.Contains("disable_web_page_preview = 'true'", StringComparison.Ordinal),
            "5H must be silent and 7D must be audible at Telegram Bot API level.");
        Assert(script.Contains(messageHeading, StringComparison.Ordinal) &&
            script.Contains(receiptMarker + "|", StringComparison.Ordinal),
            "Bot sender message and typed receipt must match the lane.");
        Assert(script.Contains("MaximumRedirection = 0", StringComparison.Ordinal) &&
            script.Contains("TimeoutSec = 10", StringComparison.Ordinal) &&
            !script.Contains("Start-Sleep", StringComparison.Ordinal),
            "Telegram request must not redirect or implement automatic retries.");
        Assert(script.Contains("Microsoft.PowerShell.Security.psd1",
                StringComparison.Ordinal) &&
            script.Contains("ZeroFreeBSTR", StringComparison.Ordinal),
            "Use the pinned Windows PowerShell DPAPI module and wipe the BSTR.");
        Assert(!script.Contains("Start-Transcript", StringComparison.Ordinal) &&
            !script.Contains("Write-Host", StringComparison.Ordinal) &&
            !script.Contains("Write-Error", StringComparison.Ordinal),
            "Do not log secret-bearing HTTP errors or user token material.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
