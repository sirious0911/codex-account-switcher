namespace CodexAccountSwitcher.Core;

/// <summary>
/// Plans a Windows 5H/7D PowerShell command preview WITHOUT the two
/// switches required for live Telegram sends. No Process.Start, network,
/// credential or setting access. This result must never be executed: only
/// the separately PINNED scripts have been tested to exit 40 without approval.
/// </summary>
public static class WindowsTelegramLaunchPreview
{
    private static readonly TimeSpan MaximumSampleAge=TimeSpan.FromMinutes(6);
    private static readonly TimeSpan FutureSkew=TimeSpan.FromSeconds(30);

    public static WindowsTelegramLaunchPreviewResult Inspect(
        TelegramNotificationPreferences? localPreferences,
        TelegramNotificationLane lane,
        FiveHour100Alert? fiveHour,
        Weekly100Alert? weekly,
        WindowsDualBotLocalGateResult? localArtifacts,
        TelegramSenderOwnershipDecision? declaredOwner,
        TelegramHandoffInspection? sharedLeaseSimulation,
        string? candidateScriptsDirectory,
        string? existingBotDirectory,
        DateTimeOffset now)
    {
        var preferences=TelegramSenderDispatchPolicy.Inspect(localPreferences,lane);
        if (preferences.Reason!=TelegramDispatchHoldReason.SingleSenderLeaseUnverified)
            return Hold(WindowsTelegramLaunchHoldReason.LocalPreferenceOffOrViewer);

        // Local flags and simulated leases are NOT independent ownership proof.
        if (declaredOwner?.PermitsTemporaryWindowsSender!=true)
            return Hold(WindowsTelegramLaunchHoldReason.DeclaredWindowsOwnerMissing);
        if (sharedLeaseSimulation is not {
            Reason:TelegramHandoffHoldReason.MockLeaseWouldQualify,
            WouldPassSharedLeaseSimulation:true
        })
            return Hold(WindowsTelegramLaunchHoldReason.SharedLeaseSimulationMissing);
        if (localArtifacts?.BothLocalArtifactsVerified!=true ||
            !OperatingSystem.IsWindows())
            return Hold(WindowsTelegramLaunchHoldReason.BotScriptPreflightIncomplete);

        var route=lane switch
        {
            TelegramNotificationLane.FiveHourRecovery =>
                QuotaTelegramBotRouting.ForFiveHour(fiveHour),
            TelegramNotificationLane.WeeklyRecovery =>
                QuotaTelegramBotRouting.ForWeekly(weekly),
            _ => null
        };
        var key=lane switch
        {
            TelegramNotificationLane.FiveHourRecovery => fiveHour?.Key,
            TelegramNotificationLane.WeeklyRecovery => weekly?.Key,
            _ => null
        };
        var observed=lane switch
        {
            TelegramNotificationLane.FiveHourRecovery => fiveHour?.ObservedAt,
            TelegramNotificationLane.WeeklyRecovery => weekly?.ObservedAt,
            _ => null
        };
        var reset=lane switch
        {
            TelegramNotificationLane.FiveHourRecovery => fiveHour?.ResetAt,
            TelegramNotificationLane.WeeklyRecovery => weekly?.ResetAt,
            _ => null
        };
        if (route is null || key is null ||
            (lane==TelegramNotificationLane.FiveHourRecovery &&
                fiveHour?.Status!="MOCK_PENDING") ||
            (lane==TelegramNotificationLane.WeeklyRecovery &&
                weekly?.Status!="MOCK_PENDING") ||
            observed is null || reset is null ||
            observed.Value>now+FutureSkew ||
            now-observed.Value>MaximumSampleAge ||
            reset.Value<=observed.Value)
            return Hold(WindowsTelegramLaunchHoldReason.EventNotVerifiedFresh);
        if (!IsDriveLocalDirectory(candidateScriptsDirectory) ||
            !IsDriveLocalDirectory(existingBotDirectory))
            return Hold(WindowsTelegramLaunchHoldReason.PathsNotVerified);

        var expected=lane switch
        {
            TelegramNotificationLane.FiveHourRecovery =>
                ("send-fivehour100-existing-monitor-bot.ps1",QuotaTelegramBot.MonitorMuted),
            TelegramNotificationLane.WeeklyRecovery =>
                ("send-weekly100-existing-alert-bot.ps1",QuotaTelegramBot.AlertImportant),
            _ => ("",QuotaTelegramBot.MonitorMuted)
        };
        if (!string.Equals(route.ProposedSenderScriptName,expected.Item1,
                StringComparison.Ordinal) ||
            route.Destination!=expected.Item2)
            return Hold(WindowsTelegramLaunchHoldReason.BotLaneMismatch);

        var script=Path.Combine(candidateScriptsDirectory!,expected.Item1);
        var arguments=new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-File",
            script,
            "-LegacyBotDirectory",
            existingBotDirectory!,
            "-EventKey",
            key,
            "-Message",
            route.Text
        };

        // Deliberately NO -ApprovedSingleSend or -WindowsSenderVerified.
        // A real runner/authorization authority is not implemented.
        return new(WindowsTelegramLaunchHoldReason.AwaitingRealApprovalAndAuthority,
            true,script,arguments);
    }

    // Preview only. Reject remote shares, device paths and dot-dot traversal.
    // A later production implementation must also bind the specific paths to
    // fresh OS file identity + pinned SHA verification immediately before use.
    private static bool IsDriveLocalDirectory(string? directory)
    {
        if (directory is not {Length:>=3} ||
            !char.IsAsciiLetter(directory[0]) || directory[1]!=':' ||
            directory[2] is not ('\\' or '/') ||
            !Path.IsPathFullyQualified(directory))
            return false;
        return !directory.Split(new[]{'\\','/'},StringSplitOptions.None)
            .Any(segment=>segment is "." or "..");
    }

    private static WindowsTelegramLaunchPreviewResult Hold(
        WindowsTelegramLaunchHoldReason reason) =>
        new(reason,false,null,Array.Empty<string>());
}

public enum WindowsTelegramLaunchHoldReason
{
    LocalPreferenceOffOrViewer,
    DeclaredWindowsOwnerMissing,
    SharedLeaseSimulationMissing,
    BotScriptPreflightIncomplete,
    EventNotVerifiedFresh,
    PathsNotVerified,
    BotLaneMismatch,
    AwaitingRealApprovalAndAuthority
}

public sealed record WindowsTelegramLaunchPreviewResult(
    WindowsTelegramLaunchHoldReason Reason,
    bool StaticCommandPreviewPrepared,
    string? CandidateScriptPath,
    IReadOnlyList<string> UnarmedArguments)
{
    // A command preview never independently grants real Telegram delivery.
    public bool ActualSendAuthorized=>false;
}
