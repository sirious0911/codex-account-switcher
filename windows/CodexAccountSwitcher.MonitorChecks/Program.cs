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

Console.WriteLine($"MONITOR_SYNTHETIC_PREVIEW_PASS checks={checks} realCodexReads=0 actualAccountSwitches=0 telegramSends=0");
