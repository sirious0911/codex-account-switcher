using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Windows-only, permanently UNARMED native reservation fixture.
/// The only write target is an already-provisioned, explicitly private
/// directory below a dedicated OS-TEMP handle-test folder.
/// Locks the root and lane directory handles against delete/rename during
/// the test, creates exactly one claim with CREATE_NEW, and inspects its
/// owner/DACL/FileId from the SAME newly created handle before writing.
/// This still does not establish a distributed sender lease, actual consent,
/// a production durable outbox, or safety across unpinned OS-TEMP ancestors.
/// </summary>
public sealed class WindowsTelegramSameHandleReservationTrial
{
    private const string Prefix="codex-switcher-telegram-handle-test-";
    private const uint ReadAttributes=0x80;
    private const uint ReadControl=0x20000;
    private const uint GenericWrite=0x40000000;
    private const uint ShareReadWrite=3; // deny DELETE sharing on parents
    private const uint OpenExisting=3;
    private const uint CreateNew=1;
    private const uint BackupSemantics=0x02000000;
    private const uint OpenReparsePoint=0x00200000;
    private const uint WriteThrough=0x80000000;
    private readonly string root;

    public WindowsTelegramSameHandleReservationTrial(string scratchRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(scratchRoot));
        var tmp=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var leaf=Path.GetFileName(full);
        if(!OperatingSystem.IsWindows() ||
            !string.Equals(Path.GetDirectoryName(full),tmp,
                StringComparison.OrdinalIgnoreCase) ||
            !leaf.StartsWith(Prefix,StringComparison.Ordinal) ||
            leaf.Length!=Prefix.Length+32 ||
            !leaf[Prefix.Length..].All(c=>c is >= 'a' and <= 'f' or
                >= '0' and <= '9'))
            throw new ArgumentException("Only dedicated OS-TEMP handle-test roots permitted",
                nameof(scratchRoot));
        root=full;
    }

    public WindowsSameHandleReservationInspection ReserveForOfflineTest(
        TelegramNotificationPreferences? settings,
        WindowsSenderReadinessInspection? audit,
        TelegramNotificationLane lane,string? eventKey,
        string? localDeviceId,
        TelegramProductionClaimForReview? syntheticClaim,
        DateTimeOffset nowUtc)
    {
        var gate=WindowsTelegramProductionBoundaryReview.Inspect(
            settings,audit,lane,eventKey,localDeviceId,syntheticClaim,nowUtc);
        if(gate.Reason!=TelegramProductionBoundaryHold.ClaimsNotIndependentlyAttested)
            return new(WindowsSameHandleReservationStatus.PreconditionsHeld);
        var folder=Path.Combine(root,lane==TelegramNotificationLane.FiveHourRecovery
            ?"five-hour":"weekly");

        // Never provision the root or lane in this class.
        if(WindowsTelegramHandleIdentityProbe.InspectReadOnly(root,true).Status!=
                WindowsHandleIdentityStatus.HandleIdentityObservedOnly ||
           WindowsTelegramHandleIdentityProbe.InspectReadOnly(folder,true).Status!=
                WindowsHandleIdentityStatus.HandleIdentityObservedOnly)
            return new(WindowsSameHandleReservationStatus.UntrustedParent);

        SafeFileHandle? rootHandle=null;
        SafeFileHandle? laneHandle=null;
        IntPtr descriptorBuffer=IntPtr.Zero;
        try
        {
            rootHandle=OpenDirectory(root);
            laneHandle=OpenDirectory(folder);
            if(rootHandle.IsInvalid || laneHandle.IsInvalid)
                return new(WindowsSameHandleReservationStatus.UntrustedParent);
            var rootReview=WindowsTelegramSameHandleReview.InspectOpenedHandle(
                rootHandle,true);
            var laneReview=WindowsTelegramSameHandleReview.InspectOpenedHandle(
                laneHandle,true);
            if(rootReview.Status!=WindowsSameHandleStatus.HandleAclAndIdObservedOnly ||
                laneReview.Status!=WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
                return new(WindowsSameHandleReservationStatus.UntrustedParent);

            var marker=Path.Combine(folder,eventKey!+".reservation");
            var fileSecurity=PrivateSecurity();
            var descriptorBytes=fileSecurity.GetSecurityDescriptorBinaryForm();
            descriptorBuffer=Marshal.AllocHGlobal(descriptorBytes.Length);
            Marshal.Copy(descriptorBytes,0,descriptorBuffer,descriptorBytes.Length);
            var attrs=new SecurityAttributes {
                Length=Marshal.SizeOf<SecurityAttributes>(),
                Descriptor=descriptorBuffer,InheritHandle=false
            };

            // Claim reservation MUST be written only through this exact
            // CREATE_NEW handle. Existing/partial claims are never deleted.
            using var file=CreateFileWWithSecurity(marker,
                GenericWrite|ReadControl|ReadAttributes,0,ref attrs,
                CreateNew,OpenReparsePoint|WriteThrough,IntPtr.Zero);
            if(file.IsInvalid)
            {
                var error=Marshal.GetLastWin32Error();
                return new(error is 80 or 183 or 32
                    ?WindowsSameHandleReservationStatus.AlreadyClaimedOrUncertain
                    :WindowsSameHandleReservationStatus.StorageUntrusted);
            }
            var claimReview=WindowsTelegramSameHandleReview.InspectOpenedHandle(
                file,false);
            if(claimReview.Status!=WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
                return new(WindowsSameHandleReservationStatus.ClaimHandleUntrusted);
            var stillRoot=WindowsTelegramSameHandleReview.InspectOpenedHandle(
                rootHandle,true,rootReview.FileId);
            var stillLane=WindowsTelegramSameHandleReview.InspectOpenedHandle(
                laneHandle,true,laneReview.FileId);
            if(stillRoot.Status!=WindowsSameHandleStatus.HandleAclAndIdObservedOnly ||
                stillLane.Status!=WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
                return new(WindowsSameHandleReservationStatus.UntrustedParent);

            var deviceHash=Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(localDeviceId!))).ToLowerInvariant();
            var text=JsonSerializer.Serialize(new {
                version=1,
                lane=lane==TelegramNotificationLane.FiveHourRecovery
                    ?"five-hour":"weekly",
                eventKey,
                deviceSha256=deviceHash,
                observedAtUtcMs=nowUtc.ToUnixTimeMilliseconds(),
                status="HANDLE_BOUND_FIXTURE_ONLY_NO_SEND"
            });
            using var stream=new FileStream(file,FileAccess.Write,4096,
                isAsync:false);
            var data=Encoding.UTF8.GetBytes(text);
            stream.Write(data);
            stream.Flush(flushToDisk:true);
            var finalClaim=WindowsTelegramSameHandleReview.InspectOpenedHandle(
                file,false,claimReview.FileId);
            return finalClaim.Status==WindowsSameHandleStatus.HandleAclAndIdObservedOnly
                ?new(WindowsSameHandleReservationStatus.FixtureClaimOnly,
                    claimReview.FileId,true)
                :new(WindowsSameHandleReservationStatus.ClaimHandleUntrusted);
        }
        catch(Exception ex) when(ex is ArgumentException or IOException or
            System.Security.SecurityException or UnauthorizedAccessException or
            NotSupportedException or InvalidOperationException)
        {
            return new(WindowsSameHandleReservationStatus.StorageUntrusted);
        }
        finally
        {
            if(descriptorBuffer!=IntPtr.Zero) Marshal.FreeHGlobal(descriptorBuffer);
            laneHandle?.Dispose();
            rootHandle?.Dispose();
        }
    }

    private static SafeFileHandle OpenDirectory(string path)=>
        CreateFileW(path,ReadAttributes|ReadControl,ShareReadWrite,
            IntPtr.Zero,OpenExisting,
            BackupSemantics|OpenReparsePoint,IntPtr.Zero);

    private static FileSecurity PrivateSecurity()
    {
        var user=WindowsIdentity.GetCurrent().User ??
            throw new UnauthorizedAccessException("Missing Windows user SID");
        var acl=new FileSecurity();
        acl.SetOwner(user);
        acl.SetAccessRuleProtection(true,false);
        acl.AddAccessRule(new FileSystemAccessRule(user,
            FileSystemRights.FullControl,AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),
            FileSystemRights.FullControl,AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),
            FileSystemRights.FullControl,AccessControlType.Allow));
        return acl;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,
        SetLastError=true,ExactSpelling=true)]
    private static extern SafeFileHandle CreateFileW(
        string path,uint access,uint share,IntPtr attributes,
        uint mode,uint flags,IntPtr template);

    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,
        SetLastError=true,ExactSpelling=true)]
    private static extern SafeFileHandle CreateFileWWithSecurity(
        string path,uint access,uint share,ref SecurityAttributes attributes,
        uint mode,uint flags,IntPtr template);
}

public enum WindowsSameHandleReservationStatus
{
    PreconditionsHeld,
    UntrustedParent,
    AlreadyClaimedOrUncertain,
    StorageUntrusted,
    ClaimHandleUntrusted,
    FixtureClaimOnly
}

public sealed record WindowsSameHandleReservationInspection(
    WindowsSameHandleReservationStatus Status,
    WindowsHandleFileId? FileId=null,
    bool FixtureWasWritten=false)
{
    public bool ProductionReservationAuthorized=>false;
    public bool TelegramSendAuthorized=>false;
}
