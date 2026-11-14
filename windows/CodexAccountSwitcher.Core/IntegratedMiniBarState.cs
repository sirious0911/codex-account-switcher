using System.Text.Json;

namespace CodexAccountSwitcher.Core;

// Read-only in-process display projection. It never fetches Codex data,
// persists account identity, or authorizes alerts. Only the existing verified
// active-profile gate may feed observations into this state.
public enum MiniBarDisplayStatus { Unknown, FreshUnbound, AgedUnbound, Invalidated }

public sealed record MiniBarReadout(
    MiniBarDisplayStatus Status, double? FiveHourRemaining, double? WeeklyRemaining,
    int? ResetCreditsAvailable, DateTimeOffset? ObservedAt,
    DateTimeOffset? FiveHourResetsAt = null,
    DateTimeOffset? WeeklyResetsAt = null,
    IReadOnlyList<DateTimeOffset?>? CreditExpiresAt = null)
{
    public bool AccountVerified => false;
    public bool EventsAllowed => false;
    public bool TelegramAllowed => false;
}

public sealed class IntegratedMiniBarState
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DisplayFor = TimeSpan.FromMinutes(6);
    private MonitorSyntheticPreview.Snapshot? snapshot;
    private DateTimeOffset? observedAt;
    private long? lastSourceMilliseconds;
    private string? sourceAccount;
    private long minimumSourceMilliseconds;
    private bool invalidated;
    // A mixed provider identity remains blocked for this active selection epoch.
    // Only an external active-profile gate transition can clear this latch.
    private bool mixedSource;

    public void Invalidate(DateTimeOffset now)
    {
        snapshot = null;
        observedAt = null;
        lastSourceMilliseconds = null;
        sourceAccount = null;
        minimumSourceMilliseconds = Math.Max(minimumSourceMilliseconds, now.ToUnixTimeMilliseconds());
        invalidated = true;
        mixedSource = false;
    }

    // Only an authenticated active-profile gate transition may reset the
    // source-identity latch. Bad samples merely clear the display; they
    // must not make the next provider identity look like a new epoch.
    private void RejectObservation(DateTimeOffset now)
    {
        snapshot = null;
        observedAt = null;
        lastSourceMilliseconds = null;
        minimumSourceMilliseconds = Math.Max(minimumSourceMilliseconds,
            now.ToUnixTimeMilliseconds());
        invalidated = true;
    }

    public bool TryApply(CodexMonitorWireSample sample, DateTimeOffset now)
    {
        if (mixedSource) return false;
        // Drop queued, duplicate and out-of-order frames WITHOUT invalidating
        // a later, already-accepted observation from the current active epoch.
        if (sample.ObservedAtMilliseconds <= minimumSourceMilliseconds ||
            (lastSourceMilliseconds is long last &&
             sample.ObservedAtMilliseconds <= last))
            return false;
        if (!MonitorSyntheticPreview.TryRead(sample.RawJson, out var parsed) || parsed == null ||
            (parsed.FiveHourRemaining is null &&
             parsed.WeeklyRemaining is null && parsed.CreditsRemaining is null))
        {
            RejectObservation(now);
            return false;
        }
        DateTimeOffset timestamp;
        try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(sample.ObservedAtMilliseconds); }
        catch (ArgumentOutOfRangeException)
        {
            RejectObservation(now);
            return false;
        }
        // Old queued frames and future timestamps cannot reinstate usage
        // after a switch, host restart, or display expiration.
        if (timestamp > now.AddSeconds(30) || now - timestamp >= DisplayFor)
        {
            RejectObservation(now);
            return false;
        }

        // Compare the source-provided identity only in volatile process memory.
        // Neither the identity nor its hash is shown or logged.
        string id;
        try
        {
            using var document = JsonDocument.Parse(sample.RawJson);
            id = document.RootElement.GetProperty("accountId").GetString()!;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            RejectObservation(now);
            return false;
        }
        if (sourceAccount is string existing &&
            !string.Equals(existing, id, StringComparison.Ordinal))
        {
            RejectObservation(now);
            mixedSource = true;
            return false;
        }
        sourceAccount = id;
        snapshot = parsed;
        observedAt = timestamp;
        lastSourceMilliseconds = sample.ObservedAtMilliseconds;
        invalidated = false;
        return true;
    }

    public MiniBarReadout Read(DateTimeOffset now)
    {
        if (snapshot is null || observedAt is null)
            return new(invalidated ? MiniBarDisplayStatus.Invalidated : MiniBarDisplayStatus.Unknown,
                null, null, null, null);
        var age = now - observedAt.Value;
        if (age < TimeSpan.Zero || age >= DisplayFor)
            return new(MiniBarDisplayStatus.Unknown, null, null, null, null);
        return new(age < FreshFor ? MiniBarDisplayStatus.FreshUnbound : MiniBarDisplayStatus.AgedUnbound,
            snapshot.FiveHourRemaining, snapshot.WeeklyRemaining,
            snapshot.CreditsRemaining, observedAt,
            snapshot.FiveHourResetsAt, snapshot.WeeklyResetsAt,
            snapshot.CreditExpiresAt);
    }
}
