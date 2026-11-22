using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher;

/// <summary>
/// Read-only operating trust-source adapter candidate. Does not issue any
/// keys, approve a generation, read credentials/DPAPI, or start an Owner.
/// A positive snapshot is still only input to separately reviewed P0-15
/// signed-HEAD verification; it NEVER grants product admission.
/// </summary>
internal static class WindowsP015AdminTrustSource
{
    private const string PolicyKey = @"SOFTWARE\CodexAccountSwitcher\OwnerTrustV1";
    private const string AdminSid = "S-1-5-32-544";
    private const string SystemSid = "S-1-5-18";
    private const int MaxPublicPem = 4096;
    private const uint OwnerAndDacl = 0x00000001u | 0x00000004u;
    private const uint InsufficientBuffer = 122;
    // Never confuse READ_CONTROL (0x00020000) with WRITE_DAC (0x00040000).
    // GENERIC_WRITE and GENERIC_ALL are also grants of mutation rights.
    // Registry and file effective-mutation masks are reviewed separately
    // by the shared, pure ACL-policy helper.

    internal sealed record Floor(long Revision, long ActiveGeneration);
    internal sealed record Snapshot(byte[] SignedHeadBytes, string ApprovalSpkiPem,
        string IssuerSpkiPem, Floor ProtectedFloor,
        WindowsAdmissionFileEvidence.Identity HeadFileIdentity);
    internal sealed record ProbeResult(string Code, bool IsAvailable);

    // This fixed machine scope is never derived from user dataHome,
    // enrollment JSON, supplied HEAD, an environment variable, or argv.
    internal static string HeadFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CodexAccountSwitcher", "OwnerTrustV1", "head-envelope.json");

    internal static ProbeResult Probe()
    {
        try
        {
            var data = Read();
            if (data.SignedHeadBytes.Length == 0)
                return new("HOLD_P015_EMPTY_HEAD", false);
            return new("P015_PROTECTED_INPUTS_PRESENT_SIGNATURE_UNCHECKED_NO_SEND", true);
        }
        catch (InvalidDataException e)
        {
            return new(e.Message.StartsWith("HOLD_P015_", StringComparison.Ordinal)
                ? e.Message : "HOLD_P015_READER_ERROR", false);
        }
        catch (Exception)
        {
            return new("HOLD_P015_READER_UNAVAILABLE", false);
        }
    }

    internal static Snapshot Read()
    {
        if (!OperatingSystem.IsWindows()) throw Hold("HOLD_P015_WINDOWS_ONLY");
        // Registry view is fixed to prevent process-bitness confusion.
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = machine.OpenSubKey(PolicyKey, writable: false);
        if (key is null) throw Hold("HOLD_P015_ADMIN_POLICY_MISSING");
        ValidateProtectedPolicyAcl(key.Handle);
        string approval = ReadPublicKey(key, "ApprovalHeadSpkiPem");
        string issuer = ReadPublicKey(key, "IssuerHeadSpkiPem");
        if (approval == issuer) throw Hold("HOLD_P015_HEAD_SIGNERS_NOT_SEPARATE");
        long revision = ReadPositiveQword(key, "MinimumRevision");
        long generation = ReadPositiveQword(key, "MinimumGeneration");

        // No directory creation, no write, no access to user DPAPI or SQLite.
        // Pin each parent by an open handle, require administrator-only write
        // ACL, and open the next component via NT relative file lookup. This
        // removes the check-then-open absolute-path swap window.
        var captured = WindowsP015RelativeHeadReader.Read();
        // Detect an administrator policy update while we obtained HEAD bytes.
        // A caller still has to verify the dual HEAD signatures and re-read
        // this independently protected floor after cryptographic admission.
        ValidateProtectedPolicyAcl(key.Handle);
        if (ReadPositiveQword(key, "MinimumRevision") != revision ||
            ReadPositiveQword(key, "MinimumGeneration") != generation ||
            ReadPublicKey(key, "ApprovalHeadSpkiPem") != approval ||
            ReadPublicKey(key, "IssuerHeadSpkiPem") != issuer)
            throw Hold("HOLD_P015_ADMIN_POLICY_CHANGED");
        return new(captured.Bytes, approval, issuer,
            new Floor(revision, generation), captured.Identity);
    }

    private static string ReadPublicKey(RegistryKey key, string name)
    {
        if (key.GetValueKind(name) != RegistryValueKind.String ||
            key.GetValue(name) is not string pem ||
            pem.Length < 48 || pem.Length > MaxPublicPem ||
            !pem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal) ||
            pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw Hold("HOLD_P015_PUBLIC_KEY_POLICY");
        return pem;
    }

    private static long ReadPositiveQword(RegistryKey key, string name)
    {
        if (key.GetValueKind(name) != RegistryValueKind.QWord ||
            key.GetValue(name) is not long number || number < 1)
            throw Hold("HOLD_P015_PROTECTED_FLOOR");
        return number;
    }

    private static void ValidateProtectedPolicyAcl(SafeRegistryHandle handle)
    {
        uint size = 0;
        uint status = RegGetKeySecurity(handle, OwnerAndDacl, IntPtr.Zero, ref size);
        if (status != InsufficientBuffer || size == 0 || size > 65536)
            throw Hold("HOLD_P015_POLICY_ACL");
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            status = RegGetKeySecurity(handle, OwnerAndDacl, buffer, ref size);
            if (status != 0) throw Hold("HOLD_P015_POLICY_ACL");
            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            if (!WindowsP015AclPolicy.Accepts(security, registry: true))
                throw Hold("HOLD_P015_POLICY_WRITE_ACL");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void ValidateHeadFileAcl(SafeFileHandle handle)
    {
        uint code = GetSecurityInfo(handle, 1, OwnerAndDacl, out _,
            IntPtr.Zero, out _, IntPtr.Zero, out var descriptor);
        if (code != 0 || descriptor == IntPtr.Zero)
            throw Hold("HOLD_P015_HEAD_ACL_OR_IDENTITY");
        try
        {
            uint length = GetSecurityDescriptorLength(descriptor);
            if (length == 0 || length > 65536)
                throw Hold("HOLD_P015_HEAD_ACL_OR_IDENTITY");
            var bytes = new byte[checked((int)length)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            if (!WindowsP015AclPolicy.Accepts(security, registry: false))
                throw Hold("HOLD_P015_HEAD_WRITE_ACL");
        }
        finally { LocalFree(descriptor); }
    }

    private static InvalidDataException Hold(string code) => new(code);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType,
        uint securityInformation, out IntPtr owner, IntPtr group,
        out IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr descriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint RegGetKeySecurity(SafeRegistryHandle key,
        uint securityInformation, IntPtr descriptor, ref uint size);
}
