using System.Security.AccessControl;
using System.Security.Principal;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// READ-ONLY inspection of a future Windows LocalAppData outbox location.
/// The directory is NEVER created here and NO Telegram transport is exposed.
/// Even a locally acceptable directory does not attest Mac ownership, actual
/// user consent, OS-handle identity at reserve time, or remote deduplication.
/// </summary>
public static class WindowsTelegramProductionOutboxPreflight
{
    public static WindowsOutboxPreflightInspection Inspect()
    {
        if (!OperatingSystem.IsWindows())
            return new(WindowsOutboxPreflightHold.UnsupportedPlatform);
        try
        {
            var baseDirectory=Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDirectory) ||
                !Path.IsPathFullyQualified(baseDirectory))
                return new(WindowsOutboxPreflightHold.LocationUntrusted);
            var baseFull=Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(baseDirectory));
            var root=Path.GetPathRoot(baseFull);
            if (root is null || baseFull==root ||
                !string.Equals(new DirectoryInfo(baseFull).FullName,baseFull,
                    StringComparison.OrdinalIgnoreCase))
                return new(WindowsOutboxPreflightHold.LocationUntrusted);
            var drive=new DriveInfo(root);
            if (drive.DriveType!=DriveType.Fixed)
                return new(WindowsOutboxPreflightHold.LocationUntrusted);
            var components=new[]{
                baseFull,
                Path.Combine(baseFull,"Sirious0911"),
                Path.Combine(baseFull,"Sirious0911","CodexAccountSwitcher"),
                Path.Combine(baseFull,"Sirious0911","CodexAccountSwitcher",
                    "TelegramOutbox")
            };
            foreach (var path in components)
            {
                if (!Directory.Exists(path))
                    return new(WindowsOutboxPreflightHold.DirectoryNotProvisioned);
                var attributes=File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory)==0 ||
                    (attributes & FileAttributes.ReparsePoint)!=0)
                    return new(WindowsOutboxPreflightHold.DirectoryIdentityUntrusted);
            }
            var sid=WindowsIdentity.GetCurrent().User;
            if (sid is null)
                return new(WindowsOutboxPreflightHold.OwnerIdentityUntrusted);

            // Inspect only the outbox and its private app-owned ancestor.
            // LocalAppData's broad ancestor rules must be separately audited
            // before live deployment. This is a preview, not a proof of secure
            // file-handle identity or an operational authorization.
            foreach (var path in components.Skip(1))
            {
                var security=new DirectoryInfo(path).GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access);
                var owner=security.GetOwner(typeof(SecurityIdentifier))
                    as SecurityIdentifier;
                if (owner is null || !owner.Equals(sid))
                    return new(WindowsOutboxPreflightHold.OwnerIdentityUntrusted);
                if (!security.AreAccessRulesProtected)
                    return new(WindowsOutboxPreflightHold.InheritedAclNeedsReview);
                var rules=security.GetAccessRules(
                    includeExplicit:true,includeInherited:true,
                    targetType:typeof(SecurityIdentifier));
                foreach (FileSystemAccessRule rule in rules)
                {
                    if (rule.AccessControlType != AccessControlType.Allow)
                        continue;
                    var trustee=rule.IdentityReference as SecurityIdentifier;
                    if (trustee is null || !IsTrustedTrustee(trustee,sid))
                        return new(WindowsOutboxPreflightHold.AccessControlNeedsReview);
                }
            }
            // This is a read-only candidate: ACL and owner can change later.
            return new(WindowsOutboxPreflightHold.LocalDirectoryCandidateOnly,true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or
            NotSupportedException or InvalidOperationException)
        {
            return new(WindowsOutboxPreflightHold.OsInspectionUnavailable);
        }
    }

    private static bool IsTrustedTrustee(SecurityIdentifier trustee,
        SecurityIdentifier currentUser)
        => trustee.Equals(currentUser) ||
            trustee.Value=="S-1-5-18" || // LOCAL SYSTEM
            trustee.Value=="S-1-5-32-544"; // BUILTIN Administrators
}

public enum WindowsOutboxPreflightHold
{
    UnsupportedPlatform,
    LocationUntrusted,
    DirectoryNotProvisioned,
    DirectoryIdentityUntrusted,
    OwnerIdentityUntrusted,
    InheritedAclNeedsReview,
    AccessControlNeedsReview,
    OsInspectionUnavailable,
    LocalDirectoryCandidateOnly
}

public sealed record WindowsOutboxPreflightInspection(
    WindowsOutboxPreflightHold Reason,
    bool LocalAclCandidateOnly=false)
{
    /// <summary>Local path checks cannot authorize a send or durable reservation.</summary>
    public bool ProductionReservationAuthorized=>false;
    public bool TelegramSendAuthorized=>false;
}
