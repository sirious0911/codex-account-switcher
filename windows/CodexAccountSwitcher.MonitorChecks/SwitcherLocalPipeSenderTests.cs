using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;

internal static class SwitcherLocalPipeSenderTests
{
    private const string ValidRaw = """
    {
      "accountId":"FIXTURE_ACCOUNT_ONLY",
      "ordinaryUsageAllowed":null,
      "rateLimitsByLimitId":{"codex":{
        "planType":"plus",
        "primary":{"usedPercent":30,"windowDurationMins":300,"resetsAt":2000000000},
        "secondary":{"usedPercent":14,"windowDurationMins":10080,"resetsAt":2000600000}
      }},
      "rateLimitResetCredits":{"availableCount":1}
    }
    """;

    public static async Task RunAsync(Action<string, bool> check)
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = "switcher-sender-test-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await using var receiver = new NamedPipeServerStream(
            name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var sender = new SwitcherLocalPipeSender(name);
        await receiver.WaitForConnectionAsync(timeout.Token);
        check("IPC connects to a current-user-only Windows receiver", receiver.IsConnected);

        using var reader = new StreamReader(receiver, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        check("IPC nonblocking publish of fresh 5H, 7D and credits",
            sender.Publish(new CodexMonitorWireSample(now, ValidRaw)));

        var line = await reader.ReadLineAsync(timeout.Token);
        using var json = JsonDocument.Parse(line!);
        var root = json.RootElement;
        var raw = root.GetProperty("raw");
        var windows = raw.GetProperty("rateLimitsByLimitId").GetProperty("codex");
        check("IPC uses MonitorObserved envelope, newline framing and original timestamp",
            root.GetProperty("kind").GetString() == "monitorObservation" &&
            root.GetProperty("observedAtMs").GetInt64() == now &&
            Encoding.UTF8.GetByteCount(line!) <= SwitcherLocalPipeSender.MaxFrameBytes);
        check("IPC preserves actual 5H/7D quotas and reset credits in the receiver schema",
            windows.GetProperty("primary").GetProperty("usedPercent").GetInt32() == 30 &&
            windows.GetProperty("primary").GetProperty("windowDurationMins").GetInt32() == 300 &&
            windows.GetProperty("secondary").GetProperty("usedPercent").GetInt32() == 14 &&
            windows.GetProperty("secondary").GetProperty("windowDurationMins").GetInt32() == 10080 &&
            raw.GetProperty("rateLimitResetCredits").GetProperty("availableCount").GetInt32() == 1 &&
            raw.GetProperty("accountId").GetString() == "FIXTURE_ACCOUNT_ONLY");
        check("IPC never serializes a credential token or extra response fields",
            !line!.Contains("auth.json", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("access_token", StringComparison.OrdinalIgnoreCase) &&
            root.EnumerateObject().Count() == 3);

        check("IPC refuses repeated observation timestamps",
            !sender.Publish(new CodexMonitorWireSample(now, ValidRaw)));
        check("IPC refuses stale observations",
            !sender.Publish(new CodexMonitorWireSample(now - 20_000, ValidRaw)));
        check("IPC refuses malformed JSON",
            !sender.Publish(new CodexMonitorWireSample(now + 1, "{")));
        check("IPC refuses oversized JSON without splitting frames",
            !sender.Publish(new CodexMonitorWireSample(now + 1,
                ValidRaw.TrimEnd().Insert(ValidRaw.TrimEnd().Length - 1,
                    ",\"spare\":\"" + new string('x', 8192) + "\""))));
        check("IPC rejects invalid pipe names",
            RejectName("..\\pipe") && RejectName("invalid/path"));

        var later = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1;
        check("IPC accepts subsequent fresh observation on the same connection",
            sender.Publish(new CodexMonitorWireSample(later, ValidRaw)));
        var next = await reader.ReadLineAsync(timeout.Token);
        using var parsed = JsonDocument.Parse(next!);
        check("IPC delivers a second complete bounded JSON line",
            parsed.RootElement.GetProperty("observedAtMs").GetInt64() == later);

        // The source selection is independent of the switcher's multi-profile refresh.
        var activeA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var activeB = Guid.Parse("22222222-2222-2222-2222-222222222222");
        static AccountSnapshot State(Guid? id, bool confirmed = true, bool mutating = false,
            string? error = null) =>
            new([], id, new(), mutating, false, confirmed, error, new Dictionary<string, string>());
        var gate = new ActiveMonitorPublicationGate();
        var sampleA = new CodexMonitorWireSample(now, ValidRaw, activeA);
        var sampleB = new CodexMonitorWireSample(now + 1, ValidRaw, activeB);
        check("unknown active profile cannot publish", !gate.Allows(sampleA));
        check("confirmed active profile A opens only A",
            gate.Observe(State(activeA)) && gate.Allows(sampleA) && !gate.Allows(sampleB));
        check("existing unlabelled monitor samples remain valid for local worker but not IPC",
            !gate.Allows(new CodexMonitorWireSample(now, ValidRaw)));
        check("repeated snapshot with same active profile preserves IPC epoch",
            !gate.Observe(State(activeA)) && gate.Allows(sampleA));
        check("SwiftHost transport failure immediately invalidates prior account",
            gate.Observe(State(activeA, error: "Core transport stopped")) &&
            !gate.Allows(sampleA) && gate.ActiveProfile is null);
        check("persistent host error cannot re-enable the old IPC sender",
            !gate.Observe(State(activeA, error: "Core transport stopped")) &&
            !gate.Allows(sampleA));
        check("healthy new snapshot can restore a verified IPC epoch",
            gate.Observe(State(activeA)) && gate.Allows(sampleA));
        gate.SwitchRequested();
        check("switch request immediately invalidates A before host confirmation",
            !gate.Allows(sampleA));
        check("pre-switch UI state cannot reopen the IPC gate",
            !gate.Observe(State(activeA)) && !gate.Allows(sampleA));
        check("switch in-progress cannot publish A or B",
            !gate.Observe(State(activeA, mutating: true)) && !gate.Allows(sampleB));
        check("B becomes publishable only after switch completion",
            gate.Observe(State(activeB)) && gate.Allows(sampleB) && !gate.Allows(sampleA));
        check("unconfirmed identity invalidates the active stream",
            gate.Observe(State(activeB, confirmed: false)) && !gate.Allows(sampleB));
        check("verified profile B can reopen after a new snapshot",
            gate.Observe(State(activeB)) && gate.Allows(sampleB));
        check("no active profile clears the display",
            gate.Observe(State(null)) && !gate.Allows(sampleB));

        // Closing the pipe invalidates #14's displayed snapshot immediately.
        sender.Invalidate();
        var ended = await reader.ReadLineAsync(timeout.Token);
        check("switch invalidation closes the external receiver stream", ended is null);
        check("observations captured before the switch cutoff are rejected",
            !sender.Publish(new CodexMonitorWireSample(now, ValidRaw)));
        await receiver.DisposeAsync();

        await using (var resumedReceiver = new NamedPipeServerStream(
            name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await resumedReceiver.WaitForConnectionAsync(timeout.Token);
            using var resumedReader = new StreamReader(resumedReceiver,
                new UTF8Encoding(false), false, 1024, leaveOpen: true);
            // No pre-switch buffered frame is replayed on reconnect.
            var fresh = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1;
            check("fresh post-switch observation is accepted in new epoch",
                sender.Publish(new CodexMonitorWireSample(fresh, ValidRaw, activeB)));
            var received = await resumedReader.ReadLineAsync(timeout.Token);
            using var resumedFrame = JsonDocument.Parse(received!);
            check("new receiver receives only post-switch data",
                resumedFrame.RootElement.GetProperty("observedAtMs").GetInt64() == fresh);
        }

        // The internal SwiftHost stdio envelope carries profile UUID, but the
        // independent Named Pipe keeps the exact three-field #14 wire format.
        using (var extended = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            kind = "monitorObservation",
            profileID = activeB.ToString(),
            observedAtMs = now,
            raw = JsonSerializer.Deserialize<JsonElement>(ValidRaw)
        })))
        {
            check("SwiftHost profile UUID is parsed for active selection",
                CodexMonitorWireSample.TryParse(extended.RootElement, out var correlated) &&
                correlated?.ProfileID == activeB);
        }

        // Missing receiver must never make the account switcher wait or fail.
        var missingPipe = "switcher-no-receiver-" + Guid.NewGuid().ToString("N");
        await using (var unconnected = new SwitcherLocalPipeSender(missingPipe))
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var accepted = unconnected.Publish(
                new CodexMonitorWireSample(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ValidRaw));
            check("IPC does not block the account switcher when receiver is absent",
                accepted && started.Elapsed < TimeSpan.FromSeconds(1));
            await unconnected.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            check("IPC cancellation releases absent-receiver background connection",
                started.Elapsed < TimeSpan.FromSeconds(5));
        }
    }

    private static bool RejectName(string name)
    {
        try { _ = new SwitcherLocalPipeSender(name); return false; }
        catch (ArgumentException) { return true; }
    }
}
