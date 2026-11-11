using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;

// Real V2 encoder output -> production HTTPS publisher implementation ->
// injected in-memory HTTP transport. No external URL is ever connected.
var clock = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
var profileId = Guid.Parse("12345678-0000-4000-8000-000000000001");
var accountRow = new AccountRow(
    new Profile(profileId, "Synthetic", null), "S",
    new Usage(62, clock.AddDays(2), 85, clock.AddHours(1), 2), null);
var active = new AccountSnapshot(
    [accountRow], profileId, new Preferences(), false, false, true, null, []);
var rawProvider = JsonSerializer.Serialize(new
{
    accountId = "synthetic-for-test-only",
    rateLimitsByLimitId = new
    {
        codex = new
        {
            primary = new
            {
                windowDurationMins = 300,
                usedPercent = 15,
                resetsAt = clock.AddHours(1).ToUnixTimeSeconds()
            },
            secondary = new
            {
                windowDurationMins = 10080,
                usedPercent = 38,
                resetsAt = clock.AddDays(2).ToUnixTimeSeconds()
            }
        }
    },
    rateLimitResetCredits = new
    {
        availableCount = 2,
        credits = new[]
        {
            new {status="available", expiresAt=clock.AddDays(2).ToUnixTimeSeconds()},
            new {status="available", expiresAt=clock.AddDays(3).ToUnixTimeSeconds()}
        }
    }
});
var encoder = new VerifiedActiveQuotaV2FrameEncoder("windows-b");
encoder.ObserveSnapshot(active, clock.AddSeconds(-5));
var sample = new CodexMonitorWireSample(
    clock.AddMilliseconds(-1000).ToUnixTimeMilliseconds(), rawProvider, profileId);
if (!encoder.TryBuild(sample, clock, out var frame) || frame is null)
    throw new InvalidOperationException("Synthetic source V2 frame unavailable");
var checks = 0;
void Check(bool condition, string title)
{
    if (!condition) throw new InvalidOperationException("P3_V2_PUBLISHER_FAIL " + title);
    checks++;
}
const string sourceId = "windows-b";
string Credential() => new string('s',48);
var issuerCalls = 0;
ValueTask<string?> Issue(CancellationToken _)
{
    issuerCalls++;
    return ValueTask.FromResult<string?>(Credential());
}
var origin = new Uri("https://synthetic.invalid/");
try
{
    using var invalid = new QuotaRelayV2HttpsPublisher(
        new Uri("http://synthetic.invalid/"), sourceId, Issue);
    throw new InvalidOperationException("HTTP origin wrongly accepted");
}
catch (ArgumentException) { Check(true,"HTTP endpoint denied"); }
try
{
    using var invalid = new QuotaRelayV2HttpsPublisher(
        new Uri("https://synthetic.invalid/something"), sourceId, Issue);
    throw new InvalidOperationException("subpath wrongly accepted");
}
catch (ArgumentException) { Check(true,"subpath origin denied"); }
try
{
    using var invalid = new QuotaRelayV2HttpsPublisher(
        origin, "wrong/source", Issue);
    throw new InvalidOperationException("invalid source wrongly accepted");
}
catch (ArgumentException) { Check(true,"invalid device denied"); }

var calls = 0;
string? sentBody = null;
string? sentPath = null;
string? sentAuth = null;
string? sentContentType = null;
long? sentBodyLength = null;
string? sentMethod = null;
using (var fake = new RecordingHandler(async (req,ct) =>
{
    calls++;
    sentBody = await req.Content!.ReadAsStringAsync(ct);
    sentPath = req.RequestUri!.AbsolutePath;
    sentAuth = req.Headers.Authorization?.Parameter;
    sentMethod = req.Method.Method;
    sentContentType = req.Content.Headers.ContentType?.ToString();
    sentBodyLength = req.Content.Headers.ContentLength;
    return new HttpResponseMessage(HttpStatusCode.Accepted);
}))
using (var publisher = new QuotaRelayV2HttpsPublisher(
    origin, sourceId, Issue, fake))
{
    Check(await publisher.PublishOnceAsync(frame),"synthetic 202 accepted");
    Check(calls==1,"one HTTP request");
    Check(sentPath=="/v2/quota/windows-b" && sentMethod=="PUT",
        "exact PUT endpoint");
    Check(sentAuth==Credential(),"ephemeral credential delivered only to HTTP");
    Check(sentContentType=="application/json","Content-Type exactly JSON");
    Check(sentBodyLength is > 0 and <= 2048,"body bounded by strict relay");
    Check(sentBody==frame,"payload matches verified V2 source byte-for-byte");
    Check(!sentBody!.Contains("synthetic-for-test-only",StringComparison.Ordinal),
        "raw provider ID redacted in relay frame");
    Check(issuerCalls==1,"credentials requested for actual send only");

    var forbidden = frame.Replace("\"schemaVersion\":2",
        "\"schemaVersion\":1",StringComparison.Ordinal);
    Check(!await publisher.PublishOnceAsync(forbidden),"V1 downgrade rejected");
    Check(calls==1&&issuerCalls==1,"V1 denial never touches auth or transport");
    var wrongSource = frame.Replace("\"windows-b\"",
        "\"macmini-a\"",StringComparison.Ordinal);
    Check(!await publisher.PublishOnceAsync(wrongSource),"source mismatch denied");
    Check(calls==1&&issuerCalls==1,"source mismatch denied before token access");
    var privileged = frame.Replace("\"senderTransitionAuthorized\":false",
        "\"senderTransitionAuthorized\":true",StringComparison.Ordinal);
    Check(!await publisher.PublishOnceAsync(privileged),
        "sender capability cannot be elevated by payload");
    var duplicate = frame.Replace("\"schemaVersion\":2",
        "\"schemaVersion\":2,\"schemaVersion\":2",StringComparison.Ordinal);
    Check(!await publisher.PublishOnceAsync(duplicate),"duplicate keys rejected");
    Check(!await publisher.PublishOnceAsync(new string('X',2050)),
        "oversized payload rejected");
    Check(calls==1,"all invalid frames blocked before HTTP");
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    Check(!await publisher.PublishOnceAsync(frame,canceled.Token),
        "already cancelled epoch denied");
    Check(calls==1,"cancelled epoch never hits HTTP");
}

using (var fake401 = new RecordingHandler((_,_) =>
    Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))))
using (var sender = new QuotaRelayV2HttpsPublisher(
    origin, sourceId, Issue, fake401))
{
    Check(!await sender.PublishOnceAsync(frame),"401 never treated as success");
    Check(fake401.Count==1,"failed delivery attempts only once");
}
using (var fake302 = new RecordingHandler((_,_) =>
    Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect))))
using (var sender = new QuotaRelayV2HttpsPublisher(
    origin, sourceId, Issue, fake302))
{
    Check(!await sender.PublishOnceAsync(frame),"redirect is denied");
    Check(fake302.Count==1,"redirect does not trigger retry");
}

// The first asynchronous request occupies the single flight and can be
// cancelled when an account-switch epoch expires. No second PUT is queued.
var entered = new TaskCompletionSource<bool>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource<bool>(
    TaskCreationOptions.RunContinuationsAsynchronously);
using (var pending = new RecordingHandler(async (_,ct) =>
{
    entered.TrySetResult(true);
    await release.Task.WaitAsync(ct);
    return new HttpResponseMessage(HttpStatusCode.Accepted);
}))
using (var sender = new QuotaRelayV2HttpsPublisher(
    origin, sourceId, Issue, pending))
using (var epoch = new CancellationTokenSource())
{
    var first = sender.PublishOnceAsync(frame,epoch.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(!await sender.PublishOnceAsync(frame),"concurrent PUT not queued");
    Check(pending.Count==1,"only one request in flight");
    epoch.Cancel();
    Check(!await first,"account epoch cancellation rejects in-flight success");
    Check(pending.Count==1,"cancelled request has no automatic retry");
}

// Disposing the sender cancels any pending transport and disallows all later requests.
var started = new TaskCompletionSource<bool>(
    TaskCreationOptions.RunContinuationsAsynchronously);
using (var pending = new RecordingHandler(async (_,ct) =>
{
    started.TrySetResult(true);
    await Task.Delay(30_000,ct);
    return new HttpResponseMessage(HttpStatusCode.Accepted);
}))
{
    var sender = new QuotaRelayV2HttpsPublisher(origin,sourceId,Issue,pending);
    var first = sender.PublishOnceAsync(frame);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    sender.Dispose();
    Check(!await first,"disposal cancels in-flight PUT");
    Check(!await sender.PublishOnceAsync(frame),"disposed publisher remains off");
    Check(pending.Count==1,"dispose never retries network");
}

Console.WriteLine("P3_WINDOWS_V2_HTTPS_PUBLISHER_CHECKS_PASS="+checks);
Console.WriteLine("REAL_CODEX_CALLS=0 EXTERNAL_NETWORK=0 TELEGRAM=0 APP_ACTIVATION=0");

sealed class RecordingHandler(
    Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    public int Count {get;private set;}
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,CancellationToken cancellationToken)
    {
        Count++;
        if (request.RequestUri?.Scheme!="https"||request.Method!=HttpMethod.Put)
            throw new InvalidOperationException("Synthetic PUT only");
        return respond(request,cancellationToken);
    }
}
