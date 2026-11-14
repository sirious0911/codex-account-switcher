using System;
using System.IO;
using CodexAccountSwitcher.Core;

internal static class WindowsPackagedTelegramScriptsTest
{
    public static void Run(string output)
    {
        // The only files under test are non-secret repository scripts.
        // No DPAPI bot token, chat destination or Telegram endpoint is used.
        var scratch = Path.Combine(output, "packaged-telegram-senders-fixture");
        if (Directory.Exists(scratch))
            throw new InvalidOperationException("Sender package fixture must be new.");
        Directory.CreateDirectory(scratch);

        var host = Path.Combine(scratch, "SwitcherHost.exe");
        var missing = WindowsPackagedTelegramScripts.Inspect(host);
        Check(!missing.BothVerified && !missing.ActualTelegramSendAuthorized,
            "Missing packaged scripts must fail closed.");

        var source = Path.Combine(Directory.GetCurrentDirectory(), "windows", "scripts");
        var monitor = Path.Combine(scratch, WindowsPackagedTelegramScripts.MonitorScriptName);
        var alert = Path.Combine(scratch, WindowsPackagedTelegramScripts.AlertScriptName);
        File.Copy(Path.Combine(source, WindowsPackagedTelegramScripts.MonitorScriptName), monitor);
        var incomplete = WindowsPackagedTelegramScripts.Inspect(host);
        Check(incomplete.MonitorVerified && !incomplete.AlertVerified,
            "The 5H script alone must not satisfy a two-bot package.");

        File.Copy(Path.Combine(source, WindowsPackagedTelegramScripts.AlertScriptName), alert);
        var intact = WindowsPackagedTelegramScripts.Inspect(host);
        Check(intact.BothVerified && !intact.ActualTelegramSendAuthorized &&
            intact.Display.Contains("자동 발송: 차단 · 수동 1건 승인 필요", StringComparison.Ordinal),
            "Both pinned scripts are required, but never grant a sender permit.");

        File.AppendAllText(alert, "#tampered-fixture");
        var tampered = WindowsPackagedTelegramScripts.Inspect(host);
        Check(tampered.MonitorVerified && !tampered.AlertVerified,
            "A modified 7D script must invalidate the exact package SHA256.");
        File.Delete(monitor);
        Check(!WindowsPackagedTelegramScripts.Inspect(host).BothVerified,
            "A missing 5H script must invalidate the package.");

        Check(!Directory.Exists(Path.Combine(scratch, "accounts")) &&
            !Directory.Exists(Path.Combine(scratch, "bot-config")),
            "Sender verification must not create credential directories.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
