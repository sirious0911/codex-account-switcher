using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher;

/// <summary>Read-only adapter rehearsal. The product admission gate does not call it.</summary>
internal static class WindowsAdmissionFixtureFlow
{
    internal sealed record Binding(long Generation, Guid ApprovalId, string UserSid,
        Guid ProfileId, string AccountSha256, Guid ThreadId, string DeviceId);
    internal sealed record FilePin(WindowsAdmissionRelativeReader.Anchor Identity, string Sha256);
    internal sealed record Head(long Revision, Binding Binding, FilePin Startup,
        FilePin Activation, FilePin Ipc, bool Revoked);
    internal sealed record Sender(string DeviceId, bool Enabled, bool Revoked,
        string HmacSha256);
    internal sealed record Ledger(Binding Binding, Sender[] Senders);
    internal sealed record OwnerProof(Binding Binding, string SenderHmacSha256, string PipeKeySha256);
    internal sealed record PipeProof(Binding Binding, string PipeKeySha256);

    internal static string Check(SafeFileHandle root, WindowsAdmissionRelativeReader.Anchor rootPin,
        (string Name, WindowsAdmissionRelativeReader.Anchor Anchor)[] chain,
        Func<Head> readIndependentHead, Func<Binding> readCurrent,
        Func<Binding, Ledger> readCentral,
        Func<byte[], OwnerProof> decodeFixtureOwner,
        Func<byte[], PipeProof> decodeFixturePipe)
    {
        var head = readIndependentHead();
        var binding = head.Binding;
        if (head.Revision <= 0 || head.Revoked || binding.Generation <= 0 ||
            binding.ApprovalId == Guid.Empty || readCurrent() != binding)
            return "HOLD_FIXTURE_TRUST_OR_ACCOUNT";

        using var reader = new WindowsAdmissionRelativeReader(root, rootPin, chain);
        using var startup = ReadRecord(reader, "codex-owner-startup-v1.json", head.Startup, binding);
        using var activation = ReadRecord(reader, "codex-chat-production-v1.json", head.Activation, binding);
        using var ipc = ReadRecord(reader, "codex-due-ipc-enrollment-v1.json", head.Ipc, binding);
        if (!activation.RootElement.GetProperty("enabled").GetBoolean() ||
            activation.RootElement.GetProperty("profileId").GetGuid() != binding.ProfileId ||
            activation.RootElement.GetProperty("accountSha256").GetString() != binding.AccountSha256 ||
            activation.RootElement.GetProperty("threadId").GetGuid() != binding.ThreadId ||
            activation.RootElement.GetProperty("ownerDevice").GetString() != binding.DeviceId ||
            ipc.RootElement.GetProperty("ProfileId").GetGuid() != binding.ProfileId ||
            ipc.RootElement.GetProperty("AccountSha256").GetString() != binding.AccountSha256 ||
            ipc.RootElement.GetProperty("ThreadId").GetGuid() != binding.ThreadId)
            return "HOLD_FIXTURE_ACTIVATION_OR_IPC";

        // Only bytes parsed from verified and pinned file handles reach the
        // decoders. Fixture callbacks do not invoke DPAPI or retain bytes.
        var owner = Decode(startup, "PrivateConfigDpapiBase64", decodeFixtureOwner);
        var pipe = Decode(ipc, "ProtectedSecretBase64", decodeFixturePipe);
        if (owner.Binding != binding || pipe.Binding != binding ||
            !IsHash(owner.PipeKeySha256) || owner.PipeKeySha256 != pipe.PipeKeySha256)
            return "HOLD_FIXTURE_DPAPI_BINDING";

        var ledger = readCentral(binding);
        if (ledger.Binding != binding || ledger.Senders.Length != 1 ||
            ledger.Senders[0] is not { Enabled: true, Revoked: false } sender ||
            sender.DeviceId != binding.DeviceId || !IsHash(owner.SenderHmacSha256) ||
            sender.HmacSha256 != owner.SenderHmacSha256)
            return "HOLD_FIXTURE_LEDGER_BINDING";

        if (readIndependentHead() != head || readCurrent() != binding)
            return "HOLD_FIXTURE_ADMISSION_CHANGED";
        reader.Recheck();
        return "SYNTHETIC_HANDLE_PREFLIGHT_PASS_NO_SEND";
    }

    private static bool IsHash(string value) => value is { Length: 64 } &&
        value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');

    private static JsonDocument ReadRecord(WindowsAdmissionRelativeReader reader, string name,
        FilePin pin, Binding binding)
    {
        var captured = reader.Read(name, pin.Identity);
        if (captured.Sha256 != pin.Sha256) throw new InvalidDataException("HOLD_FIXTURE_FILE_HASH");
        var doc = JsonDocument.Parse(captured.Bytes);
        try
        {
            var row = doc.RootElement;
            var properties = row.EnumerateObject().Select(property => property.Name).ToArray();
            if (properties.Distinct(StringComparer.OrdinalIgnoreCase).Count() != properties.Length ||
                row.GetProperty("schemaVersion").GetInt32() != 1 ||
                row.GetProperty("generation").GetInt64() != binding.Generation ||
                row.GetProperty("approvalId").GetGuid() != binding.ApprovalId)
                throw new InvalidDataException("HOLD_FIXTURE_FILE_GENERATION");
            return doc;
        }
        catch { doc.Dispose(); throw; }
    }

    private static T Decode<T>(JsonDocument src, string property, Func<byte[], T> decode)
    {
        var blob = src.RootElement.GetProperty(property).GetBytesFromBase64();
        try
        {
            if (blob.Length is < 1 or > 30000)
                throw new InvalidDataException("HOLD_FIXTURE_PROTECTED_BLOB");
            return decode(blob);
        }
        finally { CryptographicOperations.ZeroMemory(blob); }
    }
}
