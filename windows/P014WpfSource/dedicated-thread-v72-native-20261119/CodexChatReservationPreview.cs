using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// The exact canonical request identity expected by the separately isolated
/// Codex chat SQLite authority. This is a PREVIEW ONLY. No auth token,
/// HTTP client, RPC sender, or message transport is constructed or invoked.
/// </summary>
public sealed record CodexChatReservationPreview(
    string AccountSha256,
    string ExistingThreadId,
    string Lane,
    long ResetAtUnix,
    string EventKey,
    string Endpoint = "/v1/codex-chat/events/reserve")
{
    public bool RealMessageSendAuthorized => false;
}

public static class CodexChatReservationPlanner
{
    private const string Domain = "CODEX_CHAT_RESET_EVENT_V1";

    public static CodexChatReservationPreview? TryCreate(
        TrustedResetDeadline due,
        DedicatedResetPreflightVerdict preflight,
        DateTimeOffset nowUtc)
    {
        if (due is null || preflight is null)
            return null;
        // A candidate may be previewed with automatic sending OFF. Even
        // forced ON is NOT an authorization; source always returns false.
        if (preflight.Status is not (
            "AUTOMATIC_SENDING_OFF" or
            "FIVE_HOUR_SENDING_OFF" or
            "SEVEN_DAY_SENDING_OFF" or
            "EXCLUSIVE_SENDER_AND_DISPATCH_NOT_AUTHORIZED"))
            return null;

        var binding = preflight.VerifiedBinding;
        if (binding is null || !due.ActiveIdentityConfirmed ||
            !due.TrustedSource || !due.OrdinaryUsageAllowed ||
            due.ActiveProfileId != binding.ProfileId ||
            due.ActiveAccountSha256 != binding.AccountSha256 ||
            binding.Message != "안녕" ||
            !Regex.IsMatch(binding.AccountSha256, "^[0-9a-f]{64}$") ||
            !Guid.TryParseExact(binding.ExistingThreadId, "D", out _) ||
            binding.ExistingThreadId != binding.ExistingThreadId.ToLowerInvariant())
            return null;

        var lane = due.Lane switch
        {
            LimitLane.FiveHour => "CODEX_FIVE_HOUR",
            LimitLane.SevenDay => "CODEX_SEVEN_DAY",
            _ => null
        };
        if (lane is null || due.WindowDurationMinutes != (
            due.Lane == LimitLane.FiveHour ? 300 : 10080) ||
            nowUtc < due.PreviousResetAtUtc ||
            nowUtc - due.PreviousResetAtUtc > TimeSpan.FromMinutes(10) ||
            nowUtc - due.LastTrustedObservationUtc > TimeSpan.FromMinutes(6) ||
            due.LastTrustedObservationUtc > nowUtc.AddSeconds(30))
            return null;

        var timestamp = due.PreviousResetAtUtc.ToUnixTimeSeconds();
        if (timestamp <= 0) return null;

        var raw = string.Join("\n",
            Domain, binding.AccountSha256, binding.ExistingThreadId,
            lane, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "");
        var key = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return new(binding.AccountSha256, binding.ExistingThreadId,
            lane, timestamp, key);
    }
}
