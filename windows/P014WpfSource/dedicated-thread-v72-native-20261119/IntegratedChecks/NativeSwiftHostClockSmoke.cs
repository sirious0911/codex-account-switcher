using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CodexAccountSwitcher.Core;

internal static partial class NativeSwiftHostRefreshSmoke
{
    /// <summary>
    /// Real elapsed-time integration gate, but deliberately SYNTHETIC.
    /// Same SwiftHost and strict CoreClient IPC as the Windows app, and
    /// the real 30s/15s C# scheduling policy; zero real accounts or sends.
    /// Neither a stopwatch test nor a fake activity flag proves that the
    /// user's foreground Codex activity detector works in production.
    /// </summary>
    public static async Task<int> RunWallClockAsync(string swiftHostPath, string scenario)
    {
        if (scenario is not ("active" or "reset") ||
            !File.Exists(swiftHostPath) ||
            Path.GetFileName(swiftHostPath) != "SwitcherHost.exe")
            return 95;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) ||
            !File.Exists(exe) ||
            !Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return 95;

        const string modeVariable = "CODEX_SWITCHER_FAKE_RPC_MODE";
        const string cliVariable = "CODEX_CLI_PATH";
        const string resetVariable = "CODEX_SWITCHER_FAKE_RESET_WATCH_MODE";
        const string traceVariable = "CODEX_SWITCHER_FAKE_RPC_TRACE_FILE";
        var oldMode = Environment.GetEnvironmentVariable(modeVariable);
        var oldCli = Environment.GetEnvironmentVariable(cliVariable);
        var oldReset = Environment.GetEnvironmentVariable(resetVariable);
        var oldTrace = Environment.GetEnvironmentVariable(traceVariable);
        var root = Path.Combine(Path.GetTempPath(),
            "p014-wallclock-no-send-" + Guid.NewGuid().ToString("N"));
        var authHome = Path.Combine(root, "active");
        var dataHome = Path.Combine(root, "data");
        var trace = Path.Combine(root, "rpc-methods.log");
        var stage = "PREPARE";
        try
        {
            Directory.CreateDirectory(authHome);
            const string fixture = """{"tokens":{"account_id":"synthetic-fixture-account"}}""";
            var authFile = Path.Combine(authHome, "auth.json");
            await File.WriteAllTextAsync(authFile, fixture);

            Environment.SetEnvironmentVariable(modeVariable, TestMode);
            Environment.SetEnvironmentVariable(cliVariable, exe);
            Environment.SetEnvironmentVariable(resetVariable,
                scenario == "reset" ? "reset" : null);
            Environment.SetEnvironmentVariable(traceVariable, trace);

            var samples = new ConcurrentQueue<CodexMonitorWireSample>();
            await using (var client = new CoreClient(
                Path.GetFullPath(swiftHostPath), dataHome, authHome,
                blockMutatingCommands: true))
            {
                client.MonitorObserved += samples.Enqueue;
                stage = "STARTUP";
                await client.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await UntilAsync(() =>
                    client.State.ActiveIdentityConfirmed &&
                    client.State.ActiveAccountID.HasValue,
                    TimeSpan.FromSeconds(12));
                var profile = client.State.ActiveAccountID!.Value;

                stage = "FRESH_BASELINE";
                await UntilAsync(() => !samples.IsEmpty, TimeSpan.FromSeconds(18));
                if (!samples.TryDequeue(out var baseline) ||
                    !Accepted(client.State, baseline, profile,
                        previousMs: 0, out var initialQuota))
                    throw new InvalidOperationException("Fresh baseline rejected.");

                if (scenario == "reset" &&
                    (initialQuota.FiveHourResetsAt is not DateTimeOffset reset ||
                     reset <= DateTimeOffset.UtcNow.AddSeconds(10) ||
                     reset > DateTimeOffset.UtcNow.AddMinutes(3)))
                    throw new InvalidOperationException("Reset watch not seeded.");
                if (scenario == "active" &&
                    initialQuota.FiveHourResetsAt is DateTimeOffset notNear &&
                    notNear <= DateTimeOffset.UtcNow.AddHours(6))
                    throw new InvalidOperationException("Activity fixture reset too near.");

                var policy = new AdaptiveUsageRefreshPolicy(DateTimeOffset.UtcNow);
                if (policy.TrySchedule(DateTimeOffset.UtcNow, false, client.State))
                    throw new InvalidOperationException("Idle poll before trusted baseline.");
                policy.Observed(profile, DateTimeOffset.UtcNow,
                    initialQuota.FiveHourResetsAt, initialQuota.WeeklyResetsAt);

                // No stubbed wall clock. The policy receives actual UTC;
                // the real SwiftHost+fake JSON-RPC read is performed at each
                // permitted scheduled instant. Timer granularity = 1s.
                var timer = Stopwatch.StartNew();
                var lastIssueSeconds = 0.0;
                long lastObservedMs = baseline.ObservedAtMilliseconds;
                int sent = 0;
                var targetSeconds = scenario == "reset" ? 15.0 : 30.0;
                while (sent < 2)
                {
                    stage = "WAIT_SCHEDULE_" + (sent + 1);
                    if (timer.Elapsed > TimeSpan.FromSeconds(targetSeconds * 3 + 22))
                        throw new TimeoutException("Synthetic wallclock scheduling deadline.");

                    if (!policy.TrySchedule(DateTimeOffset.UtcNow,
                            hasRecentCodexWork: scenario == "active", client.State))
                    {
                        await Task.Delay(1000);
                        continue;
                    }

                    var issued = timer.Elapsed.TotalSeconds;
                    var sincePrevious = issued - lastIssueSeconds;
                    if (sincePrevious < targetSeconds - 1.5 ||
                        sincePrevious > targetSeconds + 9.0)
                        throw new InvalidOperationException("Real scheduling interval outside tolerance.");

                    stage = "READ_PROVIDER_" + (sent + 1);
                    while (samples.TryDequeue(out _)) { }
                    await client.CommandAsync("refreshActive")
                        .WaitAsync(TimeSpan.FromSeconds(18));
                    await UntilAsync(() => !samples.IsEmpty, TimeSpan.FromSeconds(8));

                    if (!samples.TryDequeue(out var latest) ||
                        !Accepted(client.State, latest, profile,
                            lastObservedMs, out var parsed))
                        throw new InvalidOperationException("New quota observation rejected.");
                    lastObservedMs = latest.ObservedAtMilliseconds;
                    policy.Observed(profile, DateTimeOffset.UtcNow,
                        parsed.FiveHourResetsAt, parsed.WeeklyResetsAt);

                    sent++;
                    lastIssueSeconds = issued;
                    // Never output account IDs, real usage amounts or creds.
                    Console.WriteLine($"PASS_WALLCLOCK_{scenario.ToUpperInvariant()}_READ_{sent}_INTERVAL_SECONDS={sincePrevious:F1}");
                }

                if (await File.ReadAllTextAsync(authFile) != fixture)
                    throw new InvalidOperationException("Synthetic credential mutated.");
            }

            stage = "VERIFY_RPC_CALL_COUNT";
            var rpc = File.Exists(trace) ? File.ReadAllLines(trace) : [];
            if (rpc.Count(m => m == "account/rateLimits/read") != 3 ||
                rpc.Any(m => m == "denied"))
                throw new InvalidOperationException("Provider called wrong number of times.");
            Console.WriteLine($"PASS_WALLCLOCK_{scenario.ToUpperInvariant()}_BASELINE_PLUS_2_ACTIVE_READS_NO_SEND");
            return 0;
        }
        catch (Exception error)
        {
            // Never echo exception details or private paths to CI logs.
            Console.Error.WriteLine(
                "HOLD_WALLCLOCK_" + scenario.ToUpperInvariant() + "_" +
                stage + "_" + error.GetType().Name);
            return 96;
        }
        finally
        {
            Environment.SetEnvironmentVariable(cliVariable, oldCli);
            Environment.SetEnvironmentVariable(modeVariable, oldMode);
            Environment.SetEnvironmentVariable(resetVariable, oldReset);
            Environment.SetEnvironmentVariable(traceVariable, oldTrace);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool Accepted(
        AccountSnapshot state,
        CodexMonitorWireSample? sample,
        Guid active,
        long previousMs,
        out MonitorSyntheticPreview.Snapshot reading)
    {
        reading = null!;
        if (sample is null || sample.ObservedAtMilliseconds <= previousMs)
            return false;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lag = nowMs - sample.ObservedAtMilliseconds;
        if (lag < -30_000 || lag > 20_000) return false;
        var gate = new ActiveMonitorPublicationGate();
        gate.Observe(state);
        if (gate.ActiveProfile != active || !gate.Allows(sample))
            return false;
        if (!MonitorSyntheticPreview.TryRead(sample.RawJson, out var parsed) ||
            parsed is null ||
            parsed.FiveHourRemaining != 33 ||
            parsed.WeeklyRemaining != 57 ||
            parsed.CreditsRemaining != 2 ||
            parsed.OrdinaryUsageAllowed is not null)
            return false;
        reading = parsed;
        return true;
    }
}