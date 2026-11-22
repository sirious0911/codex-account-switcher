using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CodexAccountSwitcher.Core;
using CodexUsageMonitor.Core;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// Read-only scheduled reset observer. It does not own any Codex session,
/// thread binding, ledger reservation, Telegram sender or outbound transport.
/// All deadlines are derived from trusted active SwiftHost monitor observations.
/// </summary>
public sealed class DedicatedResetDeadlineObserver
{
    private sealed record Watch(
        Guid ProfileId,
        string ActiveEmailFingerprint,
        string SourceAccountHash,
        LimitLane Lane,
        DateTimeOffset ResetAtUtc,
        DateTimeOffset LastTrustedObservationUtc,
        int WindowDurationMinutes,
        bool OrdinaryUsageAllowed);

    private readonly Dictionary<LimitLane, Watch> watches = new();
    private readonly HashSet<string> emitted = new(StringComparer.Ordinal);

    public int ActiveWatchCount => watches.Count;
    public int CandidateCount => emitted.Count;

    public void Invalidate()
    {
        watches.Clear();
        // Past preview keys are not permissions to send. Retain in-process keys
        // to avoid duplicate observations on a profile switch back.
    }

    /// <summary>
    /// An unsupported/stale active sample cannot extend watch freshness.
    /// Its existence also must not falsely clear a legitimately fresh watch.
    /// </summary>
    public void Observe(
        CodexMonitorWireSample sample,
        AccountSnapshot state,
        ActiveMonitorPublicationGate gate,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(gate);

        if (!TryActiveProfile(state, gate, sample.ProfileID,
                out var profile, out var fingerprint))
        {
            Invalidate();
            return;
        }
        var observedUtc = ReadTimestamp(sample.ObservedAtMilliseconds);
        if (observedUtc is null || observedUtc > nowUtc.AddSeconds(30) ||
            nowUtc - observedUtc > TimeSpan.FromMinutes(6))
            return;

        UsageObservation data;
        try
        {
            if (!MonitorSyntheticPreview.TryRead(sample.RawJson, out _))
                return;
            data = SyntheticUsageNormalizer.Parse(sample.RawJson, observedUtc.Value);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or
            FormatException or ArgumentException or OverflowException)
        {
            return;
        }

        // The monitor's provider ID is a distinct proof from the Switcher's
        // verified email. Both must remain stable on a watched lane.
        if (string.IsNullOrWhiteSpace(data.AccountId) ||
            data.AccountId.Length > 512 || data.OrdinaryUsageAllowed == false)
        {
            Invalidate();
            return;
        }

        var sourceHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("codex-provider-account-v1\n" + data.AccountId)))
            .ToLowerInvariant();

        UpdateLane(LimitLane.FiveHour, data.FiveHour, 300, profile,
            fingerprint, sourceHash, observedUtc.Value, nowUtc);
        UpdateLane(LimitLane.SevenDay, data.Weekly, 10080, profile,
            fingerprint, sourceHash, observedUtc.Value, nowUtc);
    }

    /// <summary>
    /// Returns a NO-SEND preview of trusted due windows at their scheduled
    /// boundary. There is no RPC, file write, reservation or event listener.
    /// </summary>
    public IReadOnlyList<TrustedResetDeadline> TakeDue(
        AccountSnapshot currentState, ActiveMonitorPublicationGate gate,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(gate);

        var due = new List<TrustedResetDeadline>();
        foreach (var watch in watches.Values.ToArray())
        {
            if (!TryActiveProfile(currentState, gate, watch.ProfileId,
                    out _, out var fingerprint) ||
                fingerprint != watch.ActiveEmailFingerprint)
            {
                Invalidate();
                return [];
            }

            if (nowUtc < watch.ResetAtUtc ||
                nowUtc - watch.ResetAtUtc > TimeSpan.FromMinutes(10))
                continue;
            if (watch.LastTrustedObservationUtc > nowUtc.AddSeconds(30) ||
                nowUtc - watch.LastTrustedObservationUtc > TimeSpan.FromMinutes(6))
                continue;

            var key = string.Join("|", watch.ProfileId, watch.Lane,
                watch.SourceAccountHash,
                watch.ResetAtUtc.ToUnixTimeSeconds());
            if (!emitted.Add(key)) continue;
            due.Add(new TrustedResetDeadline(
                watch.ProfileId,
                watch.ActiveEmailFingerprint,
                watch.Lane,
                watch.ResetAtUtc,
                watch.LastTrustedObservationUtc,
                watch.WindowDurationMinutes,
                ActiveIdentityConfirmed: true,
                OrdinaryUsageAllowed: watch.OrdinaryUsageAllowed,
                TrustedSource: true));
        }
        return due;
    }

    private void UpdateLane(LimitLane lane, UsageWindow data, int duration,
        Guid profile, string fingerprint, string sourceHash,
        DateTimeOffset observedUtc, DateTimeOffset nowUtc)
    {
        if (data.DurationMinutes != duration ||
            data.ResetsAtUnix is not long resetSeconds ||
            data.UsedPercent is not double used ||
            !double.IsFinite(used) || used < 0 || used > 100 ||
            resetSeconds <= 0 || resetSeconds >= 253_402_300_799)
        {
            watches.Remove(lane);
            return;
        }

        DateTimeOffset resetUtc;
        try { resetUtc = DateTimeOffset.FromUnixTimeSeconds(resetSeconds); }
        catch (ArgumentOutOfRangeException) { watches.Remove(lane); return; }

        var horizon = lane == LimitLane.FiveHour
            ? TimeSpan.FromHours(6) : TimeSpan.FromDays(8);
        if (resetUtc <= observedUtc.AddMinutes(-10) ||
            resetUtc > observedUtc + horizon)
        {
            watches.Remove(lane);
            return;
        }

        var sameSource = watches.TryGetValue(lane, out var previous) &&
            previous!.ProfileId == profile &&
            previous.ActiveEmailFingerprint == fingerprint &&
            previous.SourceAccountHash == sourceHash;
        if (sameSource && previous is not null)
        {
            // Ignore stale and replayed monitor samples.
            if (observedUtc <= previous.LastTrustedObservationUtc) return;

            if (previous.ResetAtUtc == resetUtc)
            {
                watches[lane] = previous with {
                    LastTrustedObservationUtc = observedUtc
                };
                return;
            }

            // Preserve the old scheduled deadline only for a plausible
            // provider rollover around its boundary. Early deadline
            // corrections must replace the watch, not trigger the old one.
            var delta = resetUtc - previous.ResetAtUtc;
            var expected = TimeSpan.FromMinutes(duration);
            if (observedUtc >= previous.ResetAtUtc &&
                observedUtc <= previous.ResetAtUtc.AddMinutes(10) &&
                delta >= expected - TimeSpan.FromMinutes(10) &&
                delta <= expected + TimeSpan.FromMinutes(10))
            {
                watches[lane] = previous with {
                    LastTrustedObservationUtc = observedUtc
                };
                return;
            }
        }

        watches[lane] = new Watch(profile, fingerprint, sourceHash,
            lane, resetUtc, observedUtc, duration, OrdinaryUsageAllowed: true);
    }

    private static bool TryActiveProfile(AccountSnapshot state,
        ActiveMonitorPublicationGate gate, Guid? sampleProfile,
        out Guid profile, out string fingerprint)
    {
        profile = Guid.Empty;
        fingerprint = "";
        if (sampleProfile is not Guid id || id == Guid.Empty ||
            !state.ActiveIdentityConfirmed || state.IsMutating ||
            state.IsAddingAccount || !string.IsNullOrEmpty(state.Error) ||
            state.ActiveAccountID != id || gate.ActiveProfile != id)
            return false;
        var matching = state.Accounts.Where(a => a.Profile.Id == id).ToArray();
        if (matching.Length != 1 || string.IsNullOrWhiteSpace(matching[0].Profile.Email))
            return false;
        var email = matching[0].Profile.Email!.Trim().ToLowerInvariant();
        if (email.Count(c => c == '@') != 1 || email.Any(char.IsWhiteSpace) ||
            email.Any(char.IsControl) || email.Length > 254)
            return false;
        var parts = email.Split('@');
        if (parts[0].Length == 0 || !parts[1].Contains('.'))
            return false;
        profile = id;
        fingerprint = VerifiedExistingThreadPicker.FingerprintEmail(email);
        return true;
    }

    private static DateTimeOffset? ReadTimestamp(long milliseconds)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
