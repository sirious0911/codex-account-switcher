using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Canonical event deduplication key for a future shared Windows/macOS sender.
/// Event keys contain no raw account identity, bot token, email or chat ID.
/// Compatible with the existing Windows and Node weekly/five-hour event format.
/// </summary>
public static class TelegramQuotaEventIdentity
{
    public const int SchemaVersion = 1;

    public static string Build(
        TelegramNotificationLane lane, string accountIdSha256,
        long previousResetAtUnixMs, long currentResetAtUnixMs)
    {
        var kind=lane switch
        {
            TelegramNotificationLane.FiveHourRecovery => "FIVE_HOUR_100",
            TelegramNotificationLane.WeeklyRecovery => "WEEKLY_100",
            _ => throw new ArgumentOutOfRangeException(nameof(lane))
        };
        if (accountIdSha256?.Length!=64 ||
            !accountIdSha256.All(char.IsAsciiHexDigit))
            throw new ArgumentException(
                "Account identity must be an already-hashed SHA256 digest",
                nameof(accountIdSha256));
        if (previousResetAtUnixMs<=0 ||
            currentResetAtUnixMs<=previousResetAtUnixMs ||
            currentResetAtUnixMs>253402300799000L)
            throw new ArgumentOutOfRangeException(nameof(currentResetAtUnixMs));

        // Explicit invariant formatting keeps the shared fingerprint stable
        // across Windows/macOS system languages and date/number locales.
        var input=string.Join("|",kind,
            accountIdSha256.ToLowerInvariant(),
            previousResetAtUnixMs.ToString(CultureInfo.InvariantCulture),
            currentResetAtUnixMs.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant();
    }
}
