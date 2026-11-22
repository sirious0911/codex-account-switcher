using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Strict READ-ONLY V2 fixture recovery. A private native ROOTED reservation
/// is examined relative to its pinned directory handles, never path-opened
/// for payload access and never repaired. Always denies real send/retry.
/// </summary>
public static class WindowsTelegramRootedV2RecoveryReview
{
    private const string FixturePrefix="codex-switcher-telegram-handle-test-";
    private const uint FileReadAttributes=0x80;
    private const uint ReadControl=0x20000;
    private const uint FileReadData=0x1;
    private const uint Synchronize=0x100000;
    private const uint ShareRead=0x1; // no writer/delete sharing on examined claim
    private const uint ShareReadWrite=0x3;
    private const uint OpenExisting=3;
    private const uint BackupSemantics=0x02000000;
    private const uint OpenReparsePoint=0x00200000;
    private const uint ObjCaseInsensitive=0x40;
    private const uint NtOpen=1;
    private const uint NtDirectoryFile=1;
    private const uint NtNonDirectoryFile=0x40;
    private const uint NtSynchronousNonalert=0x20;
    private const uint NtOpenReparse=0x00200000;
    private const uint NtBackupIntent=0x00004000;
    private const int MaxMarkerBytes=4096;
    private const int ExpectedPropertyCount=6;

    public static WindowsV2RecoveryInspection InspectForOfflineTest(
        string? scratchRoot,TelegramNotificationLane lane,
        string? eventKey,string? expectedDeviceId)
    {
        if(!OperatingSystem.IsWindows())
            return Hold(WindowsV2RecoveryStatus.UnsupportedPlatform);
        if(!ValidHex(eventKey) ||
            lane is not (TelegramNotificationLane.FiveHourRecovery or
                TelegramNotificationLane.WeeklyRecovery) ||
            !ValidDevice(expectedDeviceId))
            return Hold(WindowsV2RecoveryStatus.InvalidBinding);
        if(!ValidFixtureRoot(scratchRoot,out var root))
            return Hold(WindowsV2RecoveryStatus.InvalidFixtureScope);

        var chain=WindowsTelegramRootedChainReview
            .InspectFixtureReadOnly(root);
        if(chain.Status!=WindowsRootedChainStatus.HandleRelativeCandidateOnly ||
            chain.Snapshot?.Identities.LastOrDefault() is not { } pinnedRoot)
            return Hold(WindowsV2RecoveryStatus.ParentUntrusted);

        using var rootHandle=CreateFileW(root,FileReadAttributes|ReadControl,
            ShareReadWrite,IntPtr.Zero,OpenExisting,
            BackupSemantics|OpenReparsePoint,IntPtr.Zero);
        if(rootHandle.IsInvalid)
            return Hold(WindowsV2RecoveryStatus.ParentUntrusted);
        if(WindowsTelegramSameHandleReview.InspectOpenedHandle(
                rootHandle,true,pinnedRoot).Status!=
            WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
            return Hold(WindowsV2RecoveryStatus.ParentUntrusted);

        var name=lane==TelegramNotificationLane.FiveHourRecovery
            ?"five-hour":"weekly";
        using var laneHandle=OpenRelative(rootHandle,name,
            FileReadAttributes|ReadControl,
            NtDirectoryFile|NtOpenReparse|NtBackupIntent,
            ShareReadWrite,out _);
        if(laneHandle is null || laneHandle.IsInvalid)
            return Hold(WindowsV2RecoveryStatus.ParentUntrusted);
        var laneInspection=WindowsTelegramSameHandleReview.InspectOpenedHandle(
            laneHandle,true);
        if(laneInspection.Status!=WindowsSameHandleStatus.HandleAclAndIdObservedOnly ||
            laneInspection.FileId?.VolumeSerial!=pinnedRoot.VolumeSerial)
            return Hold(WindowsV2RecoveryStatus.ParentUntrusted);

        var leaf=eventKey!+".reservation";
        using var fileHandle=OpenRelative(laneHandle,leaf,
            FileReadData|FileReadAttributes|ReadControl|Synchronize,
            NtNonDirectoryFile|NtOpenReparse|NtSynchronousNonalert,
            ShareRead,out var status);
        if(fileHandle is null || fileHandle.IsInvalid)
            return Hold(status is 0xC0000034 or 0xC000003A
                ?WindowsV2RecoveryStatus.NoClaimObserved
                :WindowsV2RecoveryStatus.FileOpenUntrusted);

        var before=WindowsTelegramSameHandleReview.InspectOpenedHandle(
            fileHandle,false);
        if(before.Status!=WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
            return Hold(WindowsV2RecoveryStatus.FileAclOrIdUntrusted);

        try
        {
            using var file=new FileStream(fileHandle,FileAccess.Read,4096,
                isAsync:false);
            if(file.Length==0)
                return Hold(WindowsV2RecoveryStatus.EmptyOrInterrupted);
            if(file.Length>MaxMarkerBytes)
                return Hold(WindowsV2RecoveryStatus.MalformedV2Marker);
            var bytes=new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            if(file.Position!=bytes.Length)
                return Hold(WindowsV2RecoveryStatus.MalformedV2Marker);
            if(WindowsTelegramSameHandleReview.InspectOpenedHandle(
                file.SafeFileHandle,false,before.FileId).Status!=
                    WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
                return Hold(WindowsV2RecoveryStatus.FileAclOrIdUntrusted);
            if(WindowsTelegramSameHandleReview.InspectOpenedHandle(
                laneHandle,true,laneInspection.FileId).Status!=
                    WindowsSameHandleStatus.HandleAclAndIdObservedOnly ||
               WindowsTelegramSameHandleReview.InspectOpenedHandle(
                rootHandle,true,pinnedRoot).Status!=
                    WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
                return Hold(WindowsV2RecoveryStatus.ParentUntrusted);
            if(WindowsTelegramRootedChainReview.InspectFixtureReadOnly(
                root,chain.Snapshot).Status!=
                WindowsRootedChainStatus.HandleRelativeCandidateOnly)
                return Hold(WindowsV2RecoveryStatus.ParentUntrusted);

            return ParseStrictV2(bytes,name,eventKey!,expectedDeviceId!);
        }
        catch(Exception ex) when(ex is IOException or ArgumentException or
            InvalidOperationException or NotSupportedException or
            UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Hold(WindowsV2RecoveryStatus.ReadUncertain);
        }
    }

    private static WindowsV2RecoveryInspection ParseStrictV2(
        byte[] bytes,string expectedLane,string expectedKey,string expectedDevice)
    {
        try
        {
            using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions {
                AllowTrailingCommas=false,
                CommentHandling=JsonCommentHandling.Disallow,
                MaxDepth=8
            });
            var obj=doc.RootElement;
            if(obj.ValueKind!=JsonValueKind.Object)
                return Hold(WindowsV2RecoveryStatus.MalformedV2Marker);
            var fields=new HashSet<string>(StringComparer.Ordinal) {
                "version","lane","eventKey","deviceSha256",
                "observedAtUtcMs","status"
            };
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in obj.EnumerateObject())
                if(!fields.Contains(property.Name) || !seen.Add(property.Name))
                    return Hold(WindowsV2RecoveryStatus.MalformedV2Marker);
            if(seen.Count!=ExpectedPropertyCount ||
                obj.GetProperty("version").ValueKind!=JsonValueKind.Number ||
                !obj.GetProperty("version").TryGetInt32(out var version))
                return Hold(WindowsV2RecoveryStatus.MalformedV2Marker);
            if(version!=2)
                return Hold(WindowsV2RecoveryStatus.WrongReservationVersion);
            if(obj.GetProperty("lane").ValueKind!=JsonValueKind.String ||
               obj.GetProperty("eventKey").ValueKind!=JsonValueKind.String ||
               obj.GetProperty("deviceSha256").ValueKind!=JsonValueKind.String ||
               obj.GetProperty("status").ValueKind!=JsonValueKind.String ||
               obj.GetProperty("observedAtUtcMs").ValueKind!=JsonValueKind.Number)
                return Hold(WindowsV2RecoveryStatus.MalformedV2Marker);
            if(!string.Equals(obj.GetProperty("lane").GetString(),expectedLane,
                    StringComparison.Ordinal) ||
               !string.Equals(obj.GetProperty("eventKey").GetString(),expectedKey,
                    StringComparison.Ordinal))
                return Hold(WindowsV2RecoveryStatus.EventOrLaneMismatch);
            var expectedHash=Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(expectedDevice))).ToLowerInvariant();
            if(!string.Equals(obj.GetProperty("deviceSha256").GetString(),
                    expectedHash,StringComparison.Ordinal))
                return Hold(WindowsV2RecoveryStatus.DeviceMismatch);
            if(obj.GetProperty("status").GetString()!=
                    "ROOTED_NATIVE_FIXTURE_ONLY_NO_SEND" ||
               !obj.GetProperty("observedAtUtcMs").TryGetInt64(out var utcMs) ||
               utcMs<=0 || utcMs>253402300799999)
                return Hold(WindowsV2RecoveryStatus.MalformedV2Marker);
            return new(WindowsV2RecoveryStatus.ValidV2FixtureNoSend);
        }
        catch(Exception ex) when(ex is JsonException or ArgumentException or
            InvalidOperationException)
        {
            return Hold(WindowsV2RecoveryStatus.MalformedV2Marker);
        }
    }

    private static bool ValidHex(string? s)=>
        s is {Length:64} && s.All(c=>c is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static bool ValidDevice(string? id)=>
        id is {Length:>=1 and <=48} &&
        id.All(c=>c is >= '0' and <= '9'
            or >= 'a' and <= 'z' or >= 'A' and <= 'Z'
            or '-' or '_');

    private static bool ValidFixtureRoot(string? raw,out string result)
    {
        result="";
        if(string.IsNullOrWhiteSpace(raw) ||
            !Path.IsPathFullyQualified(raw) ||
            raw.IndexOfAny(['\0','*','?'])>=0 ||
            raw.Replace('/','\\').Split('\\').Any(p=>p is "." or ".."))
            return false;
        try
        {
            var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            var temp=Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));
            var leaf=Path.GetFileName(full);
            if(!string.Equals(Path.GetDirectoryName(full),temp,
                    StringComparison.OrdinalIgnoreCase) ||
                !leaf.StartsWith(FixturePrefix,StringComparison.Ordinal) ||
                leaf.Length!=FixturePrefix.Length+32 ||
                !leaf[FixturePrefix.Length..].All(c=>c is >= '0' and <= '9'
                    or >= 'a' and <= 'f'))
                return false;
            result=full;return true;
        }
        catch(Exception ex) when(ex is ArgumentException or IOException or
            NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static SafeFileHandle? OpenRelative(SafeFileHandle parent,
        string child,uint access,uint options,uint share,out uint status)
    {
        status=0;
        if(child.Length is <1 or >100 ||
            child is "." or ".." ||
            child.IndexOfAny(['\0','\\','/',':'])>=0)
            return null;
        var buffer=Marshal.StringToHGlobalUni(child);
        var ptr=IntPtr.Zero;
        try
        {
            var byteLength=checked((ushort)(child.Length*2));
            var unicode=new UnicodeString {
                Length=byteLength,
                MaximumLength=checked((ushort)(byteLength+2)),
                Buffer=buffer
            };
            ptr=Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode,ptr,false);
            var attrs=new ObjectAttributes {
                Length=Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory=parent.DangerousGetHandle(),
                ObjectName=ptr,
                Attributes=ObjCaseInsensitive
            };
            var nt=NtCreateFile(out var file,access,ref attrs,out _,
                IntPtr.Zero,0,share,NtOpen,options,IntPtr.Zero,0);
            status=unchecked((uint)nt);
            if(nt!=0 || file.IsInvalid)
            {
                file?.Dispose();
                return null;
            }
            return file;
        }
        finally
        {
            if(ptr!=IntPtr.Zero) Marshal.FreeHGlobal(ptr);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static WindowsV2RecoveryInspection Hold(
        WindowsV2RecoveryStatus status)=>new(status);

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
        out SafeFileHandle handle,uint access,
        ref ObjectAttributes attrs,out IoStatusBlock ios,
        IntPtr allocation,uint fileAttributes,uint share,
        uint disposition,uint options,IntPtr eaBuffer,uint eaLength);
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,
        ExactSpelling=true,SetLastError=true)]
    private static extern SafeFileHandle CreateFileW(
        string path,uint access,uint share,IntPtr attrs,
        uint disposition,uint flags,IntPtr template);
}

public enum WindowsV2RecoveryStatus
{
    UnsupportedPlatform,
    InvalidBinding,
    InvalidFixtureScope,
    ParentUntrusted,
    NoClaimObserved,
    FileOpenUntrusted,
    FileAclOrIdUntrusted,
    EmptyOrInterrupted,
    MalformedV2Marker,
    WrongReservationVersion,
    EventOrLaneMismatch,
    DeviceMismatch,
    ReadUncertain,
    ValidV2FixtureNoSend
}

public sealed record WindowsV2RecoveryInspection(
    WindowsV2RecoveryStatus Status)
{
    public bool AutomaticReplayAllowed=>false;
    public bool ProductionReservationAuthorized=>false;
    public bool TelegramSendAuthorized=>false;
}
