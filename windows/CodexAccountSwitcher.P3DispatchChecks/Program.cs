using System.Text.Json;
using CodexAccountSwitcher.Core;

// These checks use a synthetic profile and an injected in-memory sink.
// They do not instantiate an operating app, access Codex credentials,
// establish an outside connection, or trigger Telegram.
var passed = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new InvalidOperationException("P3 dispatch check failed: " + name);
    passed++;
}

var now = DateTimeOffset.UtcNow;
var profileID = Guid.Parse("12345678-0000-4000-8000-000000000001");
var anotherID = Guid.Parse("12345678-0000-4000-8000-000000000002");
var row = new AccountRow(
    new Profile(profileID, "Synthetic Windows", null),
    "S", new Usage(62, now.AddDays(2), 85, now.AddHours(1)), null);
AccountSnapshot State(bool confirmed = true, bool mutating = false) =>
    new([row], profileID, new Preferences(), mutating, false,
        confirmed, null, []);
CodexMonitorWireSample Sample(long offsetMs = 1000, Guid? selected = null) =>
    new(
        now.AddMilliseconds(offsetMs).ToUnixTimeMilliseconds(),
        JsonSerializer.Serialize(new
        {
            accountId = "synthetic-windows-account",
            rateLimitsByLimitId = new
            {
                codex = new
                {
                    primary = new
                    {
                        windowDurationMins = 300, usedPercent = 15,
                        resetsAt = now.AddHours(1).ToUnixTimeSeconds()
                    },
                    secondary = new
                    {
                        windowDurationMins = 10080, usedPercent = 38,
                        resetsAt = now.AddDays(2).ToUnixTimeSeconds()
                    }
                }
            }
        }),
        selected ?? profileID
    );

// 1) Only an explicitly constructed coordinator with a valid active snapshot
// can deliver telemetry. Duplicate observations are consumed.
var delivered = new List<string>();
using (var coordinator = new VerifiedQuotaDispatchCoordinator(
    "windows-b", (json, _) =>
    {
        delivered.Add(json);
        return Task.FromResult(true);
    }))
{
    Check(!await coordinator.DispatchAsync(Sample(), now.AddSeconds(2)),
        "no verified snapshot -> no dispatch");
    coordinator.ObserveSnapshot(State(), now);
    Check(!await coordinator.DispatchAsync(Sample(selected: anotherID),
        now.AddSeconds(2)), "another local profile blocked");
    Check(await coordinator.DispatchAsync(Sample(), now.AddSeconds(2)),
        "verified active sample delivered");
    Check(delivered.Count == 1, "exactly one callback invocation");
    Check(!await coordinator.DispatchAsync(Sample(), now.AddSeconds(2)),
        "identical sample is consumed only once");

    using var doc = JsonDocument.Parse(delivered[0]);
    var root = doc.RootElement;
    Check(root.GetProperty("sourceDeviceId").GetString() == "windows-b" &&
          root.GetProperty("fiveHour").GetProperty("remainingPercent").GetInt32() == 85 &&
          root.GetProperty("sevenDay").GetProperty("remainingPercent").GetInt32() == 62,
          "strict V1 5H/7D values preserved");
    Check(root.GetProperty("actualSendAuthorized").ValueKind ==
          JsonValueKind.False &&
          root.GetProperty("senderTransitionAuthorized").ValueKind ==
          JsonValueKind.False &&
          !delivered[0].Contains("synthetic-windows-account",
              StringComparison.Ordinal),
          "no raw account ID or privileged flags");
    coordinator.ObserveSnapshot(State(confirmed:false), now.AddSeconds(3));
    Check(!await coordinator.DispatchAsync(Sample(4000),
        now.AddSeconds(5)), "loss of active identity stops dispatch");
}

// 2) Failed transport must not cause retries or replay of the same frame.
var failures=0;
using (var coordinator = new VerifiedQuotaDispatchCoordinator(
    "windows-b", (_, _) =>
    {
        failures++;
        return Task.FromResult(false);
    }))
{
    coordinator.ObserveSnapshot(State(), now);
    Check(!await coordinator.DispatchAsync(Sample(),now.AddSeconds(2)),
        "explicit unsuccessful sink remains false");
    Check(!await coordinator.DispatchAsync(Sample(),now.AddSeconds(2))
          && failures == 1, "no automatic retry after a failed sink");
}

// 3) An in-flight submission is canceled when the user switches accounts.
var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using (var coordinator = new VerifiedQuotaDispatchCoordinator(
    "windows-b", async (_, cancellation) =>
    {
        begun.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        return true;
    }))
{
    coordinator.ObserveSnapshot(State(), now);
    var pending = coordinator.DispatchAsync(Sample(),now.AddSeconds(2));
    await begun.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(!await coordinator.DispatchAsync(Sample(2000),now.AddSeconds(3)),
        "in-flight competing request blocked");
    coordinator.SwitchRequested(now.AddSeconds(4));
    Check(!await pending.WaitAsync(TimeSpan.FromSeconds(5)),
        "switch cancels in-flight submission");
    Check(!await coordinator.DispatchAsync(Sample(5000),now.AddSeconds(6)),
        "pending switch prevents new submissions");
}

// 4) Identity invalidation cancels an outstanding asynchronous submission.
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using (var coordinator = new VerifiedQuotaDispatchCoordinator(
    "windows-b", async (_, cancellation) =>
    {
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        return true;
    }))
{
    coordinator.ObserveSnapshot(State(),now);
    var pending=coordinator.DispatchAsync(Sample(),now.AddSeconds(2));
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    coordinator.ObserveSnapshot(State(confirmed:false),now.AddSeconds(3));
    Check(!await pending.WaitAsync(TimeSpan.FromSeconds(5)),
        "active identity loss cancels network attempt");
}

// 5) No allowed sample is sent after disposed or during mutation.
using (var coordinator = new VerifiedQuotaDispatchCoordinator(
    "windows-b", (_, _) => Task.FromResult(true)))
{
    coordinator.ObserveSnapshot(State(mutating:true),now);
    Check(!await coordinator.DispatchAsync(Sample(),now.AddSeconds(2)),
        "host mutation rejects outbound view");
    coordinator.Dispose();
    Check(!await coordinator.DispatchAsync(Sample(3000),now.AddSeconds(4)),
        "disposed coordinator is permanently closed");
}
// 6) Wire the exact existing event shapes without creating a SwiftHost.
var linked = 0;
var fake = new FakeQuotaEvents(State());
using (var coordinator = new VerifiedQuotaDispatchCoordinator(
    "windows-b", (_, _) =>
    {
        linked++;
        return Task.FromResult(true);
    }))
using (var bridge = new VerifiedQuotaRuntimeBridge(fake, coordinator))
{
    fake.EmitObservation(Sample(3000));
    Check(linked == 1, "CoreClient-compatible event emits one verified frame");
    fake.EmitObservation(Sample(3000));
    Check(linked == 1, "duplicate event does not send again");
    fake.Change(State(confirmed: false));
    fake.EmitObservation(Sample(4000));
    Check(linked == 1, "identity change closes event subscription gate");
    fake.EmitSwitch();
    fake.EmitObservation(Sample(5000));
    Check(linked == 1, "pre-switch event closes outgoing path");
}
fake.EmitObservation(Sample(6000));
Check(linked == 1, "disposing bridge detaches all existing handlers");

Console.WriteLine($"P3_VERIFIED_QUOTA_DISPATCH_PASS checks={passed}");
Console.WriteLine("REAL_CODEX_READS=0 ACTUAL_TELEGRAM_SENDS=0 NETWORK_CALLS=0");

internal sealed class FakeQuotaEvents(AccountSnapshot initial) : IVerifiedQuotaEventSource
{
    public AccountSnapshot State { get; private set; } = initial;
    public event Action? Changed;
    public event Action<CodexMonitorWireSample>? MonitorObserved;
    public event Action? SwitchRequested;
    public void Change(AccountSnapshot next)
    {
        State = next;
        Changed?.Invoke();
    }
    public void EmitObservation(CodexMonitorWireSample sample) =>
        MonitorObserved?.Invoke(sample);
    public void EmitSwitch() => SwitchRequested?.Invoke();
}
