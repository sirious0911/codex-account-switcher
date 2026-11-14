using System.Text.Json;
using CodexAccountSwitcher.Core;

// A completely synthetic, non-account-accessing Windows .NET V1 producer.
// A separate integration test sends its exact output to the Node HTTPS relay.
// This program never opens a network connection, reads credentials, or sends Telegram.
var now=DateTimeOffset.UtcNow;
var profileId=Guid.Parse("12345678-0000-4000-8000-000000000001");
var row=new AccountRow(
    new Profile(profileId,"Synthetic Windows Account",null),
    "W",
    new Usage(62,now.AddDays(2),85,now.AddHours(1)),
    null);
var activeSnapshot=new AccountSnapshot(
    [row],profileId,new Preferences(),false,false,true,null,[]);
var encoder=new VerifiedActiveQuotaFrameEncoder("windows-b");
encoder.ObserveSnapshot(activeSnapshot,now.AddSeconds(-2));

var observed=now.AddMilliseconds(-500).ToUnixTimeMilliseconds();
var sampleJson=JsonSerializer.Serialize(new
{
    accountId="synthetic-windows-account",
    rateLimitsByLimitId=new
    {
        codex=new
        {
            primary=new
            {
                windowDurationMins=300,
                usedPercent=15,
                resetsAt=now.AddHours(1).ToUnixTimeSeconds()
            },
            secondary=new
            {
                windowDurationMins=10080,
                usedPercent=38,
                resetsAt=now.AddDays(2).ToUnixTimeSeconds()
            }
        }
    }
});
var sample=new CodexMonitorWireSample(observed,sampleJson,profileId);
if(!encoder.TryBuild(sample,now,out var frame)||frame is null)
    throw new InvalidOperationException("Synthetic verified active frame was rejected.");
if(encoder.TryBuild(sample,now,out _))
    throw new InvalidOperationException("Duplicate synthetic observation was accepted.");
Console.WriteLine(frame);
