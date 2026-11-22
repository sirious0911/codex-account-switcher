using System.Globalization;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Routes confirmed Codex 5H/7D recovery events to the user's EXISTING
/// Amano Monitor/Alert bot configuration, without copying credentials or
/// touching the live Amano booking monitor. This class has NO send method:
/// approval, durable production reservation and process execution are separate.
/// </summary>
public static class WindowsExistingAmanoBotRoute
{
    // STEP3-A FUTURE-only sender selection. The file SHA and existing Alert
    // Bot config must be present before even constructing a command preview.
    // This function has no process/network/DPAPI reads and cannot arm delivery.
    public static WindowsExistingAmanoBotInvocation? PrepareFiveHourZero(
        string? bundledHostPath,
        string? existingAmanoBotDirectory,
        FiveHour100Alert? alert)
    {
        var route = QuotaTelegramBotRouting.ForFiveHourZeroEscalation(alert);
        if (route is null || string.IsNullOrWhiteSpace(bundledHostPath) ||
            string.IsNullOrWhiteSpace(existingAmanoBotDirectory))
            return null;
        var packaged = WindowsPackagedTelegramScripts.Inspect(bundledHostPath);
        if (!packaged.FiveHourZeroAlertVerified) return null;
        var bots = TelegramBotFileStatus.Inspect(existingAmanoBotDirectory);
        if (!bots.AlertBotFilesPresent || !bots.ChatDestinationPresent) return null;
        var script = Path.Combine(Path.GetDirectoryName(bundledHostPath)!,
            WindowsPackagedTelegramScripts.FiveHourZeroAlertScriptName);
        return new WindowsExistingAmanoBotInvocation(
            script, existingAmanoBotDirectory, alert!.Key, route.Text,
            Silent: false);
    }

    public static WindowsExistingAmanoBotInvocation? Prepare(
        string? bundledHostPath,
        string? existingAmanoBotDirectory,
        TelegramNotificationLane lane,
        string? eventKey,
        DateTimeOffset observedAt,
        bool isTest = false)
    {
        if (string.IsNullOrWhiteSpace(existingAmanoBotDirectory) ||
            eventKey is not { Length: 64 } ||
            !eventKey.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return null;

        var packaged = WindowsPackagedTelegramScripts.Inspect(bundledHostPath);
        var bots = TelegramBotFileStatus.Inspect(existingAmanoBotDirectory);
        var sender = lane switch
        {
            TelegramNotificationLane.FiveHourRecovery
                when packaged.MonitorVerified && bots.MonitorBotFilesPresent =>
                WindowsPackagedTelegramScripts.MonitorScriptName,
            TelegramNotificationLane.WeeklyRecovery
                when packaged.AlertVerified && bots.AlertBotFilesPresent =>
                WindowsPackagedTelegramScripts.AlertScriptName,
            _ => null
        };
        if (sender is null || !bots.ChatDestinationPresent ||
            bundledHostPath is null)
            return null;

        var offset = observedAt.ToOffset(TimeSpan.FromHours(9));
        var timestamp = offset.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var message = isTest
            ? lane == TelegramNotificationLane.FiveHourRecovery
                ? "Codex 5시간 알림 연결 테스트 (확인 " + timestamp + " KST)"
                : "Codex 7일 알림 연결 테스트 (확인 " + timestamp + " KST)"
            : lane == TelegramNotificationLane.FiveHourRecovery
                ? "Codex 5시간 한도 초기화 확인 (관측 " + timestamp + " KST)"
                : "Codex 주간(7일) 한도 초기화 확인 (관측 " + timestamp + " KST)";
        var path = Path.Combine(Path.GetDirectoryName(bundledHostPath)!, sender);
        return new WindowsExistingAmanoBotInvocation(
            path, existingAmanoBotDirectory, eventKey, message,
            lane == TelegramNotificationLane.FiveHourRecovery, isTest);
    }
}

public sealed record WindowsExistingAmanoBotInvocation(
    string ReviewedCodexScriptPath,
    string ExistingAmanoBotDirectory,
    string EventKey,
    string Message,
    bool Silent,
    bool IsTest = false)
{
    // This is a route preview, not a process launch or send authorization.
    public bool ActualTelegramSendAuthorized => false;
}
