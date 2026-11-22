// Read-only, separate trust-source preflight. No positive operating admission.
// Never emits protected bytes, signatures, or signer keys to console.
using CodexAccountSwitcher;
using System.Security.Cryptography;
using System.Text.Json;

// Explicit synthetic-only pure ACL test: never touches the OS trust store.
if (args.Length == 1 && args[0] == "--synthetic-acl-checks")
    return WindowsP015AclPolicyChecks.Run();
if (args.Length == 1 && args[0] == "--synthetic-relative-head-checks")
    return WindowsP015RelativeHeadChecks.Run();

// Explicit read-only interoperability channel. It only emits a signed HEAD
// envelope and public verification inputs from an independently protected OS
// policy; never prints private keys, DPAPI data, or owner credentials.
// The CLI always exits 2: input acquisition is NOT operating authorization.
if (args.Length == 1 && args[0] == "--emit-public-trust-input-v1")
{
    try
    {
        var snapshot = WindowsP015AdminTrustSource.Read();
        var input = new
        {
            schemaVersion = 1,
            source = "P015_WINDOWS_ADMIN_READONLY",
            signedHeadBase64 = Convert.ToBase64String(snapshot.SignedHeadBytes),
            signedHeadSha256 = Convert.ToHexStringLower(
                SHA256.HashData(snapshot.SignedHeadBytes)),
            headKeys = new
            {
                approval = snapshot.ApprovalSpkiPem,
                issuer = snapshot.IssuerSpkiPem
            },
            floor = new
            {
                revision = snapshot.ProtectedFloor.Revision,
                activeGeneration = snapshot.ProtectedFloor.ActiveGeneration
            },
            headFile = new
            {
                volume = snapshot.HeadFileIdentity.Volume.ToString(),
                fileId = snapshot.HeadFileIdentity.FileId,
                ownerSid = snapshot.HeadFileIdentity.OwnerSid,
                daclSha256 = snapshot.HeadFileIdentity.DaclSha256
            }
        };
        Console.Out.WriteLine(JsonSerializer.Serialize(input));
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error is System.IO.InvalidDataException &&
            error.Message.StartsWith("HOLD_P015_", StringComparison.Ordinal)
            ? error.Message : "HOLD_P015_READER_UNAVAILABLE");
    }
    return 2;
}

var result = WindowsP015AdminTrustSource.Probe();
Console.WriteLine(result.Code);
// A raw input-presence probe is never sufficient for release approval.
return 2;
