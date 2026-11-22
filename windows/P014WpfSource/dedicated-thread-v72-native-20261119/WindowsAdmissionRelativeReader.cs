using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher;

/// <summary>Reads a reviewed tree from an independently opened root. Fixture only.</summary>
internal sealed class WindowsAdmissionRelativeReader : IDisposable
{
    private readonly List<SafeFileHandle> owned = new();
    private readonly SafeFileHandle directory;
    private readonly ulong volume;
    private readonly HashSet<string> identities = new(StringComparer.Ordinal);
    private readonly List<(SafeFileHandle Handle, Anchor Anchor)> directories = new();
    private readonly List<(SafeFileHandle Handle, WindowsAdmissionFileEvidence.Identity Identity)> files = new();
    private bool disposed;

    internal sealed record Anchor(ulong Volume, string FileId, string OwnerSid,
        string DaclSha256, bool ProtectedDacl);

    internal WindowsAdmissionRelativeReader(SafeFileHandle trustedRoot, Anchor root,
        IReadOnlyList<(string Name, Anchor Anchor)> chain)
    {
        if (chain.Count > 16) throw new InvalidDataException("HOLD_ADMISSION_DIRECTORY_DEPTH");
        directory = DuplicateRoot(trustedRoot);
        volume = root.Volume;
        owned.Add(directory);
        try
        {
            Verify(directory, root, directory: true);
            directories.Add((directory, root));
            foreach (var item in chain)
            {
                var next = OpenRelative(directory, item.Name, directory: true);
                owned.Add(next);
                Verify(next, item.Anchor, directory: true);
                directories.Add((next, item.Anchor));
                directory = next;
            }
        }
        catch { Dispose(); throw; }
    }

    internal WindowsAdmissionFileEvidence.Snapshot Read(string name, Anchor anchor)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RecheckDirectories();
        var handle = OpenRelative(directory, name, directory: false);
        try
        {
            var snapshot = WindowsAdmissionFileEvidence.Read(handle);
            Verify(snapshot.Identity, anchor);
            RecheckDirectories();
            owned.Add(handle);
            files.Add((handle, snapshot.Identity));
            return snapshot;
        }
        catch { handle.Dispose(); throw; }
    }

    private void Verify(SafeFileHandle handle, Anchor anchor, bool directory) =>
        Verify(WindowsAdmissionFileEvidence.Inspect(handle, directory), anchor);

    private void Verify(WindowsAdmissionFileEvidence.Identity id, Anchor anchor)
    {
        if (!Matches(id, anchor) || !identities.Add(id.FileId))
            throw new InvalidDataException("HOLD_ADMISSION_ANCHOR_MISMATCH");
    }

    private bool Matches(WindowsAdmissionFileEvidence.Identity id, Anchor anchor) =>
        id.Volume == volume && id.Volume == anchor.Volume && id.FileId == anchor.FileId &&
        id.OwnerSid == anchor.OwnerSid && id.DaclSha256 == anchor.DaclSha256 &&
        id.ProtectedDacl == anchor.ProtectedDacl;

    internal void Recheck()
    {
        RecheckDirectories();
        foreach (var file in files)
            if (WindowsAdmissionFileEvidence.Inspect(file.Handle) != file.Identity)
                throw new InvalidDataException("HOLD_ADMISSION_FILE_CHANGED");
    }

    private void RecheckDirectories()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var row in directories)
            if (!Matches(WindowsAdmissionFileEvidence.Inspect(row.Handle, directory: true), row.Anchor))
                throw new InvalidDataException("HOLD_ADMISSION_DIRECTORY_CHANGED");
    }

    private static SafeFileHandle DuplicateRoot(SafeFileHandle root)
    {
        var process = GetCurrentProcess();
        if (!DuplicateHandle(process, root, process, out var duplicate, 0, false, 2))
            throw new IOException("HOLD_ADMISSION_ROOT_HANDLE");
        return duplicate;
    }


    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        for (int i = owned.Count - 1; i >= 0; --i) owned[i].Dispose();
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, bool directory)
    {
        // Each open accepts precisely one ordinary path component.
        if (string.IsNullOrEmpty(name) || name.Length > 120 ||
            name[0] == '.' || name[^1] == '.' ||
            !System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[A-Za-z0-9_.-]+\z"))
            throw new InvalidDataException("HOLD_ADMISSION_RELATIVE_NAME");

        IntPtr text = Marshal.StringToHGlobalUni(name);
        IntPtr nameBuffer = IntPtr.Zero;
        bool retained = false;
        try
        {
            var unicode = new UnicodeString {
                Length = (ushort)(name.Length * 2),
                MaximumLength = (ushort)((name.Length + 1) * 2),
                Buffer = text
            };
            nameBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, nameBuffer, false);
            parent.DangerousAddRef(ref retained);
            var attributes = new ObjectAttributes {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                Root = parent.DangerousGetHandle(),
                Name = nameBuffer,
                Attributes = 0x1000 // OBJ_DONT_REPARSE
            };
            int status = NtCreateFile(out var handle, 0x120081, ref attributes, out _,
                IntPtr.Zero, 0, 1, 1, 0x200020u | (directory ? 1u : 0x40u),
                IntPtr.Zero, 0);
            if (status == 0 && handle is not null && !handle.IsInvalid) return handle;
            handle?.Dispose();
            throw new IOException($"HOLD_ADMISSION_RELATIVE_OPEN_{unchecked((uint)status):X8}");
        }
        finally
        {
            if (retained) parent.DangerousRelease();
            if (nameBuffer != IntPtr.Zero) Marshal.FreeHGlobal(nameBuffer);
            Marshal.FreeHGlobal(text);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr Root, Name;
        public uint Attributes;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source,
        IntPtr targetProcess, out SafeFileHandle target, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access,
        ref ObjectAttributes attributes, out IoStatusBlock status, IntPtr allocationSize,
        uint fileAttributes, uint share, uint disposition, uint options,
        IntPtr eaBuffer, uint eaLength);
}
