using System.Diagnostics;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Builds a NO-SEND preview of the exact Windows receipt-script argv.
/// No Process.Start, HTTP, bot token reads, chat ID reads, or OS changes.
/// The required PowerShell approval switches are NEVER included.
/// A future live connector requires a separate independent owner/consent gate.
/// </summary>
public static class WindowsDualBotDispatchPlan
{
    public static WindowsDualBotDispatchPlanResult PrepareFiveHour(
        FiveHour100Alert? alert,
        TelegramNotificationPreferences? preferences,
        WindowsDualBotLocalGateResult? pinnedLocalPreflight,
        string? scriptsDirectory,
        string? existingBotDirectory)
        => Prepare(QuotaTelegramBotRouting.ForFiveHour(alert),
            alert?.Key, TelegramNotificationLane.FiveHourRecovery,
            preferences, pinnedLocalPreflight, scriptsDirectory,
            existingBotDirectory);

    public static WindowsDualBotDispatchPlanResult PrepareWeekly(
        Weekly100Alert? alert,
        TelegramNotificationPreferences? preferences,
        WindowsDualBotLocalGateResult? pinnedLocalPreflight,
        string? scriptsDirectory,
        string? existingBotDirectory)
        => Prepare(QuotaTelegramBotRouting.ForWeekly(alert),
            alert?.Key, TelegramNotificationLane.WeeklyRecovery,
            preferences, pinnedLocalPreflight, scriptsDirectory,
            existingBotDirectory);

    private static WindowsDualBotDispatchPlanResult Prepare(
        QuotaTelegramRoute? route, string? eventKey,
        TelegramNotificationLane lane,
        TelegramNotificationPreferences? preferences,
        WindowsDualBotLocalGateResult? local,
        string? scriptsDirectory, string? existingBotDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return new(WindowsDispatchPlanStatus.UnsupportedPlatform);
        var decision=TelegramSenderDispatchPolicy.Inspect(preferences,lane);
        if (decision.Reason!=TelegramDispatchHoldReason.SingleSenderLeaseUnverified)
            return new(WindowsDispatchPlanStatus.LocalNotificationsNotRequested);
        if (local?.BothLocalArtifactsVerified!=true)
            return new(WindowsDispatchPlanStatus.LocalScriptPreflightFailed);
        if (route is null || !IsLowercaseEventKey(eventKey) ||
            route.Text.Length is < 10 or > 250)
            return new(WindowsDispatchPlanStatus.InvalidRecoveryEvent);

        var expectedName=lane==TelegramNotificationLane.FiveHourRecovery
            ? "send-fivehour100-existing-monitor-bot.ps1"
            : "send-weekly100-existing-alert-bot.ps1";
        var expectedDestination=lane==TelegramNotificationLane.FiveHourRecovery
            ? QuotaTelegramBot.MonitorMuted : QuotaTelegramBot.AlertImportant;
        if (route.Destination!=expectedDestination ||
            route.ProposedSenderScriptName!=expectedName ||
            route.ChatIdFileName!="telegram-chat-id.txt" ||
            route.EncryptedTokenFileName!=(lane==TelegramNotificationLane.FiveHourRecovery
                ? "telegram-health-token.dpapi" : "telegram-token.dpapi"))
            return new(WindowsDispatchPlanStatus.BotRoleMismatch);

        if (!LocalDirectory(scriptsDirectory) ||
            !LocalDirectory(existingBotDirectory))
            return new(WindowsDispatchPlanStatus.InvalidLocalDirectory);

        // This plan never passes -ApprovedSingleSend or -WindowsSenderVerified.
        // Running it against either reviewed sender script exits with code 40
        // before any credential read, without contacting Telegram.
        var powershell=Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32","WindowsPowerShell","v1.0","powershell.exe");
        if (!File.Exists(powershell))
            return new(WindowsDispatchPlanStatus.MissingWindowsPowerShell);

        var script=Path.Combine(Path.GetFullPath(scriptsDirectory!),expectedName);
        var botDirectory=Path.GetFullPath(existingBotDirectory!);
        var command=new ProcessStartInfo(powershell) {
            WorkingDirectory=Path.GetDirectoryName(script)!,
            UseShellExecute=false,
            CreateNoWindow=true,
            RedirectStandardOutput=true,
            RedirectStandardError=true
        };
        foreach(var argument in new[]{
            "-NoProfile", "-NonInteractive", "-File",script,
            "-LegacyBotDirectory",botDirectory,
            "-EventKey",eventKey!,
            "-Message",route.Text
        }) command.ArgumentList.Add(argument);

        return new(WindowsDispatchPlanStatus.OfflinePlanOnly,command,
            lane,eventKey);
    }

    private static bool IsLowercaseEventKey(string? key)=>
        key is {Length:64} &&
        key.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool LocalDirectory(string? directory)=>
        !string.IsNullOrWhiteSpace(directory) && directory.Length>=3 &&
        char.IsLetter(directory[0]) && directory[1]==':' &&
        directory[2] is '\\' or '/' &&
        Path.IsPathFullyQualified(directory);
}

public enum WindowsDispatchPlanStatus
{
    UnsupportedPlatform,
    LocalNotificationsNotRequested,
    LocalScriptPreflightFailed,
    InvalidRecoveryEvent,
    BotRoleMismatch,
    InvalidLocalDirectory,
    MissingWindowsPowerShell,
    OfflinePlanOnly
}

public sealed record WindowsDualBotDispatchPlanResult(
    WindowsDispatchPlanStatus Status,
    ProcessStartInfo? DisabledCommand=null,
    TelegramNotificationLane? Lane=null,
    string? EventKey=null)
{
    // Only the NO-SEND command can be returned; this is NOT a permit.
    public bool ActualTelegramDeliveryAuthorized=>false;

    public bool MatchesFakeReceipt(
        WindowsAlertBotDryRunResult? fakeResult)
    {
        if (Status!=WindowsDispatchPlanStatus.OfflinePlanOnly ||
            EventKey is null || Lane is null)
            return false;
        return Lane==TelegramNotificationLane.FiveHourRecovery
            ? TelegramFiveHour100ReceiptContract.TryParse(
                fakeResult,EventKey,out _)
            : TelegramWeekly100ReceiptContract.TryParse(
                fakeResult,EventKey,out _);
    }
}
