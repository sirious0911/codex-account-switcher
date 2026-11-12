using System;
using System.IO;
using CodexAccountSwitcher.Core;

internal static class WindowsExistingAmanoBotRouteTest
{
    public static void Run(string output)
    {
        var root = Path.Combine(output, "existing-amano-bot-routing");
        if (Directory.Exists(root))
            throw new InvalidOperationException("Test folder must start absent.");
        var package = Path.Combine(root, "extracted-runtime");
        var legacy = Path.Combine(root, "existing-booking-bot");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(legacy);
        var hostPath = Path.Combine(package, "SwitcherHost.exe");
        foreach (var name in new[] {
            WindowsPackagedTelegramScripts.MonitorScriptName,
            WindowsPackagedTelegramScripts.AlertScriptName
        })
            File.Copy(Path.Combine("windows", "scripts", name), Path.Combine(package, name));
        foreach (var name in new[] {
            "send-telegram-health.ps1", "send-telegram-alert.ps1",
            "telegram-health-token.dpapi", "telegram-token.dpapi",
            "telegram-chat-id.txt"
        })
            File.WriteAllText(Path.Combine(legacy, name), "synthetic-fixture");

        var key = new string('a', 64);
        var observed = new DateTimeOffset(2026, 11, 12, 13, 25, 0, TimeSpan.Zero);
        var five = WindowsExistingAmanoBotRoute.Prepare(hostPath, legacy,
            TelegramNotificationLane.FiveHourRecovery, key, observed);
        var week = WindowsExistingAmanoBotRoute.Prepare(hostPath, legacy,
            TelegramNotificationLane.WeeklyRecovery, key, observed);
        Assert(five is not null &&
            five.ReviewedCodexScriptPath.EndsWith(
                WindowsPackagedTelegramScripts.MonitorScriptName, StringComparison.Ordinal) &&
            five.Message == "Codex 5시간 한도 복구: 잔여 사용량 100% (확인 2026-11-12 22:25 KST)" &&
            five.Silent && !five.ActualTelegramSendAuthorized,
            "5H must prepare the EXISTING monitor bot, muted, without sending.");
        Assert(week is not null &&
            week.ReviewedCodexScriptPath.EndsWith(
                WindowsPackagedTelegramScripts.AlertScriptName, StringComparison.Ordinal) &&
            week.Message == "Codex 주간(7일) 한도 복구: 잔여 사용량 100% (확인 2026-11-12 22:25 KST)" &&
            !week.Silent && !week.ActualTelegramSendAuthorized,
            "7D must prepare the EXISTING alert bot, not muted, without sending.");
        Assert(five!.ExistingAmanoBotDirectory == legacy &&
            week!.ExistingAmanoBotDirectory == legacy,
            "Both lanes must reuse the same original bot folder and chat destination.");
        Assert(WindowsExistingAmanoBotRoute.Prepare(
                hostPath, legacy, TelegramNotificationLane.FiveHourRecovery,
                "invalid-event-key", observed) is null,
            "Invalid event identity must block route preparation.");

        File.Delete(Path.Combine(legacy, "telegram-health-token.dpapi"));
        Assert(WindowsExistingAmanoBotRoute.Prepare(
                hostPath, legacy, TelegramNotificationLane.FiveHourRecovery,
                key, observed) is null &&
            WindowsExistingAmanoBotRoute.Prepare(
                hostPath, legacy, TelegramNotificationLane.WeeklyRecovery,
                key, observed) is not null,
            "Missing Monitor token must only block the 5H lane.");

        File.AppendAllText(Path.Combine(package,
            WindowsPackagedTelegramScripts.AlertScriptName), "# modified");
        Assert(WindowsExistingAmanoBotRoute.Prepare(
                hostPath, legacy, TelegramNotificationLane.WeeklyRecovery,
                key, observed) is null,
            "Altered packaged alert script must fail the source pin.");

        Assert(!Directory.Exists(Path.Combine(root, "sent")) &&
            !File.Exists(Path.Combine(root, "ack.json")),
            "Route planning must not invoke any Telegram sender.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
