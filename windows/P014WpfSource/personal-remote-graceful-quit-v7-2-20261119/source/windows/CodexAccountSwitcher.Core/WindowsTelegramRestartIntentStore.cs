using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// User's persistent request to consider resume after Windows restart.
/// This class can NEVER authorize Telegram delivery. It contains no account
/// credentials, Telegram tokens, Sender lease, or network operations.
/// </summary>
public enum WindowsTelegramRestartIntentStatus
{
    Missing,
    Enabled,
    Disabled,
    InvalidOrUnreadable
}

internal sealed record WindowsTelegramRestartIntent(
    int Version, bool Desired, DateTimeOffset ApprovedAtUtc)
{
    internal bool Valid =>
        Version == 1 && ApprovedAtUtc.Offset == TimeSpan.Zero &&
        ApprovedAtUtc.Year is >= 2020 and <= 2200;
}

internal sealed class WindowsTelegramRestartIntentStore
{
    private const string FileName = "telegram-restart-user-intent.v1.dpapi";
    private const int MaxBytes = 8192;
    private static readonly JsonSerializerOptions JsonSettings = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false
    };
    private readonly string directory;
    private readonly string file;
    private readonly object sync = new();

    internal WindowsTelegramRestartIntentStore(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        directory = Path.Combine(Path.GetFullPath(userDataDirectory),
            "telegram-restart-intent");
        file = Path.Combine(directory, FileName);
    }

    internal WindowsTelegramRestartIntentStatus Read()
    {
        lock (sync)
        {
            byte[]? encrypted = null;
            byte[]? plaintext = null;
            try
            {
                SecureFiles.RejectLinks(file);
                if (!File.Exists(file))
                    return WindowsTelegramRestartIntentStatus.Missing;
                var size = new FileInfo(file).Length;
                if (size is <= 0 or > MaxBytes)
                    return WindowsTelegramRestartIntentStatus.InvalidOrUnreadable;
                encrypted = File.ReadAllBytes(file);
                plaintext = WindowsCurrentUserDpapi.Unprotect(encrypted);
                var value = JsonSerializer.Deserialize<WindowsTelegramRestartIntent>(
                    plaintext, JsonSettings);
                if (value?.Valid != true)
                    return WindowsTelegramRestartIntentStatus.InvalidOrUnreadable;
                return value.Desired
                    ? WindowsTelegramRestartIntentStatus.Enabled
                    : WindowsTelegramRestartIntentStatus.Disabled;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or
                CryptographicException or JsonException or ArgumentException or
                NotSupportedException or Win32Exception or
                System.Security.SecurityException)
            {
                return WindowsTelegramRestartIntentStatus.InvalidOrUnreadable;
            }
            finally
            {
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
                if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }

    /// <summary>
    /// Called ONLY from an explicitly confirmed settings click. Enabled means
    /// "save user preference", NOT auto-arm or permission to send.
    /// OFF is a durable encrypted tombstone, not deletion.
    /// </summary>
    internal bool ChangeByUser(bool desired, DateTimeOffset now, bool explicitConfirmation)
    {
        if (!explicitConfirmation || now.Offset != TimeSpan.Zero)
            return false;
        lock (sync)
        {
            byte[]? plain = null;
            byte[]? encrypted = null;
            try
            {
                SecureFiles.RejectLinks(file);
                if (!Directory.Exists(directory))
                    SecureFiles.CreatePrivateDirectory(directory);
                var intent = new WindowsTelegramRestartIntent(1, desired, now);
                plain = JsonSerializer.SerializeToUtf8Bytes(intent, JsonSettings);
                encrypted = WindowsCurrentUserDpapi.Protect(plain);
                if (encrypted.Length is <= 0 or > MaxBytes) return false;
                SecureFiles.AtomicWrite(file, encrypted);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or
                CryptographicException or JsonException or ArgumentException or
                NotSupportedException or Win32Exception or
                System.Security.SecurityException)
            {
                return false;
            }
            finally
            {
                if (plain is not null) CryptographicOperations.ZeroMemory(plain);
                if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }
}
