using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher;

/// <summary>Captures identity and bytes from the same already-open file handle.</summary>
internal static class WindowsAdmissionFileEvidence
{
    internal sealed record Identity(ulong Volume, string FileId, string OwnerSid,
        string DaclSha256, bool ProtectedDacl, uint LinkCount, ulong LastWriteTime);
    internal sealed record Snapshot(Identity Identity, string Sha256, byte[] Bytes);

    internal static Snapshot Read(SafeFileHandle handle)
    {
        var before = Inspect(handle);
        long length = RandomAccess.GetLength(handle);
        if (length is < 2 or > 65536)
            throw new InvalidDataException("HOLD_ADMISSION_FILE_SIZE");
        var bytes = new byte[(int)length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (count == 0)
                throw new InvalidDataException("HOLD_ADMISSION_FILE_CHANGED");
            offset += count;
        }
        if (Inspect(handle) != before || RandomAccess.GetLength(handle) != length)
            throw new InvalidDataException("HOLD_ADMISSION_FILE_CHANGED");
        return new(before, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
    }

    internal static Identity Inspect(SafeFileHandle handle, bool directory = false)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid || handle.IsClosed ||
            GetFileType(handle) != 1 ||
            !GetFileInformationByHandle(handle, out var info) ||
            (info.Attributes & 0x400u) != 0 || ((info.Attributes & 0x10u) != 0) != directory ||
            (!directory && info.Links != 1) ||
            !GetFileInformationByHandleEx(handle, 18, out var id, 24))
            throw new InvalidDataException("HOLD_ADMISSION_FILE_IDENTITY");

        uint error = GetSecurityInfo(handle, 1, 5, out _, IntPtr.Zero,
            out _, IntPtr.Zero, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            var length = GetSecurityDescriptorLength(descriptor);
            if (length is 0 or > 65536) throw new InvalidDataException("HOLD_ADMISSION_FILE_ACL");
            var binary = new byte[(int)length];
            Marshal.Copy(descriptor, binary, 0, binary.Length);
            var security = new RawSecurityDescriptor(binary, 0);
            var dacl = security.DiscretionaryAcl;
            if (security.Owner is null || dacl is null)
                throw new InvalidDataException("HOLD_ADMISSION_FILE_ACL");
            var acl = new byte[dacl.BinaryLength];
            dacl.GetBinaryForm(acl, 0);
            return new(id.Volume, $"{id.High:x16}{id.Low:x16}", security.Owner.Value,
                Convert.ToHexStringLower(SHA256.HashData(acl)),
                security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected),
                info.Links, ((ulong)info.WriteHigh << 32) | info.WriteLow);
        }
        finally { LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LegacyFileInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh,
            WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong Volume, Low, High; }

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out LegacyFileInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int type,
        out FileIdInfo info, uint size);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint type, uint securityInfo,
        out IntPtr owner, IntPtr group, out IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr address);
}
