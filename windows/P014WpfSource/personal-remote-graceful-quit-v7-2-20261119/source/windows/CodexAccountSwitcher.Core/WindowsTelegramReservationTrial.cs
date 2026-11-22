using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// A production-shaped reservation TRIAL. It only writes under a specially
/// named direct OS-temp child; it cannot use the live LocalAppData outbox.
/// It has NO sender, credential, account-switch, approval issuer, or shared
/// Windows/Mac ownership authority. Do not promote this trial to production.
/// </summary>
public sealed class WindowsTelegramReservationTrial
{
    private const string Prefix="codex-switcher-telegram-reservation-trial-";
    private readonly string root;

    public WindowsTelegramReservationTrial(string scratchRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        var canonical=Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(scratchRoot));
        var temp=Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        var leaf=Path.GetFileName(canonical);
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(Path.GetDirectoryName(canonical),temp,
                StringComparison.OrdinalIgnoreCase) ||
            !leaf.StartsWith(Prefix,StringComparison.Ordinal) ||
            leaf.Length<=Prefix.Length ||
            !leaf[Prefix.Length..].All(c=>c is >= 'a' and <= 'f' or
                >= '0' and <= '9'))
            throw new ArgumentException(
                "Reservation trial requires a dedicated direct OS-temp test path",
                nameof(scratchRoot));
        root=canonical;
    }

    public TelegramReservationTrialInspection ReserveForOfflineTrial(
        TelegramNotificationPreferences? preferences,
        WindowsSenderReadinessInspection? localAudit,
        TelegramNotificationLane lane,
        string? eventKey,
        string? deviceId,
        TelegramProductionClaimForReview? untrustedClaim,
        DateTimeOffset nowUtc)
    {
        var gate=WindowsTelegramProductionBoundaryReview.Inspect(
            preferences,localAudit,lane,eventKey,deviceId,untrustedClaim,nowUtc);
        // The explicitly named non-authorizing state is the only one that can
        // be simulated. Even its "positive" claims are UNTRUSTED.
        if (gate.Reason!=TelegramProductionBoundaryHold.ClaimsNotIndependentlyAttested ||
            !gate.LocallyReviewable)
            return new(TelegramReservationTrialStatus.PreconditionsHeld,
                gate.Reason);
        var sub=lane==TelegramNotificationLane.FiveHourRecovery
            ? "five-hour" : "weekly";
        try
        {
            Directory.CreateDirectory(root);
            if (IsReparse(root))
                return new(TelegramReservationTrialStatus.StorageUntrusted,
                    gate.Reason);
            var folder=Path.Combine(root,sub);
            Directory.CreateDirectory(folder);
            if (IsReparse(folder))
                return new(TelegramReservationTrialStatus.StorageUntrusted,
                    gate.Reason);
            var marker=Path.Combine(folder,eventKey!+".reservation");
            // Atomic CreateNew is the only persistence primitive here.
            // Partial or empty files continue to block every replay.
            using var file=new FileStream(marker,FileMode.CreateNew,
                FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
            var deviceDigest=Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(deviceId!)))
                .ToLowerInvariant();
            var payload=JsonSerializer.Serialize(new TrialReservation(
                1,sub,eventKey!,deviceDigest,
                nowUtc.ToUnixTimeMilliseconds(),"UNARMED_RESERVED_NO_SEND"));
            file.Write(Encoding.UTF8.GetBytes(payload));
            file.Flush(flushToDisk:true);
            return new(TelegramReservationTrialStatus.ReservedForOfflineTrialOnly,
                gate.Reason,true);
        }
        catch (IOException)
        {
            // Existing or interrupted reservation: never auto retry/delete.
            return new(TelegramReservationTrialStatus.ExistingOrUncertainClaim,
                gate.Reason);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or
            NotSupportedException or InvalidOperationException)
        {
            return new(TelegramReservationTrialStatus.StorageUntrusted,
                gate.Reason);
        }
    }

    private static bool IsReparse(string directory) =>
        (File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0;

    private sealed record TrialReservation(
        int Schema,string Lane,string EventKey,string DeviceSha256,
        long ReservedAtUnixMs,string Status);
}

public enum TelegramReservationTrialStatus
{
    PreconditionsHeld,
    StorageUntrusted,
    ExistingOrUncertainClaim,
    ReservedForOfflineTrialOnly
}

public sealed record TelegramReservationTrialInspection(
    TelegramReservationTrialStatus Status,
    TelegramProductionBoundaryHold? GateReason,
    bool LocalTrialClaimCreated=false)
{
    public bool ProductionReservationAuthorized=>false;
    public bool ActualTelegramSendAuthorized=>false;
}
