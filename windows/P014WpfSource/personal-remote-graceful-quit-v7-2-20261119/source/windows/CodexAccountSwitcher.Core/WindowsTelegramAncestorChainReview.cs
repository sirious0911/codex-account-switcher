using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Inspection only: holds READ-ONLY OS handles for every path component,
/// checks FILE_ID_INFO and reparse flags, and reviews private descendants
/// using the same handle. No provisioning, mutation, network, or delivery.
/// A path-chain observation is NOT production storage authorization.
/// </summary>
public static class WindowsTelegramAncestorChainReview
{
    private const uint ReadAttributes = 0x80;
    private const uint ReadControl = 0x20000;
    private const uint ShareReadWrite = 0x3; // no DELETE share while inspected
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint ReparseAttribute = 0x400;
    private const uint DirectoryAttribute = 0x10;
    private const string FixturePrefix = "codex-switcher-telegram-handle-test-";

    public static WindowsChainInspection InspectProductionReadOnly(
        WindowsPathChainSnapshot? previous = null)
    {
        if (!OperatingSystem.IsWindows())
            return Hold(WindowsChainStatus.UnsupportedPlatform);
        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local) ||
            !Path.IsPathFullyQualified(local))
            return Hold(WindowsChainStatus.InvalidScope);
        var full = Path.Combine(local, "Sirious0911",
            "CodexAccountSwitcher", "TelegramOutbox");
        return InspectChain(full, privateNodes:3, previous);
    }

    public static WindowsChainInspection InspectIsolatedFixtureReadOnly(
        string? fixtureRoot, WindowsPathChainSnapshot? previous = null)
    {
        if (!OperatingSystem.IsWindows())
            return Hold(WindowsChainStatus.UnsupportedPlatform);
        if (!IsFixtureRoot(fixtureRoot, out var full))
            return Hold(WindowsChainStatus.InvalidScope);
        return InspectChain(full, privateNodes:1, previous);
    }

    /// <summary>
    /// Preview the production-style three-level app-owned directory layout
    /// under a private, dedicated OS-TEMP fixture root. Read-only.
    /// </summary>
    public static WindowsChainInspection InspectIsolatedAppOutboxReadOnly(
        string? fixtureRoot, WindowsPathChainSnapshot? previous = null)
    {
        if (!OperatingSystem.IsWindows())
            return Hold(WindowsChainStatus.UnsupportedPlatform);
        if (!IsFixtureRoot(fixtureRoot, out var full))
            return Hold(WindowsChainStatus.InvalidScope);
        var nested=Path.Combine(full, "Sirious0911",
            "CodexAccountSwitcher", "TelegramOutbox");
        return InspectChain(nested, privateNodes:4, previous);
    }

    private static WindowsChainInspection InspectChain(
        string target, int privateNodes, WindowsPathChainSnapshot? previous)
    {
        var handles = new List<SafeFileHandle>();
        var identities = new List<WindowsHandleFileId>();
        try
        {
            var root = Path.GetPathRoot(target);
            if (string.IsNullOrEmpty(root))
                return Hold(WindowsChainStatus.InvalidScope);
            if (new DriveInfo(root).DriveType != DriveType.Fixed)
                return Hold(WindowsChainStatus.UntrustedVolume);

            var nodes = new List<string>();
            var next = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(target));
            for (var i=0; i<32; i++)
            {
                nodes.Add(next);
                var parent = Path.GetDirectoryName(next);
                if (parent is null)
                    break;
                next = parent;
            }
            nodes.Reverse();
            if (nodes.Count < privateNodes+1 ||
                !string.Equals(Path.GetPathRoot(nodes[0]),nodes[0],
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetPathRoot(nodes[0]),
                    nodes[0]+Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return Hold(WindowsChainStatus.InvalidScope);

            ulong? volume = null;
            foreach (var (node,index) in nodes.Select((value,index)=>(value,index)))
            {
                var isPrivate = index >= nodes.Count-privateNodes;
                // READ_CONTROL is used only for private app-owned descendants.
                var access = ReadAttributes | (isPrivate ? ReadControl : 0u);
                var handle = CreateFileW(node,access,ShareReadWrite,
                    IntPtr.Zero,OpenExisting,
                    BackupSemantics|OpenReparsePoint,IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    return Hold(error is 2 or 3
                        ?WindowsChainStatus.NodeMissing
                        :WindowsChainStatus.NativeOpenFailed);
                }
                handles.Add(handle);
                if (!GetFileInformationByHandleEx(handle,9,
                    out FileTagInfo tag,(uint)Marshal.SizeOf<FileTagInfo>()))
                    return Hold(WindowsChainStatus.NativeIdentityUnavailable);
                if ((tag.Attributes&ReparseAttribute)!=0)
                    return Hold(WindowsChainStatus.ReparseNodeDetected);
                if ((tag.Attributes&DirectoryAttribute)==0)
                    return Hold(WindowsChainStatus.NotDirectory);
                if (!ReadIdentity(handle,out var identity))
                    return Hold(WindowsChainStatus.NativeIdentityUnavailable);
                if (volume is not null && volume.Value!=identity.VolumeSerial)
                    return Hold(WindowsChainStatus.VolumeChanged);
                volume = identity.VolumeSerial;
                if (isPrivate)
                {
                    var review=WindowsTelegramSameHandleReview.InspectOpenedHandle(
                        handle,true,identity);
                    if (review.Status!=WindowsSameHandleStatus.HandleAclAndIdObservedOnly)
                        return Hold(WindowsChainStatus.PrivateAclUntrusted);
                }
                identities.Add(identity);
            }

            // Every opened ancestor remains held during the second pass.
            for (var i=0;i<handles.Count;i++)
            {
                if (!ReadIdentity(handles[i],out var now) ||
                    identities[i]!=now)
                    return Hold(WindowsChainStatus.ChainChangedDuringReview);
            }
            if (previous is not null &&
                (previous.Identities.Count!=identities.Count ||
                !previous.Identities.SequenceEqual(identities)))
                return Hold(WindowsChainStatus.ChainChangedSinceSnapshot);

            return new WindowsChainInspection(
                WindowsChainStatus.ObservedLocalCandidateOnly,
                new WindowsPathChainSnapshot(identities.AsReadOnly()));
        }
        catch (Exception ex) when (ex is ArgumentException or
            IOException or UnauthorizedAccessException or
            System.Security.SecurityException or NotSupportedException or
            InvalidOperationException)
        {
            return Hold(WindowsChainStatus.NativeInspectionUnavailable);
        }
        finally
        {
            for (var i=handles.Count-1;i>=0;i--)
                handles[i].Dispose();
        }
    }

    private static bool IsFixtureRoot(string? value, out string full)
    {
        full="";
        if (string.IsNullOrWhiteSpace(value) ||
            !Path.IsPathFullyQualified(value) ||
            value.IndexOfAny(['\0','*','?'])>=0 ||
            value.Replace('/','\\').Split('\\').Any(x=>x is "." or ".."))
            return false;
        try
        {
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Path.GetTempPath()));
            var leaf = Path.GetFileName(candidate);
            if (!string.Equals(Path.GetDirectoryName(candidate),temp,
                    StringComparison.OrdinalIgnoreCase) ||
                !leaf.StartsWith(FixturePrefix,StringComparison.Ordinal))
                return false;
            var tail = leaf[FixturePrefix.Length..];
            if (tail.Length!=32 || !tail.All(c=>c is >= '0' and <= '9'
                or >= 'a' and <= 'f'))
                return false;
            full=candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or
            NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool ReadIdentity(SafeFileHandle handle,
        out WindowsHandleFileId id)
    {
        id=new(0,0,0);
        if (!GetFileInformationByHandleEx(handle,18,out FileIdInfo data,
            (uint)Marshal.SizeOf<FileIdInfo>()))
            return false;
        if (data.VolumeSerial==0 || (data.Low==0 && data.High==0))
            return false;
        id=new(data.VolumeSerial,data.Low,data.High);
        return true;
    }

    private static WindowsChainInspection Hold(WindowsChainStatus status)=>
        new(status);

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

    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,
        ExactSpelling=true,SetLastError=true)]
    private static extern SafeFileHandle CreateFileW(
        string path,uint access,uint sharing,IntPtr security,
        uint creationMode,uint flags,IntPtr template);

    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",
        ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle,int informationClass,
        out FileTagInfo info,uint length);

    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",
        ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle,int informationClass,
        out FileIdInfo info,uint length);
}

public enum WindowsChainStatus
{
    UnsupportedPlatform,
    InvalidScope,
    UntrustedVolume,
    NodeMissing,
    NativeOpenFailed,
    NativeIdentityUnavailable,
    ReparseNodeDetected,
    NotDirectory,
    VolumeChanged,
    PrivateAclUntrusted,
    ChainChangedDuringReview,
    ChainChangedSinceSnapshot,
    NativeInspectionUnavailable,
    ObservedLocalCandidateOnly
}

public sealed record WindowsPathChainSnapshot(
    IReadOnlyList<WindowsHandleFileId> Identities);

public sealed record WindowsChainInspection(
    WindowsChainStatus Status,
    WindowsPathChainSnapshot? Snapshot=null)
{
    public bool ProductionOutboxProvisioned=>false;
    public bool ProductionReservationAuthorized=>false;
    public bool TelegramSendAuthorized=>false;
}
