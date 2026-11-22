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
        var baseDir = Path.Combine(Path.GetTempPath(),
            "p014-swift-native-no-send-" + Guid.NewGuid().ToString("N"));
        var activeHome = Path.Combine(baseDir, "active");
        var dataHome = Path.Combine(baseDir, "data");
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
            stage = "FIRST_ACTIVE_REFRESH";
            // Initial all-profile background fetch can overlap the first
            // probe. Retry only against the isolated synthetic CLI if Swift
            // correctly deferred an overlapping active read.
            for (int attempt = 0; attempt < 3 && observations.IsEmpty; attempt++)
            {
                await client.CommandAsync("refreshActive").WaitAsync(TimeSpan.FromSeconds(25));
                if (observations.IsEmpty) await Task.Delay(900);
            }
            await UntilAsync(() => !observations.IsEmpty, TimeSpan.FromSeconds(5));

            if (!observations.TryDequeue(out var sample) ||
                sample.ProfileID != selected ||
                !MonitorSyntheticPreview.TryRead(sample.RawJson, out var quota) ||
                quota is null ||
                quota.FiveHourRemaining != 33 || quota.WeeklyRemaining != 57 ||
                quota.CreditsRemaining != 2 ||
                quota.OrdinaryUsageAllowed is not null)
                throw new InvalidOperationException("Swift host did not publish exact synthetic 5H/7D quota.");

            var delta = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                - sample.ObservedAtMilliseconds;
            if (delta < -30000 || delta > 120000)
                throw new InvalidOperationException("Observation timestamp not fresh.");
            if (await File.ReadAllTextAsync(authFile) != auth)
                throw new InvalidOperationException("Synthetic active credential changed.");

            // Drain any responses from initial overlap retries. The
            // second command must produce its OWN later observation.
            while (observations.TryDequeue(out _)) { }
            await Task.Delay(60);
            stage = "SECOND_ACTIVE_REFRESH";
            await client.CommandAsync("refreshActive").WaitAsync(TimeSpan.FromSeconds(25));
            await UntilAsync(() => !observations.IsEmpty, TimeSpan.FromSeconds(8));
            if (!observations.TryDequeue(out var second) ||
                second.ProfileID != selected ||
                second.ObservedAtMilliseconds <= sample.ObservedAtMilliseconds ||
                !MonitorSyntheticPreview.TryRead(second.RawJson, out var secondQuota) ||
                secondQuota?.FiveHourRemaining != 33 ||
                secondQuota.WeeklyRemaining != 57)
                throw new InvalidOperationException("Repeated Swift host active refresh failed.");

            Console.WriteLine("PASS: fresh Windows SwiftHost.exe emits two strict active-only synthetic monitor observations; no send.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("HOLD_NATIVE_SWIFTHOST_ACCEPTANCE_" +
                stage + "_" + ex.GetType().Name);
            return 92;
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", originalCli);
            Environment.SetEnvironmentVariable("CODEX_SWITCHER_FAKE_RPC_MODE", originalFake);
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
