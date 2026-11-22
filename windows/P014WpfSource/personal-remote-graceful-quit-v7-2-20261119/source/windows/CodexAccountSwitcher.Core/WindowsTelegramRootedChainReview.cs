using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Native Windows path-relative READONLY inspection. Each segment is opened
/// relative to the previously opened directory handle (NtCreateFile.RootDirectory).
/// No directory creation, automatic retry, Telegram transport, or live reservation.
/// Holding every parent handle without FILE_SHARE_DELETE limits rename during review,
/// but does NOT prove subsequent separate production writes are race-free.
/// </summary>
public static class WindowsTelegramRootedChainReview
{
    private const uint FileReadAttributes=0x80;
    private const uint ReadControl=0x20000;
    private const uint ShareReadWrite=0x3;
    private const uint OpenExisting=3;
    private const uint BackupSemantics=0x02000000;
    private const uint OpenReparsePoint=0x00200000;
    private const uint ReparseAttribute=0x400;
    private const uint DirectoryAttribute=0x10;
    private const uint ObjCaseInsensitive=0x40;
    private const uint NtFileOpen=1;
    private const uint NtFileDirectoryFile=1;
    private const uint NtFileOpenReparsePoint=0x00200000;
    private const uint NtFileOpenForBackupIntent=0x00004000;
    private const string FixturePrefix="codex-switcher-telegram-handle-test-";

    public static WindowsRootedChainInspection InspectProductionReadOnly(
        WindowsPathChainSnapshot? previous=null)
    {
        if (!OperatingSystem.IsWindows())
            return Hold(WindowsRootedChainStatus.UnsupportedPlatform);
        var local=Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local) ||
            !Path.IsPathFullyQualified(local))
            return Hold(WindowsRootedChainStatus.InvalidScope);
        return InspectExistingPath(Path.Combine(local,"Sirious0911",
            "CodexAccountSwitcher","TelegramOutbox"),privateSegments:3,previous);
    }

    public static WindowsRootedChainInspection InspectFixtureReadOnly(
        string? fixtureRoot,WindowsPathChainSnapshot? previous=null)
    {
        if (!OperatingSystem.IsWindows())
            return Hold(WindowsRootedChainStatus.UnsupportedPlatform);
        if (!ValidFixture(fixtureRoot,out var root))
            return Hold(WindowsRootedChainStatus.InvalidScope);
        return InspectExistingPath(root,privateSegments:1,previous);
    }

    public static WindowsRootedChainInspection InspectFixtureAppOutboxReadOnly(
        string? fixtureRoot,WindowsPathChainSnapshot? previous=null)
    {
        if (!OperatingSystem.IsWindows())
            return Hold(WindowsRootedChainStatus.UnsupportedPlatform);
        if (!ValidFixture(fixtureRoot,out var root))
            return Hold(WindowsRootedChainStatus.InvalidScope);
        return InspectExistingPath(Path.Combine(root,"Sirious0911",
            "CodexAccountSwitcher","TelegramOutbox"),privateSegments:4,previous);
    }

    private static WindowsRootedChainInspection InspectExistingPath(
        string target,int privateSegments,WindowsPathChainSnapshot? previous)
    {
        var held=new List<SafeFileHandle>();
        var ids=new List<WindowsHandleFileId>();
        try
        {
            var canonical=Path.GetFullPath(target);
            var driveRoot=Path.GetPathRoot(canonical);
            if (driveRoot is null || driveRoot.Length!=3 ||
                driveRoot[1]!=':' || driveRoot[2]!='\\' ||
                !char.IsAsciiLetter(driveRoot[0]))
                return Hold(WindowsRootedChainStatus.InvalidOrNonlocalVolume);
            var physicalDrive=new DriveInfo(driveRoot);
            if (physicalDrive.DriveType!=DriveType.Fixed ||
                physicalDrive.DriveFormat is not ("NTFS" or "ReFS"))
                return Hold(WindowsRootedChainStatus.InvalidOrNonlocalVolume);
            var parts=canonical[driveRoot.Length..]
                .Split('\\',StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is 0 or >30 ||
                parts.Length<privateSegments ||
                parts.Any(x=>x is "." or ".." || x.Length>255 ||
                    x.IndexOfAny(['\0','/',':'])>=0))
                return Hold(WindowsRootedChainStatus.InvalidScope);

            var drive=CreateFileW(driveRoot,FileReadAttributes,
                ShareReadWrite,IntPtr.Zero,OpenExisting,
                BackupSemantics|OpenReparsePoint,IntPtr.Zero);
            if (drive.IsInvalid)
            {
                drive.Dispose();
                return Hold(WindowsRootedChainStatus.NativeRootOpenFailed);
            }
            held.Add(drive);
            if (!InspectOne(held[0],false,out var first,out var problem))
                return Hold(problem);
            ids.Add(first);
            ulong volume=first.VolumeSerial;
            var current=drive;
            for (var i=0;i<parts.Length;i++)
            {
                var privateChild=i>=parts.Length-privateSegments;
                var desired=FileReadAttributes |
                    (privateChild?ReadControl:0u);
                var opened=OpenRelativeDirectory(current,parts[i],desired,
                    out var ntstatus);
                if (opened is null)
                    return Hold(IsMissingNtStatus(ntstatus)
                        ?WindowsRootedChainStatus.ChildMissing
                        :WindowsRootedChainStatus.NativeRelativeOpenFailed);
                held.Add(opened);
                if (!InspectOne(opened,privateChild,out var id,out problem))
                    return Hold(problem);
                if (volume!=id.VolumeSerial)
                    return Hold(WindowsRootedChainStatus.VolumeChanged);
                ids.Add(id);
                current=opened;
            }

            // All parents remain open and forbid FILE_SHARE_DELETE while
            // checking. A previous observation is never a signing authority.
            for(var i=0;i<held.Count;i++)
                if (!ReadId(held[i],out var again) || again!=ids[i])
                    return Hold(WindowsRootedChainStatus.HandleChangedInReview);
            if (previous is not null &&
                (previous.Identities.Count!=ids.Count ||
                 !previous.Identities.SequenceEqual(ids)))
                return Hold(WindowsRootedChainStatus.PreviousChainChanged);

            return new(WindowsRootedChainStatus.HandleRelativeCandidateOnly,
                new WindowsPathChainSnapshot(ids.ToArray()));
        }
        catch(Exception ex) when(ex is ArgumentException or IOException or
            UnauthorizedAccessException or System.Security.SecurityException or
            InvalidOperationException or NotSupportedException)
        {
            return Hold(WindowsRootedChainStatus.NativeInspectionUnavailable);
        }
        finally
        {
            for(var i=held.Count-1;i>=0;i--) held[i].Dispose();
        }
    }

    private static bool InspectOne(SafeFileHandle handle,bool privateChild,
        out WindowsHandleFileId id,out WindowsRootedChainStatus failure)
    {
        id=new(0,0,0);
        failure=WindowsRootedChainStatus.IdentityUnavailable;
        if (!GetFileInformationByHandleEx(handle,9,out FileTagInfo tag,
            (uint)Marshal.SizeOf<FileTagInfo>()))
            return false;
        if((tag.Attributes&ReparseAttribute)!=0)
        {
            failure=WindowsRootedChainStatus.ReparseNode;
            return false;
        }
        if((tag.Attributes&DirectoryAttribute)==0)
        {
            failure=WindowsRootedChainStatus.NotDirectory;
            return false;
        }
        if(!ReadId(handle,out id)) return false;
        if(privateChild && WindowsTelegramSameHandleReview.InspectOpenedHandle(
            handle,true,id).Status!=
            WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
        {
            failure=WindowsRootedChainStatus.PrivateAclUntrusted;
            return false;
        }
        return true;
    }

    private static bool ReadId(SafeFileHandle handle,out WindowsHandleFileId id)
    {
        id=new(0,0,0);
        if (!GetFileInformationByHandleEx(handle,18,out FileIdInfo data,
            (uint)Marshal.SizeOf<FileIdInfo>()) ||
            data.VolumeSerial==0 || (data.Low==0 && data.High==0))
            return false;
        id=new(data.VolumeSerial,data.Low,data.High);
        return true;
    }

    private static SafeFileHandle? OpenRelativeDirectory(
        SafeFileHandle parent,string child,uint desired,out uint ntstatus)
    {
        ntstatus=0;
        // An UNICODE_STRING, not a path. RootDirectory anchors resolution.
        var utf16=Marshal.StringToHGlobalUni(child);
        var descriptor=IntPtr.Zero;
        try
        {
            var bytes=checked((ushort)(child.Length*2));
            var unicode=new NativeUnicodeString {
                Length=bytes,
                MaximumLength=checked((ushort)(bytes+2)),
                Buffer=utf16
            };
            descriptor=Marshal.AllocHGlobal(Marshal.SizeOf<NativeUnicodeString>());
            Marshal.StructureToPtr(unicode,descriptor,false);
            var attrs=new NativeObjectAttributes {
                Length=Marshal.SizeOf<NativeObjectAttributes>(),
                RootDirectory=parent.DangerousGetHandle(),
                ObjectName=descriptor,
                Attributes=ObjCaseInsensitive
            };
            // NtCreateFile's create disposition FILE_OPEN and no create
            // access deliberately prevent provisioning or mutation.
            var result=NtCreateFile(out var opened,desired,ref attrs,
                out _,IntPtr.Zero,0,ShareReadWrite,NtFileOpen,
                NtFileDirectoryFile|NtFileOpenReparsePoint|
                NtFileOpenForBackupIntent,IntPtr.Zero,0);
            ntstatus=unchecked((uint)result);
            // NTSTATUS 0 is STATUS_SUCCESS; positive statuses (such as
            // STATUS_PENDING) are not completed observations.
            if(result!=0 || opened.IsInvalid)
            {
                opened?.Dispose();
                return null;
            }
            return opened;
        }
        finally
        {
            if(descriptor!=IntPtr.Zero) Marshal.FreeHGlobal(descriptor);
            Marshal.FreeHGlobal(utf16);
        }
    }

    private static bool IsMissingNtStatus(uint status)=>
        status is 0xC0000034 or 0xC000003A;

    private static bool ValidFixture(string? raw,out string path)
    {
        path="";
        if(string.IsNullOrWhiteSpace(raw) ||
            !Path.IsPathFullyQualified(raw) ||
            raw.IndexOfAny(['\0','*','?'])>=0 ||
            raw.Replace('/','\\').Split('\\').Any(x=>x is ".." or "."))
            return false;
        try
        {
            var candidate=Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            var temp=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var leaf=Path.GetFileName(candidate);
            if(!string.Equals(Path.GetDirectoryName(candidate),temp,
                StringComparison.OrdinalIgnoreCase) ||
                !leaf.StartsWith(FixturePrefix,StringComparison.Ordinal))
                return false;
            var suffix=leaf[FixturePrefix.Length..];
            if(suffix.Length!=32 ||
                !suffix.All(c=>c is >= 'a' and <= 'f' or >= '0' and <= '9'))
                return false;
            path=candidate;
            return true;
        }
        catch(Exception ex) when(ex is ArgumentException or IOException or
            System.Security.SecurityException or NotSupportedException)
        {
            return false;
        }
    }

    private static WindowsRootedChainInspection Hold(
        WindowsRootedChainStatus reason)=>new(reason);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTagInfo
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

    [DllImport("ntdll.dll",EntryPoint="NtCreateFile",ExactSpelling=true)]
    private static extern int NtCreateFile(
        out SafeFileHandle handle,uint desiredAccess,
        ref NativeObjectAttributes attributes,
        out NativeIoStatusBlock ioStatus,IntPtr allocationSize,
        uint fileAttributes,uint shareAccess,uint createDisposition,
        uint createOptions,IntPtr eaBuffer,uint eaLength);

    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,
        ExactSpelling=true,SetLastError=true)]
    private static extern SafeFileHandle CreateFileW(
        string path,uint access,uint share,IntPtr security,
        uint disposition,uint flags,IntPtr template);

    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",
        ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle,int infoClass,out FileTagInfo info,uint bytes);

    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",
        ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle,int infoClass,out FileIdInfo info,uint bytes);
}

public enum WindowsRootedChainStatus
{
    UnsupportedPlatform,
    InvalidScope,
    InvalidOrNonlocalVolume,
    NativeRootOpenFailed,
    ChildMissing,
    NativeRelativeOpenFailed,
    ReparseNode,
    NotDirectory,
    IdentityUnavailable,
    VolumeChanged,
    PrivateAclUntrusted,
    HandleChangedInReview,
    PreviousChainChanged,
    NativeInspectionUnavailable,
    HandleRelativeCandidateOnly
}

public sealed record WindowsRootedChainInspection(
    WindowsRootedChainStatus Status,
    WindowsPathChainSnapshot? Snapshot=null)
{
    public bool ProductionStorageVerified=>false;
    public bool ProductionReservationAuthorized=>false;
    public bool TelegramSendAuthorized=>false;
}
