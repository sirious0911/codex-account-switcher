using CodexAccountSwitcher.Core;
using System.Text.Json;

const string valid = """
{
  "accountId":"provider-account-1",
  "ordinaryUsageAllowed":true,
  "rateLimitsByLimitId":{"codex":{
     "planType":"plus",
     "primary":{"usedPercent":70,"resetsAt":1800030000,"windowDurationMins":300},
     "secondary":{"usedPercent":40,"resetsAt":1800600000,"windowDurationMins":10080}
  }},
  "rateLimitResetCredits":{"availableCount":2}
}
""";

var checks = 0;

void Check(string name, bool condition)
{
    if (!condition) throw new Exception("FAIL " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}

bool Parse(string? json, out MonitorSyntheticPreview.Snapshot? state) =>
    MonitorSyntheticPreview.TryRead(json, out state);

Check("valid fixture is accepted", Parse(valid, out var result) && result is not null);
Check("5-hour remaining is 30", result!.FiveHourRemaining == 30);
Check("weekly remaining is 60", result.WeeklyRemaining == 60);
Check("reset credits count is 2", result.CreditsRemaining == 2);
Check("explicit true permission is retained", result.OrdinaryUsageAllowed == true);
Check("verified plan is flagged without exposing plan text", result.HasVerifiedPlan);
Check("UI text is mock-labelled and has no provider ID",
      result.Display.Contains("5H 30%") && result.Display.Contains("7D 60%") &&
      result.Display.Contains("Credits 2") && result.Display.EndsWith("MOCK") &&
      !result.Display.Contains("provider-account-1"));
Check("missing provider identity is rejected", !Parse(valid.Replace("\"provider-account-1\"", "null"), out _));
Check("blank provider identity is rejected", !Parse(valid.Replace("\"provider-account-1\"", "\" \""), out _));
Check("missing plan is accepted as unknown", Parse(valid.Replace("\"planType\":\"plus\"", "\"planType\":null"), out var unknownPlan) && unknownPlan?.HasVerifiedPlan == false);
Check("permission unknown is preserved", Parse(valid.Replace("\"ordinaryUsageAllowed\":true", "\"ordinaryUsageAllowed\":null"), out var unknownAllowed) && unknownAllowed?.OrdinaryUsageAllowed == null);
Check("missing optional primary remains unknown", Parse(valid.Replace("\"primary\":{\"usedPercent\":70,\"resetsAt\":1800030000,\"windowDurationMins\":300}", "\"primary\":null"), out var missingFive) && missingFive?.FiveHourRemaining == null && missingFive?.WeeklyRemaining == 60);
Check("missing credits show UNKNOWN not zero", Parse(valid.Replace("\"availableCount\":2", "\"availableCount\":null"), out var missingCredits) && missingCredits?.CreditsRemaining == null && missingCredits?.Display.Contains("Credits UNKNOWN") == true);
Check("zero reset credits remain valid", Parse(valid.Replace("\"availableCount\":2", "\"availableCount\":0"), out var zeroCredits) && zeroCredits?.CreditsRemaining == 0);
Check("negative credits rejected", !Parse(valid.Replace("\"availableCount\":2", "\"availableCount\":-1"), out _));
Check("non numeric percent rejected", !Parse(valid.Replace("\"usedPercent\":70", "\"usedPercent\":\"70\""), out _));
Check("out of bounds percent rejected", !Parse(valid.Replace("\"usedPercent\":70", "\"usedPercent\":101"), out _));
Check("incorrect 5-hour duration rejected", !Parse(valid.Replace("\"windowDurationMins\":300", "\"windowDurationMins\":600"), out _));
Check("incorrect weekly duration rejected", !Parse(valid.Replace("\"windowDurationMins\":10080", "\"windowDurationMins\":1500"), out _));
Check("permission wrong type rejected", !Parse(valid.Replace("\"ordinaryUsageAllowed\":true", "\"ordinaryUsageAllowed\":\"true\""), out _));
Check("plan wrong type rejected", !Parse(valid.Replace("\"planType\":\"plus\"", "\"planType\":42"), out _));
Check("account ID control characters rejected", !Parse(valid.Replace("\"provider-account-1\"", "\"provider\\naccount-1\""), out _));
Check("malformed JSON rejected", !Parse("{", out _));
Check("oversized payload rejected", !Parse(valid + new string(' ', MonitorSyntheticPreview.MaxJsonBytes + 1), out _));
Check("null payload rejected", !Parse(null, out _));
Check("missing rate limits rejected", !Parse("{" + "\"accountId\":\"id\"" + "}", out _));
Check("no external process or filesystem methods in parser", typeof(MonitorSyntheticPreview).GetMethods().Where(m => m.DeclaringType == typeof(MonitorSyntheticPreview)).All(m => m.Name == "TryRead"));

var accountID = Guid.Parse("11111111-1111-1111-1111-111111111111");
var otherID = Guid.Parse("22222222-2222-2222-2222-222222222222");
var resetTime = DateTimeOffset.FromUnixTimeSeconds(1_800_500_000);
var live = new AccountSnapshot(
    [
        new(new(accountID, "Personal", "personal@example.test"), "P",
            new(65, resetTime, 35, resetTime.AddHours(-2), 0), null),
        new(new(otherID, "Studio", null), "S", null, "refresh unavailable", "error")
    ],
    accountID, new(), false, false, true, null, new Dictionary<string, string>());
var overview = MonitorOverview.From(live);
Check("existing account snapshot produces two monitor rows", overview.Count == 2);
Check("confirmed active account is identified without switching", overview[0].IsActive && !overview[1].IsActive);
Check("snapshot weekly and five-hour quotas are preserved", overview[0].WeeklyRemainingPercent == 65 && overview[0].FiveHourRemainingPercent == 35);
Check("snapshot reset times are preserved", overview[0].WeeklyResetsAt == resetTime &&
    overview[0].FiveHourResetsAt == resetTime.AddHours(-2));
Check("actual zero credits differ from unknown credits", overview[0].ResetCreditsRemaining == 0 &&
    overview[1].ResetCreditsRemaining == null);
Check("unavailable quota stays unknown and exposes error state", overview[1].WeeklyRemainingPercent == null &&
    overview[1].FiveHourRemainingPercent == null && overview[1].HasUsageError && overview[1].UsageStatus == "error");
Check("account display name is used if there is no email", overview[1].AccountLabel == "Studio");
Check("unconfirmed active identity is not invented", MonitorOverview.From(live with { ActiveIdentityConfirmed = false }).All(row => !row.IsActive));
Check("empty account set produces empty monitor", MonitorOverview.From(AccountSnapshot.Empty).Count == 0);
Check("monitor projections never mutate account snapshot", live.Accounts[0].Usage!.ResetCreditsRemaining == 0 &&
    live.Accounts[1].Usage is null && live.ActiveAccountID == accountID);

using (var wire = JsonDocument.Parse("{\"kind\":\"monitorObservation\",\"observedAtMs\":1800000000000,\"raw\":" + valid + "}"))
{
    Check("Swift wire observation parses as a Windows monitor sample",
        CodexMonitorWireSample.TryParse(wire.RootElement, out var current) &&
        current is not null && current.ObservedAtMilliseconds == 1_800_000_000_000);
    Check("wire parser preserves exact raw JSON and existing monitor values",
        CodexMonitorWireSample.TryParse(wire.RootElement, out var parsed) &&
        parsed is not null && MonitorSyntheticPreview.TryRead(parsed.RawJson, out var usage) &&
        usage?.FiveHourRemaining == 30 && usage.WeeklyRemaining == 60);
}
using (var missingPlan = JsonDocument.Parse("{\"kind\":\"monitorObservation\",\"observedAtMs\":1800000000000,\"raw\":" +
    valid.Replace("\"planType\":\"plus\"", "\"planType\":null") + "}"))
{
    Check("Swift wire retains UNKNOWN plan rather than inferring one",
        CodexMonitorWireSample.TryParse(missingPlan.RootElement, out var parsed) &&
        parsed is not null && MonitorSyntheticPreview.TryRead(parsed.RawJson, out var usage) &&
        usage?.HasVerifiedPlan == false);
}
using (var invalid = JsonDocument.Parse("{\"kind\":\"monitorObservation\",\"observedAtMs\":-1,\"raw\":" + valid + "}"))
    Check("invalid wire timestamp rejected", !CodexMonitorWireSample.TryParse(invalid.RootElement, out _));
using (var invalid = JsonDocument.Parse("{\"kind\":\"unrelated\",\"observedAtMs\":1800000000000,\"raw\":" + valid + "}"))
    Check("unrelated host protocol message not misread as monitor observation",
        !CodexMonitorWireSample.TryParse(invalid.RootElement, out _));


var temporaryRoot = Path.Combine(Path.GetTempPath(), "switcher-local-monitor-" + Guid.NewGuid().ToString("N"));
try
{
    static CodexMonitorWireSample Sample(string data, long ms) => new(ms, data);
    const long t0 = 1_800_000_000_000L;
    var worker = new LocalMonitorWorker(temporaryRoot);
    var starting = valid.Replace("\"availableCount\":2", "\"availableCount\":1");
    var first = worker.Process(Sample(starting, t0));
    Check("local worker records silent baseline",
        first.EventKinds.Count == 0 && first.MockAlertMessages.Count == 0 &&
        first.MonitorPreview.Contains("5H 30%"));
    var pending = worker.Process(Sample(valid, t0 + 60_000));
    Check("first credit increase remains unconfirmed",
        pending.EventKinds.Count == 0 && pending.UniqueEventCount == 0);
    var confirmed = worker.Process(Sample(valid, t0 + 120_000));
    Check("confirmed mock event is recorded once",
        confirmed.EventKinds.SequenceEqual(new[] { "CREDIT_INCREASE" }) &&
        confirmed.MockAlertMessages.Count == 1 &&
        confirmed.UniqueEventCount == 1 && confirmed.CompletedMockDeliveries == 1);
    var visibleHistory = worker.ReadHistory();
    Check("local worker exposes persisted event history for the Windows UI",
        visibleHistory.Count == 1 && visibleHistory[0].EventKind == "CREDIT_INCREASE" &&
        visibleHistory[0].MockCompleted && visibleHistory[0].AccountReference.Length == 8);
    Check("monitor history does not expose original account IDs",
        visibleHistory.All(x => x.AccountReference != "provider-account-1"));
    Check("mock messages do not expose provider IDs",
        confirmed.MockAlertMessages[0].Contains("[모의 알림]") &&
        !confirmed.MockAlertMessages[0].Contains("provider-account-1"));
    var restarted = new LocalMonitorWorker(temporaryRoot);
    var replay = restarted.Process(Sample(valid, t0 + 180_000));
    Check("restart restores ledger and prevents repeated mock alert",
        replay.EventKinds.Count == 0 && replay.MockAlertMessages.Count == 0 &&
        replay.UniqueEventCount == 1 && replay.CompletedMockDeliveries == 1);
    var older = restarted.Process(Sample(starting, t0 + 90_000));
    Check("out-of-order observation does not reset confirmed event",
        older.EventKinds.Count == 0 && older.UniqueEventCount == 1);
    var other = valid.Replace("provider-account-1", "provider-account-2");
    restarted.Process(Sample(starting.Replace("provider-account-1", "provider-account-2"), t0));
    restarted.Process(Sample(other, t0 + 60_000));
    var otherConfirmed = restarted.Process(Sample(other, t0 + 120_000));
    var accountFolders = Directory.GetDirectories(Path.Combine(temporaryRoot, "usage-monitor"));
    Check("combined monitor history is recent-first and limited",
        restarted.ReadHistory().Count == 2 && restarted.ReadHistory(1).Count == 1 &&
        restarted.ReadHistory(0).Count == 0);
    Check("two accounts have separate persistent ledgers",
        otherConfirmed.EventKinds.SequenceEqual(new[] { "CREDIT_INCREASE" }) &&
        accountFolders.Length == 2);
    Check("provider IDs do not appear in account directory names",
        accountFolders.All(folder => !folder.Contains("provider-account-")));
    var stateFolder = accountFolders.Single(folder =>
        File.ReadAllText(Path.Combine(folder, "state.json")).Contains("provider-account-1"));
    File.WriteAllText(Path.Combine(stateFolder, "state.json.tmp"), "interrupted mock write");
    var recovered = restarted.Process(Sample(valid, t0 + 240_000));
    Check("orphaned temp file recovery keeps committed ledger",
        recovered.EventKinds.Count == 0 && recovered.UniqueEventCount == 1 &&
        !File.Exists(Path.Combine(stateFolder, "state.json.tmp")));
    var unknownWorker = new LocalMonitorWorker(Path.Combine(temporaryRoot, "unknown"));
    var unknown = valid.Replace("\"planType\":\"plus\"", "\"planType\":null")
        .Replace("\"ordinaryUsageAllowed\":true", "\"ordinaryUsageAllowed\":null");
    unknownWorker.Process(Sample(unknown.Replace("\"availableCount\":2", "\"availableCount\":1"), t0));
    unknownWorker.Process(Sample(unknown, t0 + 60_000));
    var unverified = unknownWorker.Process(Sample(unknown, t0 + 120_000));
    Check("unknown live plan cannot produce an alert",
        unverified.EventKinds.Count == 0 && unverified.MockAlertMessages.Count == 0);
}
finally { if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true); }

await SwitcherLocalPipeSenderTests.RunAsync(Check);
IntegratedMiniBarStateTests.Run(Check);

Console.WriteLine($"MONITOR_SYNTHETIC_PREVIEW_PASS checks={checks} realCodexReads=0 actualAccountSwitches=0 telegramSends=0");
