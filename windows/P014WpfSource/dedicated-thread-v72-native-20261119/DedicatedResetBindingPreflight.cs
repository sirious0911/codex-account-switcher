using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// Receives a provider-observed reset deadline and only evaluates the
/// per-account dedicated thread binding already stored by an explicit user
/// selection. No Codex RPC and no disk writes, transport, or permission changes.
/// </summary>
public sealed class DedicatedResetBindingPreflight
{
    private readonly DedicatedThreadBindingStore bindings;

    public DedicatedResetBindingPreflight(string trustedDataHome)
    {
        if (string.IsNullOrWhiteSpace(trustedDataHome) ||
            !Path.IsPathFullyQualified(trustedDataHome))
            throw new ArgumentException("Absolute Switcher data root required",
                nameof(trustedDataHome));
        var root = Path.GetFullPath(trustedDataHome);
        bindings = new DedicatedThreadBindingStore(Path.Combine(root,
            "dedicated-existing-thread-bindings-v1.json"));
    }

    public DedicatedResetPreflightVerdict Evaluate(
        TrustedResetDeadline due,
        AccountSnapshot currentState,
        ActiveMonitorPublicationGate gate,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(due);
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(gate);

        if (due.ActiveProfileId == Guid.Empty ||
            currentState.ActiveAccountID != due.ActiveProfileId ||
            gate.ActiveProfile != due.ActiveProfileId ||
            !currentState.ActiveIdentityConfirmed ||
            currentState.IsMutating || currentState.IsAddingAccount ||
            !string.IsNullOrEmpty(currentState.Error))
            return new("ACTIVE_ACCOUNT_NOT_CONFIRMED");

        var rows = currentState.Accounts.Where(
            r => r.Profile.Id == due.ActiveProfileId).ToArray();
        if (rows.Length != 1 ||
            string.IsNullOrWhiteSpace(rows[0].Profile.Email))
            return new("PROFILE_IDENTITY_UNAVAILABLE");
        var email = rows[0].Profile.Email!.Trim().ToLowerInvariant();
        if (email.Length > 254 || email.Count(c => c == '@') != 1 ||
            email.Any(char.IsWhiteSpace) || email.Any(char.IsControl))
            return new("PROFILE_IDENTITY_UNAVAILABLE");
        var parts = email.Split('@');
        if (parts[0].Length == 0 || !parts[1].Contains('.'))
            return new("PROFILE_IDENTITY_UNAVAILABLE");

        var fingerprint = VerifiedExistingThreadPicker.FingerprintEmail(email);
        if (due.ActiveAccountSha256 != fingerprint ||
            !due.ActiveIdentityConfirmed ||
            !due.TrustedSource || !due.OrdinaryUsageAllowed)
            return new("RESET_OR_ACCOUNT_PROOF_INVALID");

        if (nowUtc < due.PreviousResetAtUtc ||
            nowUtc - due.PreviousResetAtUtc > TimeSpan.FromMinutes(10) ||
            nowUtc - due.LastTrustedObservationUtc > TimeSpan.FromMinutes(6) ||
            due.LastTrustedObservationUtc > nowUtc.AddSeconds(30) ||
            due.WindowDurationMinutes != (
                due.Lane == LimitLane.FiveHour ? 300 : 10080))
            return new("RESET_EVENT_NOT_FRESH_OR_INVALID");

        DedicatedChatSettings[] selected;
        try
        {
            selected = bindings.ReadAll()
                .Where(b => b.ProfileId == due.ActiveProfileId).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or System.Security.SecurityException)
        {
            return new("BINDING_UNAVAILABLE_OR_CORRUPT");
        }
        if (selected.Length != 1)
            return new(selected.Length == 0 ?
                "DEDICATED_THREAD_NOT_PINNED" : "AMBIGUOUS_PROFILE_BINDING");

        var binding = selected[0];
        if (binding.AccountSha256 != fingerprint ||
            !Guid.TryParseExact(binding.ExistingThreadId, "D", out _) ||
            binding.ThreadStorage is not ("desktop-default" or "switcher-profile") ||
            binding.Message != "안녕")
            return new("BINDING_ACCOUNT_THREAD_OR_TEXT_INVALID");

        // Bindings from the previous one-time picker are deliberately OFF.
        // This evaluation cannot enable them or invoke any RPC.
        if (!binding.AutomaticMessagesEnabled)
            return new("AUTOMATIC_SENDING_OFF", binding);
        if (due.Lane == LimitLane.FiveHour && !binding.StartOnFiveHour)
            return new("FIVE_HOUR_SENDING_OFF", binding);
        if (due.Lane == LimitLane.SevenDay && !binding.StartOnSevenDay)
            return new("SEVEN_DAY_SENDING_OFF", binding);

        // Even if a future UI explicitly enables all three flags, an
        // independent exclusive cross-device Sender proof, running account
        // account/read proof and an approved dispatch transport are absent.
        return new("EXCLUSIVE_SENDER_AND_DISPATCH_NOT_AUTHORIZED", binding);
    }
}

public sealed record DedicatedResetPreflightVerdict(
    string Status,
    DedicatedChatSettings? VerifiedBinding = null)
{
    public bool WillSend => false;
}
