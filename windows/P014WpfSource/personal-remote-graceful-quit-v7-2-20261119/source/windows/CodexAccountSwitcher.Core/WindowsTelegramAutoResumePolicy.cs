using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Durable user intent, NOT a send permit. This P0 component is intentionally
/// not constructed by the running App and cannot arm WindowsTelegramAutoSendSession.
/// </summary>
internal sealed record WindowsTelegramAutoResumePolicy(
    int Version,
    bool Desired,
    Guid InstallationId,
    string UserBinding,
    string AuthorityBinding,
    bool FiveHour,
    bool Weekly,
    int ConsentRevision,
    DateTimeOffset ApprovedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    internal const int CurrentVersion = 1;
    internal const int CurrentConsentRevision = 1;
    internal static readonly TimeSpan MaximumConsentAge = TimeSpan.FromDays(30);

    internal static WindowsTelegramAutoResumePolicy? Approved(
        Guid installationId, string userBinding, string authorityBinding,
        TelegramNotificationPreferences preferences, DateTimeOffset now,
        bool explicitlyApproved)
    {
        if (!explicitlyApproved || installationId == Guid.Empty ||
            !ValidBinding(userBinding) || !ValidBinding(authorityBinding) ||
            !(preferences.WantsFiveHour || preferences.WantsWeekly))
            return null;
        var approvedAtUtc = now.ToUniversalTime();
        return new(CurrentVersion, true, installationId, userBinding,
            authorityBinding, preferences.WantsFiveHour,
            preferences.WantsWeekly, CurrentConsentRevision, approvedAtUtc,
            approvedAtUtc + MaximumConsentAge);
    }

    internal static bool ValidBinding(string? value) =>
        value is { Length: 64 } && value.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal bool StructurallyValid =>
        Version == CurrentVersion &&
        ConsentRevision == CurrentConsentRevision &&
        InstallationId != Guid.Empty && ValidBinding(UserBinding) &&
        ValidBinding(AuthorityBinding) &&
        ApprovedAtUtc.Offset == TimeSpan.Zero &&
        ExpiresAtUtc.Offset == TimeSpan.Zero &&
        ExpiresAtUtc > ApprovedAtUtc &&
        ExpiresAtUtc - ApprovedAtUtc <= MaximumConsentAge &&
        (!Desired || FiveHour || Weekly);
}

internal enum WindowsTelegramAutoResumeReadStatus
{
    Missing, Revoked, ValidIntent, InvalidOrUnreadable
}

internal sealed record WindowsTelegramAutoResumeRead(
    WindowsTelegramAutoResumeReadStatus Status,
    WindowsTelegramAutoResumePolicy? Policy = null);

/// <summary>
/// All outcomes are NON-SENDING. Even a fully verified local intent never
/// implies a current global lease or the right to invoke a Telegram process.
/// </summary>
internal enum WindowsTelegramAutoResumeHold
{
    NoDurableOptIn,
    MalformedOrUnreadable,
    Expired,
    IdentityMismatch,
    IntentChanged,
    AuthorityUnverified,
    RemoteOwnerExists,
    PendingRemoteFence,
    OutboxUncertain,
    CandidateOnlyRequiresProductionGate
}

internal sealed record WindowsTelegramAutoResumeAssessment(
    WindowsTelegramAutoResumeHold Status)
{
    internal bool CanAutomaticallySend => false;
}

internal static class WindowsTelegramAutoResumePolicyGate
{
    internal static WindowsTelegramAutoResumeAssessment Inspect(
        WindowsTelegramAutoResumeRead saved, Guid installationId,
        string userBinding, string authorityBinding,
        TelegramNotificationPreferences current,
        DateTimeOffset now,
        bool verifiedSingleOwnerAuthority,
        bool otherSenderExists,
        bool noOutstandingRemoteFence,
        bool noUncertainOutbox)
    {
        if (saved.Status is WindowsTelegramAutoResumeReadStatus.Missing
            or WindowsTelegramAutoResumeReadStatus.Revoked)
            return new(WindowsTelegramAutoResumeHold.NoDurableOptIn);
        if (saved.Status != WindowsTelegramAutoResumeReadStatus.ValidIntent ||
            saved.Policy is not { Desired: true, StructurallyValid: true } policy)
            return new(WindowsTelegramAutoResumeHold.MalformedOrUnreadable);
        if (now < policy.ApprovedAtUtc || now >= policy.ExpiresAtUtc)
            return new(WindowsTelegramAutoResumeHold.Expired);
        if (policy.InstallationId != installationId ||
            policy.UserBinding != userBinding ||
            policy.AuthorityBinding != authorityBinding)
            return new(WindowsTelegramAutoResumeHold.IdentityMismatch);
        if (current.DeviceRole != TelegramNotificationDeviceRole.Sender ||
            !current.NotificationsRequested ||
            current.WantsFiveHour != policy.FiveHour ||
            current.WantsWeekly != policy.Weekly)
            return new(WindowsTelegramAutoResumeHold.IntentChanged);
        if (!verifiedSingleOwnerAuthority)
            return new(WindowsTelegramAutoResumeHold.AuthorityUnverified);
        if (otherSenderExists)
            return new(WindowsTelegramAutoResumeHold.RemoteOwnerExists);
        if (!noOutstandingRemoteFence)
            return new(WindowsTelegramAutoResumeHold.PendingRemoteFence);
        if (!noUncertainOutbox)
            return new(WindowsTelegramAutoResumeHold.OutboxUncertain);
        // A reviewed, live, epoch-bound send-time authority is required in P1.
        return new(WindowsTelegramAutoResumeHold.CandidateOnlyRequiresProductionGate);
    }
}

/// <summary>
/// Isolated Windows per-user DPAPI file: default missing=OFF, damaged=OFF,
/// explicit revocation writes an encrypted tombstone (never deletes an
/// existing granted file first). No Telegram credential is ever stored.
/// </summary>
internal sealed class WindowsTelegramAutoResumeStore
{
    private const string FileName = "telegram-auto-resume-intent.v1.dpapi";
    private const int MaxEncryptedBytes = 8192;
    private readonly string directory;
    private readonly string file;
    private readonly object sync = new();

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false
    };

    internal WindowsTelegramAutoResumeStore(string syntheticOrUserStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(syntheticOrUserStateDirectory);
        directory = Path.GetFullPath(syntheticOrUserStateDirectory);
        file = Path.Combine(directory, FileName);
    }

    internal WindowsTelegramAutoResumeRead Read()
    {
        lock (sync)
        {
            try
            {
                SecureFiles.RejectLinks(file);
                if (!File.Exists(file))
                    return new(WindowsTelegramAutoResumeReadStatus.Missing);
                var data = new FileInfo(file);
                if (data.Length is <= 0 or > MaxEncryptedBytes)
                    return new(WindowsTelegramAutoResumeReadStatus.InvalidOrUnreadable);
                byte[] encrypted = File.ReadAllBytes(file);
                byte[]? plaintext = null;
                try
                {
                    plaintext = WindowsCurrentUserDpapi.Unprotect(encrypted);
                    var policy = JsonSerializer.Deserialize<WindowsTelegramAutoResumePolicy>(
                        plaintext, StrictJson);
                    if (policy?.StructurallyValid != true)
                        return new(WindowsTelegramAutoResumeReadStatus.InvalidOrUnreadable);
                    return policy.Desired
                        ? new(WindowsTelegramAutoResumeReadStatus.ValidIntent, policy)
                        : new(WindowsTelegramAutoResumeReadStatus.Revoked);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encrypted);
                    if (plaintext is not null)
                        CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or CryptographicException or JsonException or ArgumentException
                or Win32Exception or System.Security.SecurityException
                or NotSupportedException)
            {
                return new(WindowsTelegramAutoResumeReadStatus.InvalidOrUnreadable);
            }
        }
    }

    internal bool SaveExplicitlyApproved(WindowsTelegramAutoResumePolicy? policy)
    {
        if (policy is not { Desired: true, StructurallyValid: true })
            return false;
        return Save(policy);
    }

    internal bool Revoke(Guid installationId, string userBinding,
        string authorityBinding, DateTimeOffset now)
    {
        if (installationId == Guid.Empty ||
            !WindowsTelegramAutoResumePolicy.ValidBinding(userBinding) ||
            !WindowsTelegramAutoResumePolicy.ValidBinding(authorityBinding))
            return false;
        // Write a durable OFF tombstone, not deletion: failure to persist
        // OFF must never be misreported as successful Mac/other-host handoff.
        return Save(new(WindowsTelegramAutoResumePolicy.CurrentVersion,
            false, installationId, userBinding, authorityBinding, false, false,
            WindowsTelegramAutoResumePolicy.CurrentConsentRevision,
            now.ToUniversalTime(), now.ToUniversalTime().AddDays(1)));
    }

    private bool Save(WindowsTelegramAutoResumePolicy policy)
    {
        lock (sync)
        {
            byte[]? plaintext = null;
            byte[]? encrypted = null;
            try
            {
                SecureFiles.RejectLinks(file);
                if (!Directory.Exists(directory))
                    SecureFiles.CreatePrivateDirectory(directory);
                plaintext = JsonSerializer.SerializeToUtf8Bytes(policy, StrictJson);
                encrypted = WindowsCurrentUserDpapi.Protect(plaintext);
                if (encrypted.Length is <= 0 or > MaxEncryptedBytes)
                    return false;
                SecureFiles.AtomicWrite(file, encrypted);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or CryptographicException or ArgumentException or Win32Exception
                or System.Security.SecurityException or NotSupportedException)
            {
                return false;
            }
            finally
            {
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
                if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }
}

/// <summary>
/// CurrentUser DPAPI with no LocalMachine flag. No UI prompt or network.
/// A protected policy is NEVER a process-level Telegram send grant.
/// </summary>
internal static class WindowsCurrentUserDpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Count;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input,
        IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt,
        uint flags, out Blob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr block);

    internal static byte[] Protect(byte[] data) => Transform(data, encrypt: true);
    internal static byte[] Unprotect(byte[] data) => Transform(data, encrypt: false);

    private static byte[] Transform(byte[] data, bool encrypt)
    {
        if (!OperatingSystem.IsWindows() || data.Length is <= 0 or > 8192)
            throw new CryptographicException("Invalid protected policy input.");
        IntPtr source = Marshal.AllocHGlobal(data.Length);
        var input = new Blob { Count = data.Length, Data = source };
        Blob output = default;
        try
        {
            Marshal.Copy(data, 0, source, data.Length);
            bool succeeded = encrypt
                ? CryptProtectData(ref input, "Codex Switcher auto-resume user intent",
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, 0x1, out output);
            if (!succeeded)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (output.Data == IntPtr.Zero || output.Count is <= 0 or > 8192)
                throw new CryptographicException("Invalid protected policy size.");
            var result = new byte[output.Count];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.Copy(new byte[data.Length], 0, source, data.Length);
            Marshal.FreeHGlobal(source);
            if (output.Data != IntPtr.Zero)
            {
                if (output.Count is > 0 and <= 8192)
                    Marshal.Copy(new byte[output.Count], 0, output.Data, output.Count);
                LocalFree(output.Data);
            }
        }
    }
}
