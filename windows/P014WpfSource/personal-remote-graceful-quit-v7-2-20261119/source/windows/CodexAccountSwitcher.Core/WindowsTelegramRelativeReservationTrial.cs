using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Native FILE_CREATE reservation trial strictly inside a private OS-TEMP fixture.
/// Every file is created relative to an already opened and ACL-verified lane
/// directory handle. No path-based write, no production outbox, no send.
/// This is not a distributed ownership protocol or an authorization issuer.
/// </summary>
public sealed class WindowsTelegramRelativeReservationTrial
{
    private const string FixturePrefix="codex-switcher-telegram-handle-test-";
    private const uint ReadAttributes=0x80;
    private const uint ReadControl=0x20000;
    private const uint WriteData=0x2;
    private const uint Synchronize=0x100000;
    private const uint ShareReadWrite=0x3;
    private const uint OpenExisting=3;
    private const uint BackupSemantics=0x02000000;
    private const uint OpenReparsePoint=0x00200000;
    private const uint ObjCaseInsensitive=0x40;
    private const uint NtOpen=1;
    private const uint NtCreate=2;
    private const uint NtDirectoryFile=1;
    private const uint NtWriteThrough=0x2;
    private const uint NtSynchronousIoNonalert=0x20;
    private const uint NtNonDirectoryFile=0x40;
    private const uint NtOpenReparse=0x00200000;
    private const uint NtBackupIntent=0x00004000;
    private readonly string root;

    public WindowsTelegramRelativeReservationTrial(string isolatedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isolatedRoot);
        if(!OperatingSystem.IsWindows() ||
            !Path.IsPathFullyQualified(isolatedRoot) ||
            isolatedRoot.IndexOfAny(['\0','*','?'])>=0 ||
            isolatedRoot.Replace('/','\\').Split('\\')
                .Any(x=>x is "." or ".."))
            throw new ArgumentException("Invalid isolated Windows test root",
                nameof(isolatedRoot));
        var path=Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(isolatedRoot));
        var temp=Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        var name=Path.GetFileName(path);
        if(!string.Equals(Path.GetDirectoryName(path),temp,
                StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(FixturePrefix,StringComparison.Ordinal) ||
            name.Length!=FixturePrefix.Length+32 ||
            !name[FixturePrefix.Length..].All(c=>c is >= '0' and <= '9'
                or >= 'a' and <= 'f'))
            throw new ArgumentException("Only direct GUID-named OS-TEMP test folder allowed",
                nameof(isolatedRoot));
        root=path;
    }

    public WindowsRelativeReservationInspection ReserveOfflineOnce(
        TelegramNotificationPreferences? preferences,
        WindowsSenderReadinessInspection? localAudit,
        TelegramNotificationLane lane,string? eventKey,
        string? deviceId,
        TelegramProductionClaimForReview? claim,
        DateTimeOffset nowUtc)
    {
        var gate=WindowsTelegramProductionBoundaryReview.Inspect(
            preferences,localAudit,lane,eventKey,deviceId,claim,nowUtc);
        if(gate.Reason!=TelegramProductionBoundaryHold.ClaimsNotIndependentlyAttested ||
            !gate.LocallyReviewable)
            return new(WindowsRelativeReservationStatus.PreconditionsHeld);

        // The native ancestor scanner opens by RootDirectory and has no write
        // authority. Require it to observe a real private test folder.
        var scoped=WindowsTelegramRootedChainReview
            .InspectFixtureReadOnly(root);
        if(scoped.Status!=WindowsRootedChainStatus.HandleRelativeCandidateOnly ||
            scoped.Snapshot?.Identities.LastOrDefault() is not { } pinnedRoot)
            return new(WindowsRelativeReservationStatus.ParentUntrusted);

        var rootHandle=CreateFileW(root,ReadAttributes|ReadControl,
            ShareReadWrite,IntPtr.Zero,OpenExisting,
            BackupSemantics|OpenReparsePoint,IntPtr.Zero);
        if(rootHandle.IsInvalid)
        {
            rootHandle.Dispose();
            return new(WindowsRelativeReservationStatus.ParentUntrusted);
        }
        using(rootHandle)
        {
            var rootReview=WindowsTelegramSameHandleReview.InspectOpenedHandle(
                rootHandle,true,pinnedRoot);
            if(rootReview.Status!=WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
                return new(WindowsRelativeReservationStatus.ParentUntrusted);

            var folder=lane==TelegramNotificationLane.FiveHourRecovery
                ?"five-hour":"weekly";
            using var laneHandle=OpenNativeChild(rootHandle,folder,
                ReadAttributes|ReadControl, NtOpen,
                NtDirectoryFile|NtOpenReparse|NtBackupIntent,
                IntPtr.Zero,out _);
            if(laneHandle is null || laneHandle.IsInvalid)
                return new(WindowsRelativeReservationStatus.ParentUntrusted);
            var laneReview=WindowsTelegramSameHandleReview.InspectOpenedHandle(
                laneHandle,true);
            if(laneReview.Status!=WindowsSameHandleStatus.HandleAclAndIdObservedOnly ||
                laneReview.FileId?.VolumeSerial!=pinnedRoot.VolumeSerial)
                return new(WindowsRelativeReservationStatus.ParentUntrusted);

            IntPtr descriptor=IntPtr.Zero;
            try
            {
                var security=PrivateFileSecurity();
                var raw=security.GetSecurityDescriptorBinaryForm();
                descriptor=Marshal.AllocHGlobal(raw.Length);
                Marshal.Copy(raw,0,descriptor,raw.Length);
                // FILE_CREATE + RootDirectory is the only mutating operation.
                using var marker=OpenNativeChild(laneHandle,
                    eventKey!+".reservation",
                    WriteData|ReadAttributes|ReadControl|Synchronize,NtCreate,
                    NtNonDirectoryFile|NtOpenReparse|NtWriteThrough|
                    NtSynchronousIoNonalert,descriptor,out var status);
                if(marker is null || marker.IsInvalid)
                    return new(StatusExists(status)
                        ?WindowsRelativeReservationStatus.ClaimAlreadyExists
                        :WindowsRelativeReservationStatus.CreationUncertain);

                var markerReview=WindowsTelegramSameHandleReview
                    .InspectOpenedHandle(marker,false);
                if(markerReview.Status!=WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
                    return new(WindowsRelativeReservationStatus.MarkerAclOrIdUntrusted);

                if(WindowsTelegramSameHandleReview.InspectOpenedHandle(
                        rootHandle,true,pinnedRoot).Status!=
                    WindowsSameHandleStatus.HandleAclAndIdObservedOnly ||
                   WindowsTelegramSameHandleReview.InspectOpenedHandle(
                        laneHandle,true,laneReview.FileId).Status!=
                    WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
                    return new(WindowsRelativeReservationStatus.ParentUntrusted);

                var payload=JsonSerializer.Serialize(new {
                    version=2,
                    lane=folder,
                    eventKey,
                    deviceSha256=Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(deviceId!)))
                        .ToLowerInvariant(),
                    observedAtUtcMs=nowUtc.ToUnixTimeMilliseconds(),
                    status="ROOTED_NATIVE_FIXTURE_ONLY_NO_SEND"
                });
                // Own no handle: FileStream disposing also disposes marker.
                // The handle-based final audit happens while stream is alive.
                using var stream=new FileStream(marker,FileAccess.Write,
                    4096,isAsync:false);
                stream.Write(Encoding.UTF8.GetBytes(payload));
                stream.Flush(flushToDisk:true);
                if(WindowsTelegramSameHandleReview.InspectOpenedHandle(
                    marker,false,markerReview.FileId).Status!=
                    WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
                    return new(WindowsRelativeReservationStatus.MarkerAclOrIdUntrusted);
                return new(WindowsRelativeReservationStatus.FixtureReservedOnly,
                    markerReview.FileId,true);
            }
            catch(Exception ex) when(ex is IOException or ArgumentException or
                UnauthorizedAccessException or System.Security.SecurityException or
                InvalidOperationException or NotSupportedException)
            {
                // Even a partially created marker is never deleted or retried.
                return new(WindowsRelativeReservationStatus.CreationUncertain);
            }
            finally
            {
                if(descriptor!=IntPtr.Zero) Marshal.FreeHGlobal(descriptor);
            }
        }
    }

    private static bool StatusExists(uint status)=>
        status==0xC0000035; // STATUS_OBJECT_NAME_COLLISION only.
        // ACCESS_DENIED and SHARING_VIOLATION are uncertain, not evidence of
        // an existing durable claim. Neither state permits a retry or send.

    private static FileSecurity PrivateFileSecurity()
    {
        var user=WindowsIdentity.GetCurrent().User ??
            throw new UnauthorizedAccessException("No user SID");
        var sec=new FileSecurity();
        sec.SetOwner(user);
        sec.SetAccessRuleProtection(true,false);
        sec.AddAccessRule(new FileSystemAccessRule(user,
            FileSystemRights.FullControl,AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),
            FileSystemRights.FullControl,AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),
            FileSystemRights.FullControl,AccessControlType.Allow));
        return sec;
    }

    private static SafeFileHandle? OpenNativeChild(
        SafeFileHandle parent,string child,uint access,uint disposition,
        uint options,IntPtr securityDescriptor,out uint status)
    {
        status=0;
        if(child.Length is <1 or >100 ||
            child is "." or ".." || child.IndexOfAny(['\\','/',':','\0'])>=0)
            return null;
        var name=Marshal.StringToHGlobalUni(child);
        var nameStructure=IntPtr.Zero;
        try
        {
            var length=checked((ushort)(child.Length*2));
            var nativeName=new UnicodeString {
                Length=length,MaximumLength=checked((ushort)(length+2)),
                Buffer=name
            };
            nameStructure=Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(nativeName,nameStructure,false);
            var attrs=new ObjectAttributes {
                Length=Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory=parent.DangerousGetHandle(),
                ObjectName=nameStructure,
                Attributes=ObjCaseInsensitive,
                SecurityDescriptor=securityDescriptor
            };
            int nt=NtCreateFile(out var created,access,ref attrs,
                out _,IntPtr.Zero,0,ShareReadWrite,disposition,
                options,IntPtr.Zero,0);
            status=unchecked((uint)nt);
            if(nt!=0 || created.IsInvalid)
            {
                created?.Dispose();
                return null;
            }
            return created;
        }
        finally
        {
            if(nameStructure!=IntPtr.Zero) Marshal.FreeHGlobal(nameStructure);
            Marshal.FreeHGlobal(name);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("ntdll.dll",EntryPoint="NtCreateFile",ExactSpelling=true)]
    private static extern int NtCreateFile(
        out SafeFileHandle file,uint desiredAccess,
        ref ObjectAttributes objectAttributes,out IoStatusBlock ioStatus,
        IntPtr allocationSize,uint fileAttributes,uint shareAccess,
        uint createDisposition,uint createOptions,IntPtr eaBuffer,uint eaLength);

    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,
        ExactSpelling=true,SetLastError=true)]
    private static extern SafeFileHandle CreateFileW(
        string file,uint desiredAccess,uint share,IntPtr security,
        uint disposition,uint attributes,IntPtr template);
}

public enum WindowsRelativeReservationStatus
{
    PreconditionsHeld,
    ParentUntrusted,
    ClaimAlreadyExists,
    CreationUncertain,
    MarkerAclOrIdUntrusted,
    FixtureReservedOnly
}

public sealed record WindowsRelativeReservationInspection(
    WindowsRelativeReservationStatus Status,
    WindowsHandleFileId? MarkerId=null,bool FixtureWasWritten=false)
{
    public bool ProductionReservationAuthorized=>false;
    public bool TelegramSendAuthorized=>false;
}
