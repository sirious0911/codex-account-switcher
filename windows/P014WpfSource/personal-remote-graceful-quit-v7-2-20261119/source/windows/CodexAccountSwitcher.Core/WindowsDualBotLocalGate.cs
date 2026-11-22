namespace CodexAccountSwitcher.Core;

/// <summary>
/// Combines the role-bound local preflight for both quota-notification bots.
/// No credentials are read or decrypted, no process or network is invoked.
/// Even a passing check MUST NOT be treated as live-send authorization.
/// </summary>
public sealed class WindowsDualBotLocalGate
{
    private readonly WindowsTelegramSenderPreflight preflight = new();

    // Reviewed SOURCE hashes, not token/chat data. Source changes require
    // a separately reviewed update of these pins before any live rollout.
    private const string FiveHourSenderSha256 =
        "a3dcebc90a6914c7dc81eca6af24d12571a9e8657f70314f4daffb9ea871a081";
    private const string WeeklySenderSha256 =
        "fc2b11b893311e2ec0a9f0e27020988895695ddc600fe1a231552637062c05da";
    private const string FiveHourLegacySha256 =
        "3e5c20278fb5de2a16435c668b6d5b317aaf57d849405a7cb41f86445475d948";
    private const string WeeklyLegacySha256 =
        "1a1c3ae6faab37e821216ef0e2b6b9b7718daf261049b8cddd15722861f1c195";

    /// <summary>
    /// Compare both exact locally reviewed script versions, not caller
    /// supplied runtime-computed hashes. Still NO live-send authorization.
    /// </summary>
    public WindowsDualBotLocalGateResult InspectPinnedWindowsInstallation(
        string scriptsDirectory, string existingBotDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(existingBotDirectory);
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var monitor = new WindowsTelegramSenderPreflightPaths(
            executable,
            Path.Combine(scriptsDirectory, "send-fivehour100-existing-monitor-bot.ps1"),
            existingBotDirectory,
            FiveHourSenderSha256, FiveHourLegacySha256);
        var alert = new WindowsTelegramSenderPreflightPaths(
            executable,
            Path.Combine(scriptsDirectory, "send-weekly100-existing-alert-bot.ps1"),
            existingBotDirectory,
            WeeklySenderSha256, WeeklyLegacySha256);
        return Inspect(monitor, alert);
    }

    public WindowsDualBotLocalGateResult Inspect(
        WindowsTelegramSenderPreflightPaths monitor,
        WindowsTelegramSenderPreflightPaths alert)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(alert);

        // Both lanes must refer to distinct sender scripts, with a shared
        // original bot directory. This check is not a distributed owner lease.
        if (string.Equals(monitor.ReceiptSenderScript, alert.ReceiptSenderScript,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(monitor.LegacyBotDirectory, alert.LegacyBotDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                WindowsDualBotLocalGateStatus.BotPairConflict,
                WindowsTelegramSenderPreflightStatus.BotRoleMismatch,
                WindowsTelegramSenderPreflightStatus.BotRoleMismatch);
        }

        var m = preflight.Inspect(monitor, QuotaTelegramBot.MonitorMuted).Status;
        var a = preflight.Inspect(alert, QuotaTelegramBot.AlertImportant).Status;
        return new(
            m == WindowsTelegramSenderPreflightStatus.LocalArtifactsVerifiedOnly &&
            a == WindowsTelegramSenderPreflightStatus.LocalArtifactsVerifiedOnly
                ? WindowsDualBotLocalGateStatus.LocalArtifactsVerifiedOnly
                : WindowsDualBotLocalGateStatus.AtLeastOneBotFailed,
            m, a);
    }
}

public enum WindowsDualBotLocalGateStatus
{
    BotPairConflict,
    AtLeastOneBotFailed,
    LocalArtifactsVerifiedOnly
}

public sealed record WindowsDualBotLocalGateResult(
    WindowsDualBotLocalGateStatus Status,
    WindowsTelegramSenderPreflightStatus MonitorStatus,
    WindowsTelegramSenderPreflightStatus AlertStatus)
{
    public bool BothLocalArtifactsVerified =>
        Status == WindowsDualBotLocalGateStatus.LocalArtifactsVerifiedOnly;

    // This guard intentionally has no success path. Local stat/hash
    // evidence cannot prove Mac inactivity, user consent, or network receipt.
    public bool ActualSendAuthorized => false;
}
