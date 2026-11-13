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
                ? "Codex 5시간 한도 복구: 잔여 사용량 100% (확인 " + timestamp + " KST)"
                : "Codex 주간(7일) 한도 복구: 잔여 사용량 100% (확인 " + timestamp + " KST)";
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
