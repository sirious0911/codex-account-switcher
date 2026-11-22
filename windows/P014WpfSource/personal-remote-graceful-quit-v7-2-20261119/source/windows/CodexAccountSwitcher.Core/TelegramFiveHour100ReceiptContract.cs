using System.Globalization;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Strict parser for a FUTURE dedicated 5H Monitor Bot sender's safe stdout
/// acknowledgement. The legacy Amano script does not implement this receipt.
/// This contract alone does NOT attest that a process made a real API call.
/// </summary>
public static class TelegramFiveHour100ReceiptContract
{
    private const string Prefix = "CODEX_FIVEHOUR100_TELEGRAM_ACK_V1";

    // A stdout receipt is valid only with one exact, typed record, no stderr,
    // exit code 0 and the matching locally reserved event key. Never store or
    // display arbitrary stdout/stderr or the token-bearing URI on failure.
    public static bool TryParse(
        WindowsAlertBotDryRunResult? result,
        string? expectedEventKey,
        out long telegramMessageID)
    {
        telegramMessageID = 0;
        if (result is null || result.ExitCode != 0 ||
            !string.IsNullOrEmpty(result.StandardError) ||
            !IsCanonicalEventKey(expectedEventKey))
            return false;

        var stdout = result.StandardOutput;
        if (string.IsNullOrEmpty(stdout) || stdout.Length > 160 ||
            stdout.Contains('\0'))
            return false;
        // Windows PowerShell Write-Output adds one terminal line ending.
        // Permit that single terminator but never an extra output line.
        if (stdout.EndsWith("\r\n", StringComparison.Ordinal))
            stdout = stdout[..^2];
        else if (stdout.EndsWith('\n'))
            stdout = stdout[..^1];
        if (stdout.Contains('\r') || stdout.Contains('\n'))
            return false;

        var prefix = Prefix + "|";
        if (!stdout.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var fields = stdout.Split('|');
        if (fields.Length != 3 || fields[0] != Prefix ||
            fields[1] != expectedEventKey ||
            fields[2].Length is < 1 or > 19 ||
            fields[2][0] == '0' ||
            fields[2].Any(ch => ch is < '0' or > '9') ||
            !long.TryParse(fields[2], NumberStyles.None,
                CultureInfo.InvariantCulture, out var id) || id <= 0)
            return false;

        telegramMessageID = id;
        return true;
    }

    public static string FormatForFakeTest(string eventKey, long messageID)
    {
        if (!IsCanonicalEventKey(eventKey) || messageID <= 0)
            throw new ArgumentException("Invalid synthetic Telegram receipt");
        return Prefix + "|" + eventKey + "|" +
               messageID.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsCanonicalEventKey(string? key) =>
        key?.Length == 64 &&
        key.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}
