using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Windows OS handle identity probe, READ ONLY and deliberately non-authorizing.
/// Accepts only the fixed prospective LocalAppData TelegramOutbox directory or
/// a dedicated direct Temp child (and at most two fixture descendants).
/// Native handle is OPEN_EXISTING + FILE_READ_ATTRIBUTES; no create/write.
/// No result may authorize a production reservation or a Telegram message.
/// </summary>
public static class WindowsTelegramHandleIdentityProbe
{
    private const string FixturePrefix="codex-switcher-telegram-handle-test-";
    private const uint FileReadAttributes=0x80;
    private const uint ShareReadWriteDelete=0x7;
    private const uint OpenExisting=3;
    private const uint BackupSemantics=0x02000000;
    private const uint OpenReparsePoint=0x00200000;
    private const int FileAttributeTagInfo=9;
    private const int FileIdInfoClass=18;
    private const uint ReparseAttribute=0x400;

    public static WindowsHandleIdentityInspection InspectReadOnly(
        string? path, bool expectedDirectory, WindowsHandleFileId? previous=null)
    {
        if (!OperatingSystem.IsWindows())
            return Hold(WindowsHandleIdentityStatus.UnsupportedPlatform);
        if (!IsAllowedTarget(path,out var target))
            return Hold(WindowsHandleIdentityStatus.TargetOutsideReadOnlyScope);

        try
        {
            // A parent junction/symlink would let a permitted lexical path
            // escape to an unrelated filesystem object. Reject known reparse
            // ancestors before the handle query. This pre-open check is NOT
            // atomic and therefore never elevates the result to production.
            for (var cursor=target; cursor is not null;
                cursor=Path.GetDirectoryName(cursor))
            {
                if (Path.Exists(cursor) &&
                    (File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)
                    return Hold(WindowsHandleIdentityStatus.ReparsePointNotTrusted);
            }
            // This path check is for early rejection only; the native opened
            // handle is the identity source. Future LIVE code would need to
            // bind ACL and reservation writes to that very same handle.
            if (!Path.Exists(target))
                return Hold(WindowsHandleIdentityStatus.TargetNotProvisioned);
            using var handle=CreateFileW(target,FileReadAttributes,
                ShareReadWriteDelete,IntPtr.Zero,OpenExisting,
                BackupSemantics|OpenReparsePoint,IntPtr.Zero);
            if (handle.IsInvalid)
                return Hold(WindowsHandleIdentityStatus.NativeOpenFailed);
            if (!GetFileInformationByHandleEx(handle,FileAttributeTagInfo,
                out FileAttributeTag attributes,
                (uint)Marshal.SizeOf<FileAttributeTag>()))
                return Hold(WindowsHandleIdentityStatus.NativeIdentityUnavailable);
            if ((attributes.FileAttributes&ReparseAttribute)!=0)
                return Hold(WindowsHandleIdentityStatus.ReparsePointNotTrusted);
            var isDirectory=(attributes.FileAttributes&0x10)!=0;
            if (isDirectory!=expectedDirectory)
                return Hold(WindowsHandleIdentityStatus.ItemKindMismatch);
            if (!GetFileInformationByHandleEx(handle,FileIdInfoClass,
                out FileIdInfo native,(uint)Marshal.SizeOf<FileIdInfo>()))
                return Hold(WindowsHandleIdentityStatus.NativeIdentityUnavailable);

            var current=new WindowsHandleFileId(native.VolumeSerial,
                native.Low,native.High);
            if (current.VolumeSerial==0 ||
                (current.Low==0 && current.High==0))
                return Hold(WindowsHandleIdentityStatus.NativeIdentityUnavailable);
            if (previous is not null && previous!=current)
                return new(WindowsHandleIdentityStatus.IdentityChanged,current);
            return new(WindowsHandleIdentityStatus.HandleIdentityObservedOnly,
                current);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or
            UnauthorizedAccessException or System.Security.SecurityException or
            NotSupportedException or Win32Exception)
        {
            return Hold(WindowsHandleIdentityStatus.NativeInspectionFailed);
        }
    }

    private static WindowsHandleIdentityInspection Hold(
        WindowsHandleIdentityStatus status)=>new(status,null);

    private static bool IsAllowedTarget(string? input,out string path)
    {
        path="";
        if (string.IsNullOrWhiteSpace(input) ||
            input.IndexOfAny(['\0','*','?'])>=0 ||
            !Path.IsPathFullyQualified(input))
            return false;
        try
        {
            var rawSegments=input.Replace('/','\\').Split('\\');
            if (rawSegments.Any(segment=>segment is "." or ".."))
                return false;
            var candidate=Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(input));
            var local=Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
            {
                var production=Path.Combine(local,"Sirious0911",
                    "CodexAccountSwitcher","TelegramOutbox");
                if (string.Equals(candidate,Path.GetFullPath(production),
                    StringComparison.OrdinalIgnoreCase))
                {
                    path=candidate;
                    return true;
                }
            }
            var temp=Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));
            var relative=Path.GetRelativePath(temp,candidate);
            if (Path.IsPathRooted(relative))
                return false;
            var parts=relative.Split(Path.DirectorySeparatorChar);
            if (parts.Length is <1 or >3 ||
                !parts[0].StartsWith(FixturePrefix,StringComparison.Ordinal))
                return false;
            var suffix=parts[0][FixturePrefix.Length..];
            if (suffix.Length!=32 ||
                !suffix.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
            if (parts.Skip(1).Any(x=>x.Length is <1 or >80 ||
                !x.All(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
                    or >= '0' and <= '9' or '_' or '-' or '.')))
                return false;
            path=candidate;
            return true;
        }
        catch (Exception ex) when(ex is ArgumentException or IOException or
            NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTag
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerial;
        public ulong Low;
        public ulong High;
    }

    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,
        SetLastError=true,ExactSpelling=true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,uint desiredAccess,uint shareMode,
        IntPtr securityAttributes,uint creationDisposition,
        uint flagsAndAttributes,IntPtr template);

    [DllImport("kernel32.dll",SetLastError=true,ExactSpelling=true,
        EntryPoint="GetFileInformationByHandleEx")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle,int infoClass,out FileAttributeTag info,uint size);

    [DllImport("kernel32.dll",SetLastError=true,ExactSpelling=true,
        EntryPoint="GetFileInformationByHandleEx")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle,int infoClass,out FileIdInfo info,uint size);
}

public sealed record WindowsHandleFileId(ulong VolumeSerial,ulong Low,ulong High);

public enum WindowsHandleIdentityStatus
{
    UnsupportedPlatform,
    TargetOutsideReadOnlyScope,
    TargetNotProvisioned,
    NativeOpenFailed,
    NativeIdentityUnavailable,
    ReparsePointNotTrusted,
    ItemKindMismatch,
    IdentityChanged,
    HandleIdentityObservedOnly,
    NativeInspectionFailed
}

public sealed record WindowsHandleIdentityInspection(
    WindowsHandleIdentityStatus Status,
    WindowsHandleFileId? ObservedId)
{
    public bool ActualProductionReservationAuthorized=>false;
    public bool TelegramSendAuthorized=>false;
}
