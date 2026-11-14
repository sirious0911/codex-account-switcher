using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexAccountSwitcher.Core;

internal static class WindowsExistingAmanoProcessAdapterTest
{
    public static void Run(string output)
    {
        var root = Path.Combine(output, "existing-amano-process-fixture");
        if (Directory.Exists(root))
            throw new InvalidOperationException("Offline process fixture must start absent.");
        var scripts = Path.Combine(root, "bundled-runtime");
        var legacy = Path.Combine(root, "existing-amano-config");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(legacy);
        foreach (var name in new[]
        {
            WindowsPackagedTelegramScripts.MonitorScriptName,
            WindowsPackagedTelegramScripts.AlertScriptName
        })
            File.Copy(Path.Combine("windows", "scripts", name), Path.Combine(scripts, name));
        foreach (var name in new[]
        {
            "send-telegram-health.ps1", "send-telegram-alert.ps1",
            "telegram-health-token.dpapi", "telegram-token.dpapi",
            "telegram-chat-id.txt"
        })
            File.WriteAllText(Path.Combine(legacy, name), "fixture-not-a-real-token");

        var observed = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        var fiveKey = new string('a', 64);
        var weeklyKey = new string('b', 64);
        var host = Path.Combine(scripts, "SwitcherHost.exe");
        var five = WindowsExistingAmanoBotRoute.Prepare(host, legacy,
            TelegramNotificationLane.FiveHourRecovery, fiveKey, observed);
        var weekly = WindowsExistingAmanoBotRoute.Prepare(host, legacy,
            TelegramNotificationLane.WeeklyRecovery, weeklyKey, observed);
        Require(five is not null && weekly is not null,
            "Existing Amano bot routes must resolve in fake fixture.");

        // Inspect the exact command that the production transport would
        // build, without starting PowerShell or loading any real secret.
        var command = WindowsAmanoPowerShellRunner.CreateProcessStartInfo(five!);
        var readyFive = five!;
        var expectedArgs = new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
            readyFive.ReviewedCodexScriptPath,
            "-LegacyBotDirectory", readyFive.ExistingAmanoBotDirectory,
            "-EventKey", readyFive.EventKey,
            "-Message", readyFive.Message,
            "-ApprovedSingleSend", "-WindowsSenderVerified"
        };
        Require(command.FileName.EndsWith(@"\WindowsPowerShell\v1.0\powershell.exe",
                    StringComparison.OrdinalIgnoreCase) &&
            command.ArgumentList.SequenceEqual(expectedArgs) &&
            !command.UseShellExecute && command.CreateNoWindow &&
            command.RedirectStandardOutput && command.RedirectStandardError &&
            !command.ArgumentList.Contains("-ExecutionPolicy") &&
            !command.ArgumentList.Any(arg => arg.Contains("token.dpapi",
                StringComparison.OrdinalIgnoreCase)),
            "Production runner must quote each argument, use Windows PS 5.1, " +
            "capture errors and never place credentials in argv.");

        var fake = new FakeRunner();
        var adapter = new WindowsExistingAmanoBotProcessAdapter(fake);
        var gate = new WindowsAmanoInvocationGate(true, true, true);

        var notApproved = adapter.ExecuteOnceAsync(five,
            gate with { SingleSendApproved = false }).GetAwaiter().GetResult();
        var notOwner = adapter.ExecuteOnceAsync(five,
            gate with { SoleSenderVerified = false }).GetAwaiter().GetResult();
        var notReserved = adapter.ExecuteOnceAsync(five,
            gate with { DurableReservationConfirmed = false }).GetAwaiter().GetResult();
        Require(notApproved.Status == WindowsAmanoDeliveryStatus.Blocked &&
            notOwner.Status == WindowsAmanoDeliveryStatus.Blocked &&
            notReserved.Status == WindowsAmanoDeliveryStatus.Blocked &&
            fake.Calls == 0,
            "None of the three mandatory send gates can be omitted.");

        fake.Next = new WindowsAmanoProcessResult(0,
            "CODEX_FIVEHOUR100_TELEGRAM_ACK_V1|" + fiveKey + "|102\r\n", false);
        var first = adapter.ExecuteOnceAsync(five, gate).GetAwaiter().GetResult();
        Require(first.Status == WindowsAmanoDeliveryStatus.Acknowledged &&
            first.MessageId == 102 && fake.Calls == 1 &&
            fake.LastRoute?.Silent == true,
            "Only a typed matching 5H receipt can acknowledge an attempted script.");
        fake.Next = new WindowsAmanoProcessResult(0,
            "CODEX_WEEKLY100_TELEGRAM_ACK_V1|" + weeklyKey + "|103\n", false);
        var second = adapter.ExecuteOnceAsync(weekly, gate).GetAwaiter().GetResult();
        Require(second.Status == WindowsAmanoDeliveryStatus.Acknowledged &&
            second.MessageId == 103 && fake.Calls == 2 &&
            fake.LastRoute?.Silent == false,
            "7D receipt must match the existing alert bot's separate marker.");

        fake.Next = new WindowsAmanoProcessResult(0,
            "CODEX_FIVEHOUR100_TELEGRAM_ACK_V1|" + fiveKey + "|102", false);
        Require(adapter.ExecuteOnceAsync(weekly, gate).GetAwaiter().GetResult().Status
                == WindowsAmanoDeliveryStatus.Uncertain,
            "A healthy-looking but wrong-bot receipt is uncertain.");
        fake.Next = new WindowsAmanoProcessResult(20, "", false);
        Require(adapter.ExecuteOnceAsync(five, gate).GetAwaiter().GetResult().Status
                == WindowsAmanoDeliveryStatus.Uncertain,
            "HTTP/DPAPI exit failures must stay uncertain, with no retry.");
        fake.Next = new WindowsAmanoProcessResult(0,
            "CODEX_FIVEHOUR100_TELEGRAM_ACK_V1|" + fiveKey + "|104", true);
        Require(adapter.ExecuteOnceAsync(five, gate).GetAwaiter().GetResult().Status
                == WindowsAmanoDeliveryStatus.Uncertain,
            "Any stderr output must invalidate the transport receipt.");
        fake.Next = new WindowsAmanoProcessResult(0, null, false, true);
        Require(adapter.ExecuteOnceAsync(five, gate).GetAwaiter().GetResult().Status
                == WindowsAmanoDeliveryStatus.Uncertain,
            "Timeout is not an invitation to retry.");
        fake.ThrowNext = true;
        Require(adapter.ExecuteOnceAsync(five, gate).GetAwaiter().GetResult().Status
                == WindowsAmanoDeliveryStatus.Uncertain,
            "A synthetic subprocess exception must never bubble credentials or retry.");
        var calls = fake.Calls;
        Require(adapter.ExecuteOnceAsync(readyFive with {
                Message = "untrusted arbitrary message" }, gate).GetAwaiter().GetResult().Status
                == WindowsAmanoDeliveryStatus.Blocked && fake.Calls == calls,
            "Arbitrary messages cannot be passed to the existing bot.");

        Require(adapter.ExecuteOnceAsync(readyFive with {
                ReviewedCodexScriptPath = "invalid" + '\0' + "path" },
                gate).GetAwaiter().GetResult().Status
                == WindowsAmanoDeliveryStatus.Blocked && fake.Calls == calls,
            "Malformed source path must not escape the pre-send gate.");

        File.AppendAllText(Path.Combine(scripts,
            WindowsPackagedTelegramScripts.MonitorScriptName), "# tampered");
        Require(adapter.ExecuteOnceAsync(five, gate).GetAwaiter().GetResult().Status
                == WindowsAmanoDeliveryStatus.Blocked && fake.Calls == calls,
            "Rechecking the script pin immediately before invocation is required.");
        File.Delete(Path.Combine(legacy, "telegram-token.dpapi"));
        Require(adapter.ExecuteOnceAsync(weekly, gate).GetAwaiter().GetResult().Status
                == WindowsAmanoDeliveryStatus.Blocked && fake.Calls == calls,
            "Missing legacy alert bot metadata must block before process invocation.");

        // A genuine PowerShell transport is constructible but NEVER used
        // by this test or by the app's automatic recovery callback.
        Require(WindowsExistingAmanoBotProcessAdapter.CreateWindowsTransport() is not null,
            "Windows 5.1 production script transport must be available for a future approved call.");
    }

    private static void Require(bool passes, string description)
    {
        if (!passes) throw new InvalidOperationException(description);
    }

    private sealed class FakeRunner : IWindowsAmanoScriptRunner
    {
        public int Calls { get; private set; }
        public bool ThrowNext { get; set; }
        public WindowsExistingAmanoBotInvocation? LastRoute { get; private set; }
        public WindowsAmanoProcessResult Next { get; set; }

        public Task<WindowsAmanoProcessResult> RunAsync(
            WindowsExistingAmanoBotInvocation route, CancellationToken cancellationToken)
        {
            Calls++;
            LastRoute = route;
            if (ThrowNext)
            {
                ThrowNext = false;
                throw new IOException("Synthetic subprocess failure");
            }
            return Task.FromResult(Next);
        }
    }
}
