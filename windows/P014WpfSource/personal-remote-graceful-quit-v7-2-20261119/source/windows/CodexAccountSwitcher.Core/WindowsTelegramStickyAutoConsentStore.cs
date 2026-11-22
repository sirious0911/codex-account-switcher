using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Explicit, Windows-only, sole-sender user consent across process restarts.
/// This is not remote ownership evidence. It never starts a Telegram process.
/// Only a WPF-confirmed initial enable may write an ON record.
/// </summary>
internal sealed record WindowsTelegramStickyAutoConsent(
    int Version,
    bool Enabled,
    string DeviceBinding,
    string BotDirectoryBinding,
    bool FiveHour,
    bool Weekly,
    DateTimeOffset WrittenAtUtc)
{
    internal bool IsWellFormed =>
        Version == 1 &&
        WindowsTelegramStickyAutoConsentStore.IsHash(DeviceBinding) &&
        WindowsTelegramStickyAutoConsentStore.IsHash(BotDirectoryBinding) &&
        WrittenAtUtc.Offset == TimeSpan.Zero &&
        WrittenAtUtc.Year is >= 2020 and <= 2200 &&
        (!Enabled || FiveHour || Weekly);
}

internal sealed class WindowsTelegramStickyAutoConsentStore
{
    private const string FileName = "windows-only-auto-telegram-consent.v1.dpapi";
    private const int MaxFileBytes = 8192;
    private readonly string directory;
    private readonly string file;
    private readonly string deviceBinding;
    private readonly object sync = new();

    private static readonly JsonSerializerOptions JsonSettings = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false
    };

    internal WindowsTelegramStickyAutoConsentStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var root = Path.GetFullPath(dataDirectory);
        directory = Path.Combine(root, "telegram-sticky-local-consent");
        file = Path.Combine(directory, FileName);
        var userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new CryptographicException("Windows user identity unavailable");
        deviceBinding = Hash("WINDOWS_STICKY_SENDER_V1|" +
            userSid + "|" + Environment.MachineName.ToUpperInvariant() +
            "|" + root.ToUpperInvariant());
    }

    internal static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    private static string BotHash(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return Hash("");
        return Hash(Path.GetFullPath(directory).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant());
    }

    internal bool IsEnabledFor(
        TelegramNotificationPreferences? preferences, string? botDirectory)
    {
        var current = preferences?.Normalize();
        if (current is null || !(current.WantsFiveHour || current.WantsWeekly))
            return false;
        var record = Read();
        if (record is not { Enabled: true } ||
            record.DeviceBinding != deviceBinding ||
            record.FiveHour != current.WantsFiveHour ||
            record.Weekly != current.WantsWeekly)
            return false;
        try { return record.BotDirectoryBinding == BotHash(botDirectory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException) { return false; }
    }

    private WindowsTelegramStickyAutoConsent? Read()
    {
        lock (sync)
        {
            byte[]? encrypted = null;
            byte[]? plaintext = null;
            try
            {
                SecureFiles.RejectLinks(file);
                if (!File.Exists(file)) return null;
                var length = new FileInfo(file).Length;
                if (length is <= 0 or > MaxFileBytes) return null;
                encrypted = File.ReadAllBytes(file);
                plaintext = WindowsCurrentUserDpapi.Unprotect(encrypted);
                var record = JsonSerializer.Deserialize<WindowsTelegramStickyAutoConsent>(
                    plaintext, JsonSettings);
                return record?.IsWellFormed == true ? record : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or
                CryptographicException or JsonException or ArgumentException or
                Win32Exception or NotSupportedException or
                System.Security.SecurityException)
            {
                return null;
            }
            finally
            {
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
                if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }

    internal bool SaveFromExplicitUserAction(
        bool enabled,
        TelegramNotificationPreferences? preferences,
        string? botDirectory,
        DateTimeOffset nowUtc,
        bool confirmedOnlyWindowsSender)
    {
        if (nowUtc.Offset != TimeSpan.Zero ||
            (enabled && !confirmedOnlyWindowsSender))
            return false;
        var current = preferences?.Normalize();
        if (enabled && (current is null ||
            !(current.WantsFiveHour || current.WantsWeekly)))
            return false;
        string botBinding;
        try { botBinding = BotHash(botDirectory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException) { return false; }

        var record = new WindowsTelegramStickyAutoConsent(
            1, enabled, deviceBinding, botBinding,
            enabled && current?.WantsFiveHour == true,
            enabled && current?.WantsWeekly == true,
            nowUtc);
        byte[]? plaintext = null;
        byte[]? encrypted = null;
        lock (sync)
        {
            try
            {
                SecureFiles.RejectLinks(file);
                if (!Directory.Exists(directory))
                    SecureFiles.CreatePrivateDirectory(directory);
                plaintext = JsonSerializer.SerializeToUtf8Bytes(record, JsonSettings);
                encrypted = WindowsCurrentUserDpapi.Protect(plaintext);
                if (encrypted.Length is <= 0 or > MaxFileBytes) return false;
                SecureFiles.AtomicWrite(file, encrypted);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or
                CryptographicException or JsonException or ArgumentException or
                Win32Exception or NotSupportedException or
                System.Security.SecurityException)
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
