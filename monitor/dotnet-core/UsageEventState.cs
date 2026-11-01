using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageMonitor.Core;

public sealed record UsageEvent(
    string Key,
    string Kind,
    string Lane,
    double OldValue,
    double NewValue,
    DateTimeOffset ObservedAt);

public sealed record PendingEvent(UsageEvent Event, DateTimeOffset FirstObservedAt);

public sealed class UsageEventState
{
    public int SchemaVersion { get; set; } = 1;
    public UsageObservation? Trusted { get; set; }
    public List<PendingEvent> Pending { get; set; } = [];
    public HashSet<string> EmittedKeys { get; set; } = new(StringComparer.Ordinal);
    public List<UsageEvent> Outbox { get; set; } = [];
    public string? MockSenderOwner { get; set; }
    public List<MockDeliveryRecord> MockDeliveries { get; set; } = [];

    // A first observation is a baseline, never an alert.
    public IReadOnlyList<UsageEvent> Advance(UsageObservation next)
    {
        if (SchemaVersion != 1) throw new InvalidOperationException("Unsupported schema.");
        if (Identity(next) is null)
        {
            Trusted = null;
            Pending.Clear();
            return [];
        }

        if (Trusted is null || Identity(Trusted) != Identity(next))
        {
            Trusted = next;
            Pending.Clear();
            return [];
        }

        if (next.ObservedAt <= Trusted.ObservedAt) return [];
        if (next.ObservedAt - Trusted.ObservedAt > TimeSpan.FromHours(24))
        {
            Trusted = next;
            Pending.Clear();
            return [];
        }

        var proposals = Propose(Trusted, next).ToList();
        var pending = Pending.ToDictionary(p => p.Event.Key, StringComparer.Ordinal);
        var newPending = new List<PendingEvent>();
        var emitted = new List<UsageEvent>();

        foreach (var e in proposals)
        {
            if (pending.TryGetValue(e.Key, out var p) && next.ObservedAt > p.FirstObservedAt)
            {
                if (EmittedKeys.Add(e.Key))
                {
                    emitted.Add(e);
                    Outbox.Add(e);
                    MockDeliveries.Add(new MockDeliveryRecord
                    {
                        Key = e.Key, Status = "MOCK_PENDING"
                    });
                }
            }
            else
            {
                newPending.Add(new PendingEvent(e, next.ObservedAt));
            }
        }

        Pending = newPending;
        if (proposals.Count == 0 || emitted.Count > 0)
        {
            Trusted = next;
            if (emitted.Count > 0) Pending.Clear();
        }

        return emitted;
    }

    private static string? Identity(UsageObservation s) =>
        string.IsNullOrWhiteSpace(s.AccountId) || string.IsNullOrWhiteSpace(s.PlanType)
            ? null : s.AccountId + "|" + s.PlanType;

    private static IEnumerable<UsageEvent> Propose(UsageObservation before, UsageObservation after)
    {
        if (before.ResetCreditsAvailable is long oldCount &&
            after.ResetCreditsAvailable is long newCount && newCount > oldCount)
        {
            yield return Create(before, after, "CREDIT_INCREASE", "credits",
                oldCount, newCount, before.ObservedAt.ToUnixTimeMilliseconds().ToString());
        }

        if (before.OrdinaryUsageAllowed != true || after.OrdinaryUsageAllowed != true)
            yield break;

        foreach (var (lane, oldWindow, newWindow) in new[]
        {
            ("five", before.FiveHour, after.FiveHour),
            ("week", before.Weekly, after.Weekly)
        })
        {
            if (oldWindow.UsedPercent is not double oldUsed ||
                newWindow.UsedPercent is not double newUsed ||
                oldWindow.ResetsAtUnix is not long oldReset ||
                newWindow.ResetsAtUnix is not long newReset)
                continue;
            if (oldUsed < 15 || newUsed > 5 || oldUsed - newUsed < 15) continue;
            if (newReset <= oldReset + 120 || newReset <= after.ObservedAt.ToUnixTimeSeconds()) continue;
            var expected = DateTimeOffset.FromUnixTimeSeconds(oldReset);
            var early = after.ObservedAt < expected.AddMinutes(-2);
            if (early && before.ResetCreditsAvailable is long beforeCount &&
                after.ResetCreditsAvailable is long afterCount && afterCount < beforeCount)
                continue;
            var kind = early ? "EARLY_RECOVERY_SUSPECTED" : "SCHEDULED_RESET";
            yield return Create(before, after, kind, lane, oldUsed, newUsed,
                (oldReset * 1000).ToString() + ":" + (newReset * 1000).ToString());
        }
    }

    private static UsageEvent Create(
        UsageObservation before, UsageObservation after, string kind,
        string lane, double oldValue, double newValue, string anchor)
    {
        var raw = string.Join("|", before.AccountId, before.PlanType, kind,
            lane, anchor, oldValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            lane == "credits" ? newValue.ToString(System.Globalization.CultureInfo.InvariantCulture) : "reset");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return new UsageEvent(key, kind, lane, oldValue, newValue, after.ObservedAt);
    }
}

public static class MockTelegramFormatter
{
    // Formatting only. There is no network transport or sending method in this project.
    public static string Format(UsageEvent e) => e.Kind switch
    {
        "CREDIT_INCREASE" => FormattableString.Invariant($"[모의 알림] Codex 리셋권 증가 관측: {e.OldValue:0.##}개 → {e.NewValue:0.##}개 (2회 확인)"),
        "SCHEDULED_RESET" => FormattableString.Invariant($"[모의 알림] Codex {Lane(e.Lane)} 정기 재설정: 사용률 {e.OldValue:0.##}% → {e.NewValue:0.##}% (2회 확인)"),
        "EARLY_RECOVERY_SUSPECTED" => FormattableString.Invariant($"[모의 알림] Codex {Lane(e.Lane)} 비정기 복구 의심: 사용률 {e.OldValue:0.##}% → {e.NewValue:0.##}% (2회 확인)"),
        _ => throw new InvalidOperationException("Unknown event type.")
    };

    private static string Lane(string lane) => lane switch
    {
        "five" => "5시간",
        "week" => "주간",
        _ => "해당"
    };
}

public sealed class ExclusiveMockWriter : IDisposable
{
    private readonly string _lockPath;
    private readonly FileStream _stream;
    private bool _disposed;

    public ExclusiveMockWriter(string directory)
    {
        Directory.CreateDirectory(directory);
        _lockPath = Path.Combine(directory, ".writer.lock");
        _stream = new FileStream(_lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _stream.Dispose();
        File.Delete(_lockPath);
        _disposed = true;
    }
}

public static class MockStateStore
{
    public static UsageEventState Read(string directory)
    {
        var file = Path.Combine(directory, "state.json");
        if (!File.Exists(file)) return new UsageEventState();
        var state = JsonSerializer.Deserialize<UsageEventState>(File.ReadAllText(file));
        if (state is null || state.SchemaVersion != 1 || state.Pending is null ||
            state.EmittedKeys is null || state.Outbox is null || state.MockDeliveries is null)
            throw new InvalidDataException("Invalid saved state.");
        return state;
    }

    public static void Write(string directory, UsageEventState state)
    {
        if (state.SchemaVersion != 1) throw new InvalidDataException("Invalid state.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "state.json.tmp");
        var file = Path.Combine(directory, "state.json");

        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, file, overwrite: true);
        // Directory durability and crash recovery remain unverified on the Windows target.
    }
}
