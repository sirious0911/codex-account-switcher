using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CodexAccountSwitcher.Core;

internal static class NativeSwiftHostRefreshSmoke
{
    private const string TestAccount = "synthetic-fixture-account";
    private const string TestMode = "P014_NATIVE_NO_SEND_SYNTHETIC_ONLY";

    // Invoked as the Codex App Server in a child process created by Swift.
    // Deliberately handles only local, fixed fake JSON-RPC responses.
    public static async Task<int> FakeCodexRpcAsync()
    {
        if (Environment.GetEnvironmentVariable("CODEX_SWITCHER_FAKE_RPC_MODE") != TestMode)
            return 93;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var packet = JsonDocument.Parse(line);
            var request = packet.RootElement;
            if (!request.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number)
                continue;
            string? method = request.TryGetProperty("method", out var m)
                ? m.GetString() : null;
            // Counts only fixed method names, never auth, IDs or payloads.
            var traceFile = Environment.GetEnvironmentVariable("CODEX_SWITCHER_FAKE_RPC_TRACE_FILE");
            if (!string.IsNullOrEmpty(traceFile) &&
                Path.GetFileName(traceFile) == "rpc-methods.log" &&
                Path.GetFullPath(traceFile).StartsWith(
                    Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            {
                try { File.AppendAllText(traceFile, method is
                    ("initialize" or "account/read" or "account/rateLimits/read")
                    ? method + "\n" : "denied\n"); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            // Never silently acknowledge login, credit consumption,
            // token refresh, browser actions or other mutating RPC methods.
            if (method is not ("initialize" or "account/read" or
                "account/rateLimits/read"))
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
                    new { id = id.GetInt32(), error = new {
                        code = -32601, message = "Synthetic no-send RPC denied." } }));
                await Console.Out.FlushAsync();
                return 94;
            }
            object response = method switch
            {
                "account/read" => new
                {
                    account = new { accountId = TestAccount, email = "synthetic@example.test" }
                },
                "account/rateLimits/read" => new
                {
                    rateLimitResetCredits = new { availableCount = 2 },
                    rateLimits = new
                    {
                        primary = new
                        {
                            usedPercent = 67, windowDurationMins = 300,
                            resetsAt = 2_000_000_000L
                        },
                        secondary = new
                        {
                            usedPercent = 43, windowDurationMins = 10080,
                            resetsAt = 2_000_000_000L
                        }
                    }
                },
                "initialize" => new { capabilities = new { } },
                _ => new { }
            };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
                new { id = id.GetInt32(), result = response }));
            await Console.Out.FlushAsync();
        }
        return 0;
    }

    public static async Task<int> RunAsync(string swiftHostPath)
    {
        if (!File.Exists(swiftHostPath) ||
            !Path.GetFileName(swiftHostPath).Equals("SwitcherHost.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("HOLD_NATIVE_SWIFT_HOST_NOT_BUILT");
            return 91;
        }

        var testExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(testExe) ||
            !File.Exists(testExe) ||
            !Path.GetExtension(testExe).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native test apphost EXE required.");

        var originalCli = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        var originalFake = Environment.GetEnvironmentVariable("CODEX_SWITCHER_FAKE_RPC_MODE");
        var originalTrace = Environment.GetEnvironmentVariable("CODEX_SWITCHER_FAKE_RPC_TRACE_FILE");
        var baseDir = Path.Combine(Path.GetTempPath(),
            "p014-swift-native-no-send-" + Guid.NewGuid().ToString("N"));
        var activeHome = Path.Combine(baseDir, "active");
        var dataHome = Path.Combine(baseDir, "data");
        var traceFile = Path.Combine(baseDir, "rpc-methods.log");
        Directory.CreateDirectory(activeHome);
        const string auth = """{"tokens":{"account_id":"synthetic-fixture-account"}}""";
        var authFile = Path.Combine(activeHome, "auth.json");
        await File.WriteAllTextAsync(authFile, auth);
        var observations = new ConcurrentQueue<CodexMonitorWireSample>();
        var stage = "PREPARE";
        try
        {
            // The SwiftHost can only execute this same local test apphost for
            // Codex RPC: never use user-installed Codex, its tokens, network,
            // account mutations, browser, Telegram or any live profile.
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", testExe);
            Environment.SetEnvironmentVariable("CODEX_SWITCHER_FAKE_RPC_MODE", TestMode);
            Environment.SetEnvironmentVariable("CODEX_SWITCHER_FAKE_RPC_TRACE_FILE", traceFile);
            await using var client = new CoreClient(
                Path.GetFullPath(swiftHostPath), dataHome, activeHome,
                blockMutatingCommands: true);
            client.MonitorObserved += sample => observations.Enqueue(sample);
            stage = "INITIALIZE";
            await client.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
            stage = "VERIFY_ACTIVE_IDENTITY";
            await UntilAsync(() => client.State.ActiveIdentityConfirmed &&
                client.State.ActiveAccountID.HasValue,
                TimeSpan.FromSeconds(12));
            var selected = client.State.ActiveAccountID!.Value;
            // Provider's first 300-second refresh must publish its fresh
            // active-account observation before any explicit refreshActive.
            stage = "INITIAL_BASELINE_OBSERVATION";
            await UntilAsync(() => !observations.IsEmpty, TimeSpan.FromSeconds(18));
            if (!observations.TryDequeue(out var baseline) ||
                baseline.ProfileID != selected ||
                !MonitorSyntheticPreview.TryRead(baseline.RawJson, out var baselineQuota) ||
                baselineQuota?.FiveHourRemaining != 33 ||
                baselineQuota.WeeklyRemaining != 57 ||
                baselineQuota.CreditsRemaining != 2 ||
                baselineQuota.OrdinaryUsageAllowed is not null)
                throw new InvalidOperationException("Missing fresh idle startup quota.");

            var initialLag = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                - baseline.ObservedAtMilliseconds;
            if (initialLag < -30000 || initialLag > 120000)
                throw new InvalidOperationException("Startup quota is stale.");
            if (await File.ReadAllTextAsync(authFile) != auth)
                throw new InvalidOperationException("Synthetic credential changed.");

            // Initial provider read can still be unwinding. Retry against
            // the synthetic-only CLI, never a real Codex installation.
            stage = "FIRST_ACTIVE_REFRESH";
            for (int attempt = 0; attempt < 3 && observations.IsEmpty; attempt++)
            {
                await Task.Delay(120);
                await client.CommandAsync("refreshActive").WaitAsync(TimeSpan.FromSeconds(25));
                if (observations.IsEmpty) await Task.Delay(900);
            }
            await UntilAsync(() => !observations.IsEmpty, TimeSpan.FromSeconds(5));

            if (!observations.TryDequeue(out var sample) ||
                sample.ProfileID != selected ||
                sample.ObservedAtMilliseconds <= baseline.ObservedAtMilliseconds ||
                !MonitorSyntheticPreview.TryRead(sample.RawJson, out var quota) ||
                quota?.FiveHourRemaining != 33 ||
                quota.WeeklyRemaining != 57 || quota.CreditsRemaining != 2 ||
                quota.OrdinaryUsageAllowed is not null)
                throw new InvalidOperationException("First active quota missing.");
            while (observations.TryDequeue(out _)) { }
            await Task.Delay(80);
            stage = "SECOND_ACTIVE_REFRESH";
            await client.CommandAsync("refreshActive").WaitAsync(TimeSpan.FromSeconds(25));
            await UntilAsync(() => !observations.IsEmpty, TimeSpan.FromSeconds(8));
            if (!observations.TryDequeue(out var second) ||
                second.ProfileID != selected ||
                second.ObservedAtMilliseconds <= sample.ObservedAtMilliseconds ||
                !MonitorSyntheticPreview.TryRead(second.RawJson, out var secondQuota) ||
                secondQuota?.FiveHourRemaining != 33 ||
                secondQuota.WeeklyRemaining != 57)
                throw new InvalidOperationException("Second active quota missing.");

            Console.WriteLine("PASS: fresh Windows SwiftHost.exe emits first idle 300s baseline plus two strict active-only synthetic monitor observations; no send.");
            return 0;
        }
        catch (Exception ex)
        {
            // Do not print Swift's raw exception text: it could include
            // paths or local server diagnostics. Report only known categories.
            var detail = ex.Message;
            var category = detail switch
            {
                var m when m.Contains("Codex executable could not be found.", StringComparison.OrdinalIgnoreCase)
                    => "FAKE_CLI_NOT_FOUND",
                var m when m.Contains("Codex app-server failed to start", StringComparison.OrdinalIgnoreCase)
                    => "FAKE_CLI_LAUNCH",
                var m when m.Contains("No weekly Codex Usage window", StringComparison.OrdinalIgnoreCase)
                    => "FAKE_WEEKLY_WINDOW_UNAVAILABLE",
                var m when m.Contains("malformed JSON", StringComparison.OrdinalIgnoreCase)
                    => "FAKE_RPC_MALFORMED",
                var m when m.Contains("app-server closed the connection", StringComparison.OrdinalIgnoreCase)
                    => "FAKE_RPC_CLOSED",
                var m when m.Contains("app-server did not respond", StringComparison.OrdinalIgnoreCase)
                    => "FAKE_RPC_TIMEOUT",
                var m when m.Contains("Unknown host command", StringComparison.OrdinalIgnoreCase)
                    => "OLD_HOST_NO_REFRESH_COMMAND",
                var m when m.Contains("Synthetic no-send RPC denied", StringComparison.OrdinalIgnoreCase)
                    => "DISALLOWED_RPC_METHOD",
                var m when m.Contains("account identity", StringComparison.OrdinalIgnoreCase)
                    => "ACTIVE_IDENTITY_UNVERIFIED",
                _ => "UNCLASSIFIED"
            };
            Console.Error.WriteLine("HOLD_NATIVE_SWIFTHOST_ACCEPTANCE_" +
                stage + "_" + ex.GetType().Name + "_" + category);
            if (File.Exists(traceFile))
            {
                var methods = File.ReadAllLines(traceFile);
                foreach (var method in new[] { "initialize", "account/read", "account/rateLimits/read", "denied" })
                    Console.Error.WriteLine("SYNTHETIC_RPC_" +
                        method.Replace('/', '_').ToUpperInvariant() + "_COUNT=" +
                        methods.Count(m => m == method));
            }
            else Console.Error.WriteLine("SYNTHETIC_RPC_TRACE_ABSENT");
            return 92;
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", originalCli);
            Environment.SetEnvironmentVariable("CODEX_SWITCHER_FAKE_RPC_MODE", originalFake);
            Environment.SetEnvironmentVariable("CODEX_SWITCHER_FAKE_RPC_TRACE_FILE", originalTrace);
            try { Directory.Delete(baseDir, recursive: true); }
            catch (IOException) { /* Fixture cleanup only; never touch user data. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan horizon)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < horizon)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Synthetic SwiftHost observation deadline.");
    }
}
