using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Reviews a sanitized READ-ONLY snapshot of one Windows host.
/// This cannot inspect Mac devices, prove globally exclusive ownership,
/// or grant Telegram sending under any circumstances.
/// </summary>
public static class WindowsTelegramLocalSenderReadiness
{
    public static WindowsSenderReadinessInspection Inspect(
        string? sanitizedAuditJson, DateTimeOffset nowUtc)
    {
        if (!OperatingSystem.IsWindows())
            return new(WindowsSenderReadinessHold.UnsupportedPlatform);
        if (nowUtc.Offset!=TimeSpan.Zero)
            return new(WindowsSenderReadinessHold.UntrustedClock);
        if (string.IsNullOrWhiteSpace(sanitizedAuditJson) ||
            sanitizedAuditJson.Length>4096)
            return new(WindowsSenderReadinessHold.InvalidAudit);

        WindowsTelegramLocalAuditSnapshot? snapshot;
        try
        {
            snapshot=JsonSerializer.Deserialize<WindowsTelegramLocalAuditSnapshot>(
                sanitizedAuditJson,new JsonSerializerOptions {
                    MaxDepth=4,PropertyNameCaseInsensitive=false
                });
        }
        catch (Exception e) when(e is JsonException or
            NotSupportedException or ArgumentException)
        {
            return new(WindowsSenderReadinessHold.InvalidAudit);
        }
        if (snapshot is null || snapshot.Schema!=1 ||
            !snapshot.WindowsOs || snapshot.RealSendAuthorized)
            return new(WindowsSenderReadinessHold.InvalidAudit);
        if (snapshot.CapturedAtUtc.Offset!=TimeSpan.Zero ||
            snapshot.CapturedAtUtc>nowUtc.AddSeconds(30) ||
            snapshot.CapturedAtUtc<nowUtc.AddMinutes(-2))
            return new(WindowsSenderReadinessHold.StaleOrUntrustedObservation);

        if (!snapshot.AppProcessInventoryComplete ||
            snapshot.RunningSwitcherCount!=1)
            return new(WindowsSenderReadinessHold.LocalAppNotSoleInstance);
        if (!snapshot.InstalledReleaseMatchesExpected ||
            !snapshot.InstalledExeShaMatchesExpected)
            return new(WindowsSenderReadinessHold.InstalledBinaryUnverified);
        if (!snapshot.StartupEntryRead ||
            !snapshot.StartupMatchesRunningApp)
            return new(WindowsSenderReadinessHold.AutostartUnverified);
        if (!snapshot.NotificationPreferenceRead ||
            snapshot.NotificationPreferenceSource is not
                ("ExplicitValues" or "MissingKeyDefaultOff" or
                 "MissingValuesDefaultOff"))
            return new(WindowsSenderReadinessHold.NotificationSettingsUnverified);
        // An absent key explicitly defaults to Viewer/OFF in NativeSettings.
        // A partially missing key is safe to classify OFF only if neither
        // locally observed request flag asserts that sending is requested.
        // Never allow a fabricated default-source report to assert ON.
        if (snapshot.NotificationPreferenceSource!="ExplicitValues" &&
            (snapshot.NotificationRequested || snapshot.SenderRoleRequested))
            return new(WindowsSenderReadinessHold.NotificationSettingsUnverified);
        if (!snapshot.NotificationRequested || !snapshot.SenderRoleRequested)
            return new(WindowsSenderReadinessHold.SenderCurrentlyOff);
        if (!snapshot.ScheduledTaskInventoryComplete ||
            snapshot.PossibleScheduledSenderTasks<0)
            return new(WindowsSenderReadinessHold.ScheduledTaskInventoryUnknown);
        if (snapshot.PossibleScheduledSenderTasks>0)
            return new(WindowsSenderReadinessHold.PossibleCompetingSchedulerTask);

        // The MacSenderVerifiedOff bit in a caller-supplied JSON object is
        // *not* independently authenticated. It cannot authorize sending.
        return new(WindowsSenderReadinessHold.RemoteSenderUnverifiable,true);
    }
}

public enum WindowsSenderReadinessHold
{
    UnsupportedPlatform,
    UntrustedClock,
    InvalidAudit,
    StaleOrUntrustedObservation,
    LocalAppNotSoleInstance,
    InstalledBinaryUnverified,
    AutostartUnverified,
    NotificationSettingsUnverified,
    SenderCurrentlyOff,
    ScheduledTaskInventoryUnknown,
    PossibleCompetingSchedulerTask,
    RemoteSenderUnverifiable
}

public sealed record WindowsSenderReadinessInspection(
    WindowsSenderReadinessHold Reason,
    bool LocalChecksLookAcceptable=false)
{
    public bool ActualTelegramSendAuthorized=>false;
}

public sealed record WindowsTelegramLocalAuditSnapshot(
    int Schema,
    DateTimeOffset CapturedAtUtc,
    bool WindowsOs,
    bool AppProcessInventoryComplete,
    int RunningSwitcherCount,
    bool InstalledReleaseMatchesExpected,
    bool InstalledExeShaMatchesExpected,
    bool StartupEntryRead,
    bool StartupMatchesRunningApp,
    bool NotificationPreferenceRead,
    string? NotificationPreferenceSource,
    bool NotificationRequested,
    bool SenderRoleRequested,
    bool ScheduledTaskInventoryComplete,
    int PossibleScheduledSenderTasks,
    bool MacSenderVerifiedOff,
    bool RealSendAuthorized);
