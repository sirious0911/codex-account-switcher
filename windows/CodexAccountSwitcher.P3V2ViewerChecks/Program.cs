using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;

const long Clock = 1_800_000_000_000;
const string SourceMac = "macmini-a";
const string SourceWindows = "windows-b";
var checks = 0;
void Must(bool yes,string description)
{
    if (!yes) throw new InvalidOperationException("P3_V2_VIEWER_FAIL: " + description);
    checks++;
}

string Frame(string source, string account, long sequence, long collected,
    int? credits = 2, object? details = null, bool free = false,
    bool invalidAuthority = false)
{
    var expiry = details ?? new long?[] { Clock + 200_000_000, Clock + 300_000_000 };
    return JsonSerializer.Serialize(new
    {
        schemaVersion=2,
        status="FRESH_READ_ONLY",
        frame=new {
            sourceDeviceId=source,
            sourceSessionId="a".PadRight(32,'a'),
            sequence,
            collectedAtUtcMs=collected,
            accountIdSha256=account.PadRight(64,account[0]),
            fiveHour = new {
                remainingPercent=free ? (int?)null : 85,
                resetAtUtcMs=free ? (long?)null : collected+3_600_000
            },
            sevenDay = new {
                remainingPercent=free ? (int?)null : 62,
                resetAtUtcMs=free ? (long?)null : collected+172_800_000
            },
            resetCredits=new {
                availableCount=credits,
                expiryDetailsUtcMs=expiry
            }
        },
        actualSendAuthorized=invalidAuthority,
        automaticRetryAllowed=false,
        productionReservationAuthorized=false,
        senderTransitionAuthorized=false
    });
}

var scripted=new FixtureHttp();
using var http=new HttpClient(scripted);
using var mac=new QuotaRelayV2HttpsViewer(
    new Uri("https://fixture.invalid/"),SourceMac,new string('m',48),http);
var now=DateTimeOffset.FromUnixTimeMilliseconds(Clock);
var first=Frame(SourceMac,"b",1,Clock-1000,
    details:new long?[]{Clock+300_000_000,Clock+200_000_000});
scripted.Reply(first);
var initial=await mac.FetchAsync(now);
Must(initial.Status=="FRESH_READ_ONLY","fresh V2 GET accepted");
var snapshot=initial.Snapshot ?? throw new InvalidOperationException("Missing initial snapshot");
Must(snapshot is not null,"initial snapshot");
Must(snapshot!.ResetCreditsRemaining==2,"R count is 2");
Must(snapshot!.ResetCreditExpiries is {Count:2},"two expiry slots");
Must(snapshot!.ResetCreditExpiries![0]!.Value.ToUnixTimeMilliseconds()==Clock+200_000_000,
    "R1 date sorted ascending");
Must(snapshot!.ResetCreditExpiries[1]!.Value.ToUnixTimeMilliseconds()==Clock+300_000_000,
    "R2 date sorted ascending");
Must(snapshot!.FiveHourRemainingPercent==85&&snapshot!.SevenDayRemainingPercent==62,
    "5H 7D values preserved");
Must(!initial.TelegramAllowed&&!initial.AccountVerified&&!initial.ActualSendAuthorized,
    "untrusted remote data never grants account/Telegram actions");
Must(scripted.GetCount==1&&scripted.LastPath=="/v2/quota/macmini-a",
    "exact read-only V2 route");
Must(scripted.LastBearer==new string('m',48),"viewer credential used only for GET");

scripted.Reply(first);
var repeated=await mac.FetchAsync(now.AddMinutes(3));
Must(repeated.Status=="AGED_READ_ONLY","same frame becomes aged");
Must(repeated.Snapshot?.ResetCreditsRemaining==2,"age does not invent count");

var zero=Frame(SourceMac,"b",2,Clock-300,0,JsonDocument.Parse("null").RootElement);
scripted.Reply(zero);
var after=await mac.FetchAsync(now);
Must(after.Snapshot?.ResetCreditsRemaining==0,"next observation R0");
Must(after.Snapshot?.ResetCreditExpiries is null,"no cached expired R1/R2");

scripted.Reply(Frame(SourceMac,"c",3,Clock-200));
var switched=await mac.FetchAsync(now);
Must(switched.Status=="HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET"&&
    switched.Snapshot is null,"provider account changed -> quarantine");
var calls=scripted.GetCount;
var quarantined=await mac.FetchAsync(now);
Must(quarantined.Snapshot is null&&scripted.GetCount==calls,
    "quarantine blocks further GET until new explicit enrollment");

var otherQueue=new FixtureHttp();
using var otherHttp=new HttpClient(otherQueue);
using var win=new QuotaRelayV2HttpsViewer(
    new Uri("https://fixture.invalid/"),SourceWindows,new string('w',48),otherHttp);
var free=Frame(SourceWindows,"d",1,Clock-1000,2,JsonDocument.Parse("null").RootElement,true);
otherQueue.Reply(free);
var freeR=await win.FetchAsync(now);
Must(freeR.Snapshot?.ResetCreditsRemaining==2,"R-only count accepted");
Must(freeR.Snapshot?.ResetCreditExpiries is null,"Free expiry unknown not R0");
Must(freeR.Snapshot?.FiveHourRemainingPercent is null&&
    freeR.Snapshot?.SevenDayRemainingPercent is null,"Free 30D not invented as 7D");

otherQueue.Reply(Frame(SourceWindows,"d",2,Clock-500,0,
    new long?[]{Clock+200_000_000},true));
Must((await win.FetchAsync(now)).Snapshot is null,"R0 with expiry rejected");
otherQueue.Reply(Frame(SourceWindows,"d",2,Clock-500,2,null,true)
    .Replace("\"schemaVersion\":2",
      "\"schemaVersion\":2,\"schemaVersion\":2",StringComparison.Ordinal));
Must((await win.FetchAsync(now)).Snapshot is null,"duplicate schemaVersion rejected");
otherQueue.Reply(Frame(SourceWindows,"d",2,Clock-500,2,null,true,true));
Must((await win.FetchAsync(now)).Snapshot is null,"privileged flags rejected");
otherQueue.Reply(Frame(SourceWindows,"d",2,Clock-500,2,null,true));
var correct=await win.FetchAsync(now);
Must(correct.Snapshot?.ResetCreditsRemaining==2,"invalid attempts leave state intact");

otherQueue.Reply(Frame(SourceWindows,"d",3,Clock-360_000,2,null,true));
var stale=await win.FetchAsync(now);
Must(stale.Snapshot is null,"stale six-minute frame hidden");
otherQueue.Reply(Frame(SourceWindows,"d",3,Clock-200,2,null,true));
Must((await win.FetchAsync(now)).Snapshot is not null,
    "valid frame after invalid attempt still accepted");
otherQueue.Reply(Frame(SourceWindows,"d",4,Clock-100));
Must((await win.FetchAsync(now.AddMinutes(6))).Snapshot is null,
    "display freshness not extended by cached server frame");

using var invalidHttp=new HttpClient(new FixtureHttp());
using var invalid=new QuotaRelayV2HttpsViewer(
    new Uri("https://fixture.invalid/"),SourceMac,new string('n',48),invalidHttp);
Must((await invalid.FetchAsync(now)).Snapshot is null,"no remote response -> unknown");

Console.WriteLine("P3_WINDOWS_V2_VIEWER_READ_ONLY_CHECKS_PASS="+checks);
Console.WriteLine("ACTUAL_CODEX_READS=0 ACTUAL_HTTP_EXTERNAL=0 TELEGRAM_SENDS=0");

sealed class FixtureHttp : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Code,string Json)> replies = new();
    public int GetCount {get;private set;}
    public string? LastPath {get;private set;}
    public string? LastBearer {get;private set;}
    public void Reply(string json, HttpStatusCode code=HttpStatusCode.OK)
        => replies.Enqueue((code,json));
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,CancellationToken cancellationToken)
    {
        if(request.Method!=HttpMethod.Get||request.RequestUri?.Scheme!="https")
            throw new InvalidOperationException("ONLY_SYNTHETIC_GET_IS_ALLOWED");
        GetCount++;
        LastPath=request.RequestUri.AbsolutePath;
        LastBearer=request.Headers.Authorization?.Parameter;
        var reply=replies.Count>0?replies.Dequeue():
            (HttpStatusCode.ServiceUnavailable,"{}");
        return Task.FromResult(new HttpResponseMessage(reply.Item1){
            Content=new StringContent(reply.Item2,Encoding.UTF8,"application/json")
        });
    }
}
