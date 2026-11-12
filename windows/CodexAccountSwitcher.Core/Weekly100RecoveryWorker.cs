using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexUsageMonitor.Core;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Windows native companion to the pure Node weekly-100 detector.
/// It only prepares local MOCK_PENDING alerts. It never accesses credentials,
/// starts a process, sends Telegram messages, or changes the selected account.
/// </summary>
public sealed class Weekly100RecoveryWorker
{
    private readonly string directory;
    private readonly object sync = new();
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan MaximumGap = TimeSpan.FromHours(24);

    public Weekly100RecoveryWorker(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        directory = Path.Combine(userDataDirectory, "usage-monitor-weekly100");
    }

    public IReadOnlyList<Weekly100Alert> Process(
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
            reading.Weekly.DurationMinutes != 10080 ||
            reading.Weekly.UsedPercent is not double used ||
            reading.Weekly.ResetsAtUnix is not long resetSeconds ||
            resetSeconds <= observedAt.ToUnixTimeSeconds() ||
            resetSeconds > 253_402_300_799)
        {
            Invalidate(sample.ProfileID);
            return [];
        }

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
                state.Profiles[profile] = new Weekly100ProfileState
                {
                    AccountHash = accountHash, BaselineUsed = used,
                    BaselineResetAtMs = resetAtMs, LatestAtMs = observedMs
                };
                WriteState(state);
                return [];
            }

            existing.LatestAtMs = observedMs;
            var pending = existing.Pending;
            if (pending is not null && pending.ResetAtMs == resetAtMs &&
                used <= 5 && observedMs > pending.FirstObservedAtMs)
            {
                var key = TelegramQuotaEventIdentity.Build(
                    TelegramNotificationLane.WeeklyRecovery,
                    accountHash, pending.PreviousResetAtMs, resetAtMs);
                Weekly100Alert? created = null;
                if (state.EmittedKeys.Add(key))
                {
                    created = new Weekly100Alert(key, "WEEKLY_100_RECOVERED",
                        "week", 100, observedAt, DateTimeOffset.FromUnixTimeMilliseconds(resetAtMs),
                        "MOCK_PENDING");
                    state.Outbox.Add(created);
                }
                existing.BaselineUsed = used;
                existing.BaselineResetAtMs = resetAtMs;
                existing.Pending = null;
                WriteState(state);
                return created is null ? [] : [created];
            }

            if (pending is null && existing.BaselineUsed > 0 &&
                used == 0 && resetAtMs > existing.BaselineResetAtMs + 120_000)
            {
                existing.Pending = new Weekly100Pending
                {
                    PreviousResetAtMs = existing.BaselineResetAtMs,
                    ResetAtMs = resetAtMs, FirstObservedAtMs = observedMs
                };
                WriteState(state);
                return [];
            }

            existing.BaselineUsed = used;
            existing.BaselineResetAtMs = resetAtMs;
            existing.Pending = null;
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

    public IReadOnlyList<Weekly100Alert> ReadMockOutbox()
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
    public Weekly100Alert? ReserveMockDelivery(string key, string owner)
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

    private Weekly100DurableState ReadState()
    {
        var file = Path.Combine(directory, "state.json");
        if (!File.Exists(file)) return new();
        var state = JsonSerializer.Deserialize<Weekly100DurableState>(File.ReadAllText(file));
        if (state is null || state.SchemaVersion != 1 ||
            state.Profiles is null || state.EmittedKeys is null || state.Outbox is null ||
            state.Outbox.Any(e => !IsEventKey(e.Key) || e.Kind != "WEEKLY_100_RECOVERED" ||
                e.Lane != "week" || e.RemainingPercent != 100 ||
                e.Status is not ("MOCK_PENDING" or "MOCK_RESERVED" or
                    "MOCK_COMPLETE" or "MOCK_UNCERTAIN") ||
                (e.Status != "MOCK_PENDING" && e.MockOwner != "MOCK_WINDOWS")) ||
            state.Outbox.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() !=
                state.Outbox.Count)
            throw new InvalidDataException("Invalid weekly recovery ledger");
        return state;
    }

    private void WriteState(Weekly100DurableState state)
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

public sealed class Weekly100DurableState
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, Weekly100ProfileState> Profiles { get; set; } = [];
    public HashSet<string> EmittedKeys { get; set; } = new(StringComparer.Ordinal);
    public List<Weekly100Alert> Outbox { get; set; } = [];
}

public sealed class Weekly100ProfileState
{
    public string AccountHash { get; set; } = "";
    public double BaselineUsed { get; set; }
    public long BaselineResetAtMs { get; set; }
    public long LatestAtMs { get; set; }
    public Weekly100Pending? Pending { get; set; }
}

public sealed class Weekly100Pending
{
    public long PreviousResetAtMs { get; set; }
    public long ResetAtMs { get; set; }
    public long FirstObservedAtMs { get; set; }
}

public sealed record Weekly100Alert(
    string Key, string Kind, string Lane, int RemainingPercent,
    DateTimeOffset ObservedAt, DateTimeOffset ResetAt, string Status,
    string? MockOwner = null);
