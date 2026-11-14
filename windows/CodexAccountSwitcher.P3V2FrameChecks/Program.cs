using System.Text.Json;
using CodexAccountSwitcher.Core;

// Synthetic Windows C# V2 end-to-end producer, no network or real account.
// The Node V2 reviewer consumes its exact emitted JSON.
var when = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
var id = Guid.Parse("12345678-0000-4000-8000-000000000001");
var other = Guid.Parse("12345678-0000-4000-8000-000000000002");
var row = new AccountRow(new Profile(id, "Synthetic profile", null), "S",
    new Usage(62, when.AddDays(2), 85, when.AddHours(1), 2), null);
var snapshot = new AccountSnapshot([row], id, new Preferences(),
    false, false, true, null, []);
var nonweekly = new AccountRow(new Profile(id,"Synthetic Free 30D",null),
    "F",null,null,"loaded",null,
    new NonWeeklyUsage(40,43_200,when.AddDays(25),2,when));
var freeSnapshot = snapshot with { Accounts = [nonweekly] };

var tests=0;
void Confirm(bool result, string message)
{
    if (!result) throw new InvalidOperationException("P3_V2_TEST_FAIL "+message);
    tests++;
}
CodexMonitorWireSample Sample(long offset,
    string account="synthetic-account",
    int? count=2, bool nonweeklyInput=false, bool includeCreditDates=true,
    Guid? profile=null)
{
    object? creditList = includeCreditDates
        ? new object[]
        {
            new { status="available", expiresAt=when.AddDays(3).ToUnixTimeSeconds() },
            new { status="available", expiresAt=when.AddDays(2).ToUnixTimeSeconds() }
        }
        : null;
    var raw = JsonSerializer.Serialize(new
    {
        accountId=account,
        rateLimitsByLimitId=new
        {
            codex=new
            {
                primary=nonweeklyInput ? null : (object?)new
                {
                    windowDurationMins=300,usedPercent=15,
                    resetsAt=when.AddHours(1).ToUnixTimeSeconds()
                },
                secondary=nonweeklyInput ? null : (object?)new
                {
                    windowDurationMins=10080,usedPercent=38,
                    resetsAt=when.AddDays(2).ToUnixTimeSeconds()
                }
            }
        },
        rateLimitResetCredits=new { availableCount=count, credits=creditList }
    });
    return new CodexMonitorWireSample(
        when.AddMilliseconds(offset).ToUnixTimeMilliseconds(),raw,profile??id);
}
string Frame(VerifiedActiveQuotaV2FrameEncoder encoder,
    CodexMonitorWireSample sample, string label)
{
    Confirm(encoder.TryBuild(sample,when,out var json)&&json is not null,
        label+" accepted");
    using var document=JsonDocument.Parse(json!);
    var root=document.RootElement;
    Confirm(root.GetProperty("schemaVersion").GetInt32()==2 &&
        root.GetProperty("sourceDeviceId").GetString()=="windows-b",
        label+" V2 enrolled source");
    Confirm(!json!.Contains("synthetic-account",StringComparison.Ordinal) &&
        !json.Contains("Synthetic profile",StringComparison.Ordinal),
        label+" raw account and profile absent");
    Confirm(root.GetProperty("actualSendAuthorized").ValueKind==
            JsonValueKind.False &&
        root.GetProperty("senderTransitionAuthorized").ValueKind==
            JsonValueKind.False, label+" no action authority");
    Console.WriteLine(label+" "+json);
    return json;
}

var encoder=new VerifiedActiveQuotaV2FrameEncoder("windows-b");
Confirm(!encoder.TryBuild(Sample(-1000),when,out _),
    "no active profile snapshot rejects outbound telemetry");
encoder.ObserveSnapshot(snapshot,when.AddSeconds(-5));
Confirm(!encoder.TryBuild(Sample(-1000,profile:other),when,out _),
    "wrong selected profile is rejected");
var weekly=Sample(-1000);
Frame(encoder,weekly,"WEEKLY");
Confirm(!encoder.TryBuild(weekly,when,out _),"duplicate weekly sample rejected");

var zeroSample=Sample(-500,count:0,includeCreditDates:false);
var zero=Frame(encoder,zeroSample,"ZERO");
using(var zeroDoc=JsonDocument.Parse(zero))
{
    var credit=zeroDoc.RootElement.GetProperty("resetCredits");
    Confirm(credit.GetProperty("availableCount").GetInt32()==0 &&
            credit.GetProperty("expiryDetailsUtcMs").ValueKind==JsonValueKind.Null,
        "zero count cannot inherit previously available expiries");
}

Confirm(!encoder.TryBuild(Sample(0,"different-account"),when,out _),
    "provider account changed -> reject");
Confirm(encoder.IsQuarantined,"provider account change quarantined");
Confirm(!encoder.TryBuild(Sample(100,"synthetic-account"),when,out _),
    "quarantine remains closed");

var free=new VerifiedActiveQuotaV2FrameEncoder("windows-b");
free.ObserveSnapshot(freeSnapshot,when.AddSeconds(-5));
var rOnly=Sample(-1000,nonweeklyInput:true);
var only=Frame(free,rOnly,"R_ONLY");
using(var doc=JsonDocument.Parse(only))
{
    var f=doc.RootElement;
    Confirm(f.GetProperty("fiveHour").GetProperty("remainingPercent").ValueKind==
            JsonValueKind.Null &&
        f.GetProperty("sevenDay").GetProperty("remainingPercent").ValueKind==
            JsonValueKind.Null &&
        f.GetProperty("resetCredits").GetProperty("availableCount").GetInt32()==2,
        "R-only Free 30D has no invented 7D");
}
Confirm(!free.TryBuild(Sample(0,count:null,nonweeklyInput:true),when,out _),
    "all quota and R unknown is rejected");
free.SwitchRequested(when.AddSeconds(1));
Confirm(!free.TryBuild(Sample(2000,nonweeklyInput:true),
    when.AddSeconds(2),out _),"pending switch blocks dispatch");
var mutating=freeSnapshot with { IsMutating=true };
free.ObserveSnapshot(mutating,when.AddSeconds(2));
free.ObserveSnapshot(freeSnapshot,when.AddSeconds(3));
Confirm(free.TryBuild(Sample(3500,nonweeklyInput:true),
    when.AddSeconds(4),out _),"new verified active epoch can recover");

Console.Error.WriteLine($"P3_WINDOWS_V2_PRODUCER_CHECKS_PASS={tests}");
Console.Error.WriteLine("REAL_CODEX_READS=0 NETWORK_SENDS=0 TELEGRAM_SENDS=0");
