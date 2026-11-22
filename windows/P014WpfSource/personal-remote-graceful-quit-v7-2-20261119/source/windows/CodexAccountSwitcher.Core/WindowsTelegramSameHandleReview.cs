using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Win32 handle-bound native FileId + owner/DACL read-only review.
/// This avoids using separate path lookups for the object ACL and FileId.
/// Only test fixtures or an existing fixed LocalAppData path can be queried.
/// It does NOT pin the full ancestor chain, give cross-host owner authority,
/// write any production data or authorize Telegram delivery.
/// </summary>
public static class WindowsTelegramSameHandleReview
{
    private const uint ReadAttributes=0x80;
    private const uint ReadControl=0x20000;
    private const uint ShareReadWriteDelete=7;
    private const uint OpenExisting=3;
    private const uint BackupSemantics=0x02000000;
    private const uint OpenReparsePoint=0x00200000;
    private const uint FileAttributeReparse=0x400;
    private const uint FileAttributeDirectory=0x10;
    private const int FileIdInfoClass=18;
    private const int FileAttributeTagInfoClass=9;
    private const uint OwnerSecurityInformation=0x1;
    private const uint DaclSecurityInformation=0x4;
    private const int FileObjectType=1; // SE_FILE_OBJECT
    private const uint MaxSecurityDescriptorBytes=65536;

    public static WindowsSameHandleInspection InspectDirectoryReadOnly(string? path)
    {
        if (!OperatingSystem.IsWindows())
            return new(WindowsSameHandleStatus.UnsupportedPlatform);
        // Legacy scope review is never authority, but first rejects arbitrary
        // input paths before a new native handle is opened.
        var scoped=WindowsTelegramHandleIdentityProbe.InspectReadOnly(path,true);
        if (scoped.Status!=WindowsHandleIdentityStatus.HandleIdentityObservedOnly ||
            scoped.ObservedId is null)
            return new(WindowsSameHandleStatus.ScopeOrDirectoryUnverified);
        try
        {
            using var handle=CreateFileW(path!,ReadAttributes|ReadControl,
                ShareReadWriteDelete,IntPtr.Zero,OpenExisting,
                BackupSemantics|OpenReparsePoint,IntPtr.Zero);
            if (handle.IsInvalid)
                return new(WindowsSameHandleStatus.NativeOpenFailed);
            return InspectOpenedHandle(handle,true,scoped.ObservedId);
        }
        catch (Exception ex) when (ex is ArgumentException or
            System.Security.SecurityException or UnauthorizedAccessException or
            IOException or NotSupportedException)
        {
            return new(WindowsSameHandleStatus.NativeInspectionUnavailable);
        }
    }

    /// <summary>
    /// A safe file handle owned by a caller. NEVER closes/disposes it.
    /// Internal use only in dedicated non-sending OS TEMP experiments.
    /// </summary>
    internal static WindowsSameHandleInspection InspectOpenedHandle(
        SafeFileHandle? handle,bool expectDirectory,
        WindowsHandleFileId? previouslyObserved=null)
    {
        if (!OperatingSystem.IsWindows())
            return new(WindowsSameHandleStatus.UnsupportedPlatform);
        if (handle is null || handle.IsInvalid || handle.IsClosed)
            return new(WindowsSameHandleStatus.NativeOpenFailed);

        var descriptor=IntPtr.Zero;
        try
        {
            if (!GetFileInformationByHandleEx(handle,FileAttributeTagInfoClass,
                out FileAttributeTag tag,(uint)Marshal.SizeOf<FileAttributeTag>()))
                return new(WindowsSameHandleStatus.NativeFileIdUnavailable);
            if ((tag.Attributes&FileAttributeReparse)!=0)
                return new(WindowsSameHandleStatus.ReparsePointNotTrusted);
            if (((tag.Attributes&FileAttributeDirectory)!=0)!=expectDirectory)
                return new(WindowsSameHandleStatus.ItemKindMismatch);
            if (!GetFileInformationByHandleEx(handle,FileIdInfoClass,
                out FileIdInfo before,(uint)Marshal.SizeOf<FileIdInfo>()))
                return new(WindowsSameHandleStatus.NativeFileIdUnavailable);
            var id=new WindowsHandleFileId(before.VolumeSerial,
                before.Low,before.High);
            if (id.VolumeSerial==0 || (id.Low==0 && id.High==0))
                return new(WindowsSameHandleStatus.NativeFileIdUnavailable);
            if (previouslyObserved is not null && previouslyObserved!=id)
                return new(WindowsSameHandleStatus.FileIdentityChanged,id);

            var rc=GetSecurityInfo(handle,FileObjectType,
                OwnerSecurityInformation|DaclSecurityInformation,
                out _,out _,out _,out _,out descriptor);
            if (rc!=0 || descriptor==IntPtr.Zero)
                return new(WindowsSameHandleStatus.NativeAclUnavailable,id);
            var bytes=GetSecurityDescriptorLength(descriptor);
            if (bytes is 0 or >MaxSecurityDescriptorBytes)
                return new(WindowsSameHandleStatus.NativeAclUnavailable,id);
            var blob=new byte[checked((int)bytes)];
            Marshal.Copy(descriptor,blob,0,blob.Length);
            var acl=new DirectorySecurity();
            acl.SetSecurityDescriptorBinaryForm(blob);
            var user=WindowsIdentity.GetCurrent().User;
            var classified=WindowsTelegramOutboxAclReview.InspectDescriptor(acl,user);
            if (classified.Status!=WindowsOutboxAclStatus.LocalAclCandidateOnly)
                return new(WindowsSameHandleStatus.AclNotPrivateEnough,id,
                    classified.Status);

            // The EXACT same OS handle is still open during the second
            // FileId query. The return is observational, never send-capable.
            if (!GetFileInformationByHandleEx(handle,FileIdInfoClass,
                out FileIdInfo after,(uint)Marshal.SizeOf<FileIdInfo>()))
                return new(WindowsSameHandleStatus.NativeFileIdUnavailable);
            if (before.VolumeSerial!=after.VolumeSerial ||
                before.Low!=after.Low || before.High!=after.High)
                return new(WindowsSameHandleStatus.FileIdentityChanged,id);
            return new(WindowsSameHandleStatus.HandleAclAndIdObservedOnly,id,
                classified.Status);
        }
        catch (Exception ex) when(ex is ArgumentException or
            System.Security.SecurityException or UnauthorizedAccessException or
            IOException or InvalidOperationException or NotSupportedException)
        {
            return new(WindowsSameHandleStatus.NativeInspectionUnavailable);
        }
        finally
        {
            if (descriptor!=IntPtr.Zero)
                _=LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTag
    {
        public uint Attributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerial;
        public ulong Low;
        public ulong High;
    }

    [DllImport("kernel32.dll",EntryPoint="CreateFileW",
        CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
    private static extern SafeFileHandle CreateFileW(
        string name,uint access,uint share,IntPtr security,uint mode,
        uint flags,IntPtr template);

    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",
        SetLastError=true,ExactSpelling=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle,int informationClass,
        out FileAttributeTag info,uint size);

    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",
        SetLastError=true,ExactSpelling=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle,int informationClass,
        out FileIdInfo info,uint size);

    [DllImport("advapi32.dll",EntryPoint="GetSecurityInfo",
        SetLastError=true,ExactSpelling=true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,int objectType,uint securityInformation,
        out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll",EntryPoint="GetSecurityDescriptorLength",
        ExactSpelling=true)]
    private static extern uint GetSecurityDescriptorLength(
        IntPtr securityDescriptor);

    [DllImport("kernel32.dll",EntryPoint="LocalFree",ExactSpelling=true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}

public enum WindowsSameHandleStatus
{
    UnsupportedPlatform,
    ScopeOrDirectoryUnverified,
    NativeOpenFailed,
    NativeFileIdUnavailable,
    ReparsePointNotTrusted,
    ItemKindMismatch,
    FileIdentityChanged,
    NativeAclUnavailable,
    AclNotPrivateEnough,
    NativeInspectionUnavailable,
    HandleAclAndIdObservedOnly
}

public sealed record WindowsSameHandleInspection(
    WindowsSameHandleStatus Status,
    WindowsHandleFileId? FileId=null,
    WindowsOutboxAclStatus? AclStatus=null)
{
    public bool ProductionOutboxReady=>false;
    public bool ProductionReservationAuthorized=>false;
    public bool TelegramSendAuthorized=>false;
}
