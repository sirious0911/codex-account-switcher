using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Non-mutating Windows ACL/handle-ID review for a future outbox folder.
/// Uses the bounded native-handle probe to bracket a path-based ACL read.
/// This is NOT a race-free handle-bound ACL proof, shared-host authority,
/// production provisioning, or permission to reserve/send.
/// </summary>
public static class WindowsTelegramOutboxAclReview
{
    public static WindowsOutboxAclInspection InspectReadOnly(string? path)
    {
        if (!OperatingSystem.IsWindows())
            return new(WindowsOutboxAclStatus.UnsupportedPlatform);
        var before=WindowsTelegramHandleIdentityProbe.InspectReadOnly(path,true);
        if (before.Status!=WindowsHandleIdentityStatus.HandleIdentityObservedOnly ||
            before.ObservedId is null)
            return new(WindowsOutboxAclStatus.NativeDirectoryNotVerified);
        try
        {
            var ownerSid=WindowsIdentity.GetCurrent().User;
            if (ownerSid is null)
                return new(WindowsOutboxAclStatus.CurrentUserUnavailable);
            var security=new DirectoryInfo(path!).GetAccessControl(
                AccessControlSections.Owner|AccessControlSections.Access);
            var acl=InspectDescriptor(security,ownerSid);
            var after=WindowsTelegramHandleIdentityProbe.InspectReadOnly(
                path,true,before.ObservedId);
            if (after.Status!=WindowsHandleIdentityStatus.HandleIdentityObservedOnly ||
                after.ObservedId!=before.ObservedId)
                return new(WindowsOutboxAclStatus.NativeIdentityChangedDuringReview);
            return acl;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or
            UnauthorizedAccessException or System.Security.SecurityException or
            InvalidOperationException or NotSupportedException or
            IdentityNotMappedException or Win32Exception)
        {
            return new(WindowsOutboxAclStatus.AclInspectionUnavailable);
        }
    }

    /// <summary>
    /// Pure descriptor classifier. Input descriptors can be synthetic or forged:
    /// the result MUST NOT be used as proof of real production ACLs.
    /// </summary>
    public static WindowsOutboxAclInspection InspectDescriptor(
        DirectorySecurity? descriptor, SecurityIdentifier? currentSid)
    {
        if (descriptor is null || currentSid is null)
            return new(WindowsOutboxAclStatus.CurrentUserUnavailable);
        try
        {
            var owner=descriptor.GetOwner(typeof(SecurityIdentifier))
                as SecurityIdentifier;
            if (owner is null || !owner.Equals(currentSid))
                return new(WindowsOutboxAclStatus.OwnerSidMismatch);
            if (!descriptor.AreAccessRulesProtected)
                return new(WindowsOutboxAclStatus.InheritanceNotProtected);
            var rules=descriptor.GetAccessRules(
                includeExplicit:true,includeInherited:true,
                targetType:typeof(SecurityIdentifier));
            FileSystemRights userAllowed=0;
            foreach(FileSystemAccessRule rule in rules)
            {
                if (rule.IsInherited)
                    return new(WindowsOutboxAclStatus.InheritedRulePresent);
                var trustee=rule.IdentityReference as SecurityIdentifier;
                if (trustee is null || !Trusted(trustee,currentSid))
                    return new(WindowsOutboxAclStatus.UnexpectedAccessPrincipal);
                if (rule.AccessControlType!=AccessControlType.Allow)
                    return new(WindowsOutboxAclStatus.ExplicitDenyRequiresReview);
                if (trustee.Equals(currentSid))
                    userAllowed|=rule.FileSystemRights;
            }
            if ((userAllowed&FileSystemRights.Modify)!=FileSystemRights.Modify)
                return new(WindowsOutboxAclStatus.UserModifyPermissionMissing);

            // Neither a synthetic descriptor nor a single path-read has
            // pinned the ACL to an open writable outbox handle.
            return new(WindowsOutboxAclStatus.LocalAclCandidateOnly,true);
        }
        catch (Exception ex) when (ex is ArgumentException or
            System.Security.SecurityException or
            InvalidOperationException or NotSupportedException)
        {
            return new(WindowsOutboxAclStatus.AclInspectionUnavailable);
        }
    }

    private static bool Trusted(SecurityIdentifier sid,SecurityIdentifier user)=>
        sid.Equals(user) || sid.Value=="S-1-5-18" ||
        sid.Value=="S-1-5-32-544";
}

public enum WindowsOutboxAclStatus
{
    UnsupportedPlatform,
    NativeDirectoryNotVerified,
    NativeIdentityChangedDuringReview,
    CurrentUserUnavailable,
    OwnerSidMismatch,
    InheritanceNotProtected,
    InheritedRulePresent,
    UnexpectedAccessPrincipal,
    ExplicitDenyRequiresReview,
    UserModifyPermissionMissing,
    AclInspectionUnavailable,
    LocalAclCandidateOnly
}

public sealed record WindowsOutboxAclInspection(
    WindowsOutboxAclStatus Status,
    bool LocalAclLooksPrivateOnly=false)
{
    public bool ProductionOutboxProvisioned=>false;
    public bool DurableReservationAuthorized=>false;
    public bool TelegramSendAuthorized=>false;
}
