using System.Security.Cryptography;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Verifies the two non-secret, reviewed sender scripts shipped inside the
/// bundled Swift runtime archive. The executable may display readiness, but
/// neither a matching hash nor a local owner mutex permits Telegram delivery.
/// </summary>
public static class WindowsPackagedTelegramScripts
{
    public const string MonitorScriptName = "send-fivehour100-existing-monitor-bot.ps1";
    public const string AlertScriptName = "send-weekly100-existing-alert-bot.ps1";
    public const string MonitorSha256 =
        "9370aa1a4194e65d10c999c8d75ec48fc62ff34a9ba4ee0aae7991d6d13d0920";
    public const string AlertSha256 =
        "449b2fd6ec108a08a86e7a291f400fb99e6d3f6764163e5d5e5f7a6adcec07b2";

    public static WindowsPackagedTelegramScriptInspection Inspect(string? hostExecutable)
    {
        if (string.IsNullOrWhiteSpace(hostExecutable) ||
            !Path.IsPathFullyQualified(hostExecutable))
            return new(false, false);
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(hostExecutable));
            if (directory is null || !Directory.Exists(directory))
                return new(false, false);

            // This checks *non-secret* script files only. Never open the bot
            // token, DPAPI payload, chat ID or account storage.
            var monitor = Matches(directory, MonitorScriptName, MonitorSha256);
            var alert = Matches(directory, AlertScriptName, AlertSha256);
            return new(monitor, alert);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new(false, false);
        }
    }

    private static bool Matches(string directory, string filename, string expectedHash)
    {
        try
        {
            var file = new FileInfo(Path.Combine(directory, filename));
            if (!file.Exists || file.Length is <= 0 or > 32768 ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
            using var input = new FileStream(file.FullName, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(SHA256.HashData(input))
                .ToLowerInvariant();
            return string.Equals(hash, expectedHash, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }
}

public sealed record WindowsPackagedTelegramScriptInspection(
    bool MonitorVerified, bool AlertVerified)
{
    public bool BothVerified => MonitorVerified && AlertVerified;
    public bool ActualTelegramSendAuthorized => false;
    public string Display =>
        "내장 Telegram 5H 묵음 스크립트: " +
        (MonitorVerified ? "SHA256 확인" : "미확인") +
        " | 7D 얼럿 스크립트: " +
        (AlertVerified ? "SHA256 확인" : "미확인") +
        " | 실제 발송: 차단";
}
