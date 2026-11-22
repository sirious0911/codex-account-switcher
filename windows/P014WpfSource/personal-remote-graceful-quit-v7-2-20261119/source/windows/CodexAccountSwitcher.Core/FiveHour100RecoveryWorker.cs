using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexUsageMonitor.Core;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Windows native, mock-only detector for exact 5H quota recovery.
/// It only prepares local MOCK_PENDING alerts. It never accesses credentials,
/// starts a process, sends Telegram messages, or changes the selected account.
/// </summary>
public sealed class FiveHour100RecoveryWorker
{
    private readonly string directory;
    private readonly object sync = new();
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan MaximumGap = TimeSpan.FromHours(8);
    // The provider must advance to an actual next quota window, not merely
    // move the displayed deadline a few minutes. Allow small schedule jitter.
    private static readonly long ExpectedCycleMs = (long)TimeSpan.FromMinutes(300).TotalMilliseconds;
    private static readonly long CycleJitterMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

    public FiveHour100RecoveryWorker(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        directory = Path.Combine(userDataDirectory, "usage-monitor-fivehour100");
    }

    public IReadOnlyList<FiveHour100Alert> Process(
        CodexMonitorWireSample sample, AccountSnapshot snapshot,
        ActiveMonitorPublicationGate activeGate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(activeGate);

        if (!sample.ProfileID.HasValue || !activeGate.Allows(sample) ||
            snapshot.ActiveAccountID != sample.ProfileID ||
            !snapshot.ActiveIdentityConfirmed || snapshot.IsMutating ||
            snapshot.IsAddingAccount || !string.IsNullOrEmpty(snapshot.Error))
            return [];

        var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(sample.ObservedAtMilliseconds);
        if (now - observedAt > MaximumAge ||
            observedAt - now > TimeSpan.FromSeconds(30))
            return [];

        if (!MonitorSyntheticPreview.TryRead(sample.RawJson, out _))
            return [];

        UsageObservation reading;
        try { reading = SyntheticUsageNormalizer.Parse(sample.RawJson, observedAt); }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException)
        { return []; }

        // A null permission means "not provided" by the current Codex API.
        // Only a verified false permission excludes this observation.
        if (string.IsNullOrWhiteSpace(reading.AccountId) ||
            reading.OrdinaryUsageAllowed == false ||
            reading.FiveHour.DurationMinutes != 300 ||
            reading.FiveHour.UsedPercent is not double used ||
            reading.FiveHour.ResetsAtUnix is not long resetSeconds ||
            resetSeconds <= 0 || resetSeconds > 253_402_300_799)
        {
            Invalidate(sample.ProfileID);
            return [];
        }

        // The provider may still report yesterday's/this window's reset
        // timestamp for several minutes after the scheduled boundary.
        // Preserve the previous trusted baseline until a *new* next-reset
        // timestamp arrives, rather than erasing the only rollover proof.
        if (resetSeconds <= observedAt.ToUnixTimeSeconds())
            return [];

        var accountHash = HexHash(reading.AccountId);
        var resetAtMs = checked(resetSeconds * 1000);
        var profile = sample.ProfileID.Value.ToString("D");
        var observedMs = sample.ObservedAtMilliseconds;

        lock (sync)
        {
            Directory.CreateDirectory(directory);
            using var session = LockState();
            var state = ReadState();
            state.Profiles.TryGetValue(profile, out var existing);
            if (existing is not null && observedMs <= existing.LatestAtMs)
                return [];

            if (existing is null || existing.AccountHash != accountHash ||
                observedMs - existing.LatestAtMs > MaximumGap.TotalMilliseconds)
            {
                state.Profiles[profile] = new FiveHour100ProfileState
                {
                    AccountHash = accountHash, BaselineUsed = used,
                    BaselineResetAtMs = resetAtMs, LatestAtMs = observedMs
                };
                WriteState(state);
                return [];
            }

            var lastPreResetAtMs = existing.LatestAtMs;
            existing.LatestAtMs = observedMs;
            var oldResetAtMs = existing.BaselineResetAtMs;
            var newCycleMs = resetAtMs - oldResetAtMs;
            var inBoundaryWindow =
                observedMs >= oldResetAtMs - TimeSpan.FromMinutes(3).TotalMilliseconds &&
                observedMs <= oldResetAtMs + TimeSpan.FromMinutes(10).TotalMilliseconds;
            // Only a genuine provider rollover can qualify; previously used
            // quota is irrelevant (including an idle 100%-to-100% reset).
            var rollover = inBoundaryWindow &&
                newCycleMs >= ExpectedCycleMs - CycleJitterMs &&
                newCycleMs <= ExpectedCycleMs + CycleJitterMs;

            // The provider's next-reset timestamp can drift significantly
            // while an account is actively used. The existing general usage
            // monitor recognizes these by a sharp usage drop to <=5% after
            // the old boundary. Permit the same corroborated recovery without
            // accepting a mere schedule correction or an early credit reset.
            var highConfidenceRecovery =
                observedMs >= oldResetAtMs - TimeSpan.FromMinutes(2).TotalMilliseconds &&
                observedMs <= oldResetAtMs + TimeSpan.FromMinutes(35).TotalMilliseconds &&
                used <= 5 &&
                existing.BaselineUsed >= 15 &&
                existing.BaselineUsed - used >= 15 &&
                newCycleMs >= TimeSpan.FromMinutes(270).TotalMilliseconds &&
                newCycleMs <= TimeSpan.FromMinutes(345).TotalMilliseconds &&
                resetAtMs >= observedMs + TimeSpan.FromHours(3).TotalMilliseconds;

            // A provider-confirmed new window is the reset event. Polling may
            // observe 98% (or less) after Codex was already used; requiring an
            // exact 100% sample would silently lose a real reset.
            // A pending V5 rollover from an earlier 100%-only build is
            // eligible within its original ten-minute trusted window.
            var pending = existing.Pending;
            var deferredCycle = pending is not null &&
                pending.ResetAtMs == resetAtMs &&
                observedMs > pending.FirstObservedAtMs &&
                observedMs <= pending.PreviousResetAtMs +
                    TimeSpan.FromMinutes(10).TotalMilliseconds;
            if (rollover || highConfidenceRecovery || deferredCycle)
            {
                var beforeReset = deferredCycle ? pending!.PreviousResetAtMs : oldResetAtMs;
                var key = TelegramQuotaEventIdentity.Build(
                    TelegramNotificationLane.FiveHourRecovery,
                    accountHash, beforeReset, resetAtMs);
                // The previous trusted sample belongs to the immediately
                // ending window. A 100%-used sample is not evidence for an
                // urgent 0%-remaining alert unless it was observed shortly
                // BEFORE that exact boundary and the new provider window is
                // confirmed at/after the boundary with the same identity.
                // Deferred rollover from older schema carries no such proof.
                var lastPreResetAt = lastPreResetAtMs;
                var preResetTrusted = !deferredCycle &&
                    lastPreResetAt > 0 &&
                    lastPreResetAt < oldResetAtMs &&
                    oldResetAtMs - lastPreResetAt <= TimeSpan.FromMinutes(15).TotalMilliseconds &&
                    observedMs >= oldResetAtMs &&
                    newCycleMs >= TimeSpan.FromMinutes(270).TotalMilliseconds &&
                    newCycleMs <= TimeSpan.FromMinutes(345).TotalMilliseconds;
                var evidenceRemaining = preResetTrusted ? 100 - existing.BaselineUsed : (double?)null;
                var evidenceObservedAt = preResetTrusted
                    ? DateTimeOffset.FromUnixTimeMilliseconds(lastPreResetAt)
                    : (DateTimeOffset?)null;
                var evidenceWindowEnd = preResetTrusted
                    ? DateTimeOffset.FromUnixTimeMilliseconds(oldResetAtMs)
                    : (DateTimeOffset?)null;
                FiveHour100Alert? created = null;
                if (state.EmittedKeys.Add(key))
                {
                    // RemainingPercent=100 is a legacy reset-event discriminator,
                    // NOT the observed balance. Preserve durable ledger and bot
                    // routing compatibility; report observed quota separately.
                    created = new FiveHour100Alert(key, "FIVE_HOUR_100_RECOVERED",
                        "fiveHour", 100,
                        observedAt, DateTimeOffset.FromUnixTimeMilliseconds(resetAtMs),
                        "MOCK_PENDING", ObservedRemainingPercent: 100 - used,
                        PreResetRemainingPercent: evidenceRemaining,
                        PreResetObservedAt: evidenceObservedAt,
                        PreResetWindowEndsAt: evidenceWindowEnd);
                    state.Outbox.Add(created);
                }
                existing.BaselineUsed = used;
                existing.BaselineResetAtMs = resetAtMs;
                existing.Pending = null;
                WriteState(state);
                return created is null ? [] : [created];
            }

            // A minor deadline correction is not an entire new quota window.
            // Do not create a notification solely from usage dropping.
            existing.Pending = null;
            existing.BaselineUsed = used;
            existing.BaselineResetAtMs = resetAtMs;
            WriteState(state);
            return [];
        }
    }

    /// <summary>
    /// Discard a previous active profile's pending baseline when a switch,
    /// host failure or source-identity change invalidates its gate.
    /// Already recorded event keys are retained for deduplication.
    /// </summary>
    public void Invalidate(Guid? profileID)
    {
        if (!profileID.HasValue || !Directory.Exists(directory)) return;
        lock (sync)
        {
            using var session = LockState();
            var state = ReadState();
            if (state.Profiles.Remove(profileID.Value.ToString("D")))
                WriteState(state);
        }
    }

    public IReadOnlyList<FiveHour100Alert> ReadMockOutbox()
    {
        if (!Directory.Exists(directory)) return [];
        lock (sync)
        {
            using var session = LockState();
            return ReadState().Outbox.ToArray();
        }
    }

    // Reserve and persist exactly one synthetic delivery attempt before any
    // transport can be invoked. A crash after reservation must not auto-retry.
    // Actual Telegram delivery is not wired to this worker.
    public FiveHour100Alert? ReserveMockDelivery(string key, string owner)
    {
        if (owner != "MOCK_WINDOWS" || !IsEventKey(key) || !Directory.Exists(directory))
            return null;
        lock (sync)
        {
            using var session = LockState();
            var state = ReadState();
            var index = state.Outbox.FindIndex(e => e.Key == key);
            if (index < 0 || state.Outbox[index].Status != "MOCK_PENDING")
                return null;
            var reserved = state.Outbox[index] with {
                Status = "MOCK_RESERVED", MockOwner = owner
            };
            state.Outbox[index] = reserved;
            WriteState(state);
            return reserved;
        }
    }

    public bool CompleteMockDelivery(string key, string owner) =>
        TransitionMockDelivery(key, owner, "MOCK_COMPLETE");

    public bool MarkMockUncertain(string key, string owner) =>
        TransitionMockDelivery(key, owner, "MOCK_UNCERTAIN");

    private bool TransitionMockDelivery(string key, string owner, string status)
    {
        if (owner != "MOCK_WINDOWS" || !IsEventKey(key) || !Directory.Exists(directory))
            return false;
        lock (sync)
        {
            using var session = LockState();
            var state = ReadState();
            var index = state.Outbox.FindIndex(e => e.Key == key);
            if (index < 0 || state.Outbox[index].Status != "MOCK_RESERVED" ||
                state.Outbox[index].MockOwner != owner)
                return false;
            state.Outbox[index] = state.Outbox[index] with { Status = status };
            WriteState(state);
            return true;
        }
    }

    private static bool IsEventKey(string? key) => key?.Length == 64 &&
        key.All(char.IsAsciiHexDigit);

    private FileStream LockState() => new(
        Path.Combine(directory, ".processing.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private FiveHour100DurableState ReadState()
    {
        var file = Path.Combine(directory, "state.json");
        if (!File.Exists(file)) return new();
        var state = JsonSerializer.Deserialize<FiveHour100DurableState>(File.ReadAllText(file));
        if (state is null || state.SchemaVersion != 1 ||
            state.Profiles is null || state.EmittedKeys is null || state.Outbox is null ||
            state.Outbox.Any(e => !IsEventKey(e.Key) || e.Kind != "FIVE_HOUR_100_RECOVERED" ||
                e.Lane != "fiveHour" || e.RemainingPercent != 100 ||
                (e.ObservedRemainingPercent is double remaining &&
                    (!double.IsFinite(remaining) || remaining < 0 || remaining > 100)) ||
                !FiveHourPreResetEvidence.IsValidOrMissing(e) ||
                e.Status is not ("MOCK_PENDING" or "MOCK_RESERVED" or
                    "MOCK_COMPLETE" or "MOCK_UNCERTAIN") ||
                (e.Status != "MOCK_PENDING" && e.MockOwner != "MOCK_WINDOWS")) ||
            state.Outbox.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() !=
                state.Outbox.Count)
            throw new InvalidDataException("Invalid five-hour recovery ledger");
        return state;
    }

    private void WriteState(FiveHour100DurableState state)
    {
        // Local-only mock ledger; this does not acknowledge a Telegram delivery.
        var temp = Path.Combine(directory, "state.json.tmp");
        var target = Path.Combine(directory, "state.json");
        if (File.Exists(temp)) File.Delete(temp);
        using (var stream = new FileStream(temp, FileMode.CreateNew,
                   FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, target, overwrite: true);
    }

    private static string HexHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

public sealed class FiveHour100DurableState
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, FiveHour100ProfileState> Profiles { get; set; } = [];
    public HashSet<string> EmittedKeys { get; set; } = new(StringComparer.Ordinal);
    public List<FiveHour100Alert> Outbox { get; set; } = [];
}

public sealed class FiveHour100ProfileState
{
    public string AccountHash { get; set; } = "";
    public double BaselineUsed { get; set; }
    public long BaselineResetAtMs { get; set; }
    public long LatestAtMs { get; set; }
    public FiveHour100Pending? Pending { get; set; }
}

public sealed class FiveHour100Pending
{
    public long PreviousResetAtMs { get; set; }
    public long ResetAtMs { get; set; }
    public long FirstObservedAtMs { get; set; }
}

public sealed record FiveHour100Alert(
    string Key, string Kind, string Lane, int RemainingPercent,
    DateTimeOffset ObservedAt, DateTimeOffset ResetAt, string Status,
    string? MockOwner = null,
    // Actual remaining quota when the reset was observed. Null for
    // older persisted events; legacy RemainingPercent=100 identifies
    // reset-confirmation event and is NOT the observed quota balance.
    double? ObservedRemainingPercent = null,
    // Nullable on old event ledgers or when the previous sample was stale.
    // The three fields are always stored together after the provider has
    // rolled over; none is a permission to send a Telegram message.
    double? PreResetRemainingPercent = null,
    DateTimeOffset? PreResetObservedAt = null,
    DateTimeOffset? PreResetWindowEndsAt = null);
