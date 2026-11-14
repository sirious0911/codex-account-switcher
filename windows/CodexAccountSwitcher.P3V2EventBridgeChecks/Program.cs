using System.Text.Json;
using CodexAccountSwitcher.Core;

// Synthetic event-stream integration: actual IVerifiedQuotaEventSource
// Changed/MonitorObserved/SwitchRequested event wiring, but no SwiftHost,
// network, credentials, operating app, Telegram, file access or retries.
var when=DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
var clock=when;
var profileId=Guid.Parse("12345678-0000-4000-8000-000000000001");
var wrongId=Guid.Parse("12345678-0000-4000-8000-000000000002");
AccountRow Row(Guid id) =>
    new(new Profile(id,"Synthetic",null),"S",
        new Usage(62,when.AddDays(2),85,when.AddHours(1),2),null);
AccountSnapshot State(bool confirmed=true,bool mutating=false,Guid? active=null) =>
    new([Row(profileId)],active??profileId,new Preferences(),
        mutating,false,confirmed,null,[]);
var fake=new FakeEvents(State(confirmed:false));
var received=new List<string>();
var checkedCount=0;
void Verify(bool valid,string description)
{
    if(!valid)throw new InvalidOperationException("V2_EVENT_BRIDGE_FAIL "+description);
    checkedCount++;
}
string Frame(long delta,Guid? id=null,int credits=2,string account="synthetic-event-account")
{
    return JsonSerializer.Serialize(new{
        accountId=account,
        rateLimitsByLimitId=new {codex=new {
            primary=new { windowDurationMins=300,usedPercent=15,
                resetsAt=when.AddHours(1).ToUnixTimeSeconds()},
            secondary=new {windowDurationMins=10080,usedPercent=38,
                resetsAt=when.AddDays(2).ToUnixTimeSeconds()}
        }},
        rateLimitResetCredits=new {
            availableCount=credits, credits=credits==0?null:new object[]{
                new {status="available",expiresAt=when.AddDays(2).ToUnixTimeSeconds()},
                new {status="available",expiresAt=when.AddDays(3).ToUnixTimeSeconds()}
            }
        }
    });
}
void Observe(long delta,Guid? id=null,int credits=2,string account="synthetic-event-account")
{
    fake.Observe(new CodexMonitorWireSample(
        when.AddMilliseconds(delta).ToUnixTimeMilliseconds(),
        Frame(delta,id,credits,account),id??profileId));
}

using(var bridge=new VerifiedQuotaV2EventBridge(fake,"windows-b",()=>clock))
{
    bridge.FrameReady+=received.Add;
    Observe(-1000);
    Verify(received.Count==0,"unverified active identity rejects event");
    clock=when.AddSeconds(-5);
    fake.SetState(State(),true);
    clock=when;
    Observe(-1000,wrongId);
    Verify(received.Count==0,"wrong account profile rejected");
    Observe(-1000);
    Verify(received.Count==1,"new verified observation emits V2 frame");
    using(var doc=JsonDocument.Parse(received[0]))
    {
        var root=doc.RootElement;
        Verify(root.GetProperty("schemaVersion").GetInt32()==2,"actual V2 schema");
        Verify(root.GetProperty("sourceDeviceId").GetString()=="windows-b",
            "source binding");
        Verify(root.GetProperty("resetCredits").GetProperty("availableCount").GetInt32()==2,
            "R count from event");
        Verify(root.GetProperty("resetCredits").GetProperty("expiryDetailsUtcMs")
            .GetArrayLength()==2,"R1 R2 detail from event");
        Verify(root.GetProperty("actualSendAuthorized").ValueKind==JsonValueKind.False,
            "no action permissions");
        Verify(!received[0].Contains("synthetic-event-account",StringComparison.Ordinal),
            "no raw provider ID or email");
    }
    Observe(-1000);
    Verify(received.Count==1,"same event replay discarded");
    clock=when.AddMilliseconds(200);
    Observe(-500,credits:0);
    Verify(received.Count==2,"next event emits R0");
    using(var doc=JsonDocument.Parse(received[1]))
        Verify(doc.RootElement.GetProperty("resetCredits")
            .GetProperty("expiryDetailsUtcMs").ValueKind==JsonValueKind.Null,
            "R0 clears previously shown R expiry slots");
    clock=when.AddMilliseconds(400);
    Observe(-250,account:"different-account");
    Verify(received.Count==2,"provider account change quarantined");
    clock=when.AddSeconds(1);
    Observe(500);
    Verify(received.Count==2,"quarantined event suppressed");
    Verify(bridge.FramesProduced==2,"exactly two outputs and no retry");
    fake.RequestSwitch();
    Observe(500);
    Verify(received.Count==2,"pending switch blocks event");
    fake.SetState(State(mutating:true),true);
    fake.SetState(State(),true);
    clock=when.AddSeconds(5);
    Observe(4200);
    Verify(received.Count==3,"verified new epoch recovers");
    fake.SetState(State(confirmed:false),true);
    clock=when.AddSeconds(6);
    Observe(5200);
    Verify(received.Count==3,"identity loss blocks subsequent events");
}
fake.SetState(State(),true);
fake.Observe(new CodexMonitorWireSample(
    when.AddMilliseconds(7000).ToUnixTimeMilliseconds(),
    Frame(7000),profileId));
Verify(received.Count==3,"dispose removes all subscriptions");

Console.WriteLine("P3_WINDOWS_V2_SWIFTHOST_EVENT_BRIDGE_CHECKS_PASS="+checkedCount);
Console.WriteLine("ACTUAL_CODEX_ACCESSES=0 HTTP_PUT=0 TELEGRAM=0 OPERATING_BINDING=0");

sealed class FakeEvents(AccountSnapshot first) : IVerifiedQuotaEventSource
{
    public AccountSnapshot State {get;private set;}=first;
    public event Action? Changed;
    public event Action<CodexMonitorWireSample>? MonitorObserved;
    public event Action? SwitchRequested;
    public void SetState(AccountSnapshot next,bool emit)
    {
        State=next;
        if(emit)Changed?.Invoke();
    }
    public void Observe(CodexMonitorWireSample x)=>MonitorObserved?.Invoke(x);
    public void RequestSwitch()=>SwitchRequested?.Invoke();
}
