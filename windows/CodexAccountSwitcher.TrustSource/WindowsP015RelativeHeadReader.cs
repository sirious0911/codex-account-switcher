using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher;

/// <summary>
/// Read the signed HEAD from an OS-reported ProgramData root, opening every
/// child relative to a held parent handle with OBJ_DONT_REPARSE.
/// Both application-managed parent directories must have administrator-only
/// write ACLs. Never opens the user enrollment or private credential tree.
/// </summary>
internal static class WindowsP015RelativeHeadReader
{
    private const uint ReadControl = 0x120081;
    private const uint FileShareRead = 1;
    // Win32 OPEN_EXISTING (3) and NT FILE_OPEN (1) are DIFFERENT enums.
    // Passing 3 to NtCreateFile means FILE_OPEN_IF, which can CREATE a
    // missing trust directory. Keep the two dispositions distinct.
    private const uint Win32OpenExisting = 3;
    private const uint NtFileOpenOnly = 1;
    private const uint BackupSemanticsAndOpenReparsePoint = 0x02200000;

    internal static WindowsAdmissionFileEvidence.Snapshot Read()
    {
        var programData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);
        return ReadFromRoot(programData);
    }

    // This exact code path is also exercised against a disposable user-owned
    // fixture root. Only the fixed OS ProgramData root is used by production.
    internal static WindowsAdmissionFileEvidence.Snapshot ReadFromFixtureRoot(
        string temporaryRoot) => ReadFromRoot(temporaryRoot);

    private static WindowsAdmissionFileEvidence.Snapshot ReadFromRoot(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidDataException("HOLD_P015_WINDOWS_ONLY");
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("HOLD_P015_PROGRAMDATA_ROOT");
        using var root = CreateFileW(path, ReadControl, FileShareRead,
            IntPtr.Zero, Win32OpenExisting, BackupSemanticsAndOpenReparsePoint,
            IntPtr.Zero);
        if (root.IsInvalid)
            throw new InvalidDataException("HOLD_P015_PROGRAMDATA_ROOT");
        VerifyExactPath(root, path);
        var rootBefore = WindowsAdmissionFileEvidence.Inspect(root, directory: true);
        using var app = OpenRelative(root, "CodexAccountSwitcher", directory: true);
        VerifyExactPath(app, Path.Combine(path, "CodexAccountSwitcher"));
        var appBefore = CheckProtectedDirectory(app);
        using var folder = OpenRelative(app, "OwnerTrustV1", directory: true);
        VerifyExactPath(folder, Path.Combine(path, "CodexAccountSwitcher", "OwnerTrustV1"));
        var folderBefore = CheckProtectedDirectory(folder);
        using var head = OpenRelative(folder, "head-envelope.json", directory: false);
        VerifyExactPath(head, Path.Combine(path, "CodexAccountSwitcher", "OwnerTrustV1",
            "head-envelope.json"));

        WindowsP015AdminTrustSource.ValidateHeadFileAcl(head);
        var snapshot = WindowsAdmissionFileEvidence.Read(head);
        if (snapshot.Identity.OwnerSid is not (
            WindowsP015AclPolicy.AdminSid or WindowsP015AclPolicy.SystemSid) ||
            !snapshot.Identity.ProtectedDacl || snapshot.Identity.LinkCount != 1)
            throw new InvalidDataException("HOLD_P015_HEAD_ACL_OR_IDENTITY");
        WindowsP015AdminTrustSource.ValidateHeadFileAcl(head);

        if (rootBefore != WindowsAdmissionFileEvidence.Inspect(root, directory: true) ||
            appBefore != CheckProtectedDirectory(app) ||
            folderBefore != CheckProtectedDirectory(folder) ||
            snapshot.Identity != WindowsAdmissionFileEvidence.Inspect(head))
            throw new InvalidDataException("HOLD_P015_HEAD_PATH_CHANGED");
        return snapshot;
    }

    private static WindowsAdmissionFileEvidence.Identity CheckProtectedDirectory(
        SafeFileHandle handle)
    {
        var identity = WindowsAdmissionFileEvidence.Inspect(handle, directory: true);
        if (identity.OwnerSid is not (
                WindowsP015AclPolicy.AdminSid or WindowsP015AclPolicy.SystemSid) ||
            !identity.ProtectedDacl)
            throw new InvalidDataException("HOLD_P015_PARENT_ACL");
        WindowsP015AdminTrustSource.ValidateHeadFileAcl(handle);
        return identity;
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name,
        bool directory)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > 120 || name[0] == '.' || name[^1] == '.' ||
            !Regex.IsMatch(name, @"\A[A-Za-z0-9_.-]+\z"))
            throw new InvalidDataException("HOLD_P015_RELATIVE_NAME");

        IntPtr buffer = Marshal.StringToHGlobalUni(name);
        IntPtr unicode = IntPtr.Zero;
        bool retained = false;
        try
        {
            var text = new UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)),
                MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                Buffer = buffer
            };
            unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(text, unicode, false);
            parent.DangerousAddRef(ref retained);
            var attrs = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                Root = parent.DangerousGetHandle(),
                Name = unicode,
                Attributes = 0x1000 // OBJ_DONT_REPARSE
            };
            var code = NtCreateFile(out var result, ReadControl, ref attrs,
                out _, IntPtr.Zero, 0, FileShareRead, NtFileOpenOnly,
                0x200020u | (directory ? 1u : 0x40u), IntPtr.Zero, 0);
            if (code == 0 && result is not null && !result.IsInvalid)
                return result;
            result?.Dispose();
            throw new InvalidDataException(
                $"HOLD_P015_RELATIVE_OPEN_{unchecked((uint)code):X8}");
        }
        finally
        {
            if (retained) parent.DangerousRelease();
            if (unicode != IntPtr.Zero) Marshal.FreeHGlobal(unicode);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void VerifyExactPath(SafeFileHandle handle, string expected)
    {
        var output = new StringBuilder(4096);
        uint length = GetFinalPathNameByHandleW(handle, output,
            checked((uint)output.Capacity), 0);
        if (length == 0 || length >= output.Capacity)
            throw new InvalidDataException("HOLD_P015_OPENED_PATH");
        string actual = output.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal))
            actual = actual[4..];
        if (!string.Equals(Path.GetFullPath(actual).TrimEnd('\\'),
            Path.GetFullPath(expected).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("HOLD_P015_OPENED_PATH_MISMATCH");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr Root, Name;
        public uint Attributes;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle,
        StringBuilder buffer, uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access,
        uint share, IntPtr security, uint disposition, uint flags,
        IntPtr template);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access,
        ref ObjectAttributes attributes, out IoStatusBlock status,
        IntPtr allocationSize, uint fileAttributes, uint share,
        uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
}
