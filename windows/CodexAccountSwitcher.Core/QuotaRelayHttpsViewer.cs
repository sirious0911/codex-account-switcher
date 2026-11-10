using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// P3 read-only HTTPS quota reader. Independent of uncommitted local UI code.
/// Does not read Codex credentials, switch accounts, or grant Sender/Telegram.
/// Normal operating app does not instantiate this class until separate opt-in.
/// </summary>
public sealed class QuotaRelayHttpsViewer : IDisposable
{
    private const int MaxBytes=4096;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly Uri endpoint;
    private readonly string token;
    private readonly string sourceId;
    private string? priorAccountHash;
    private long lastTimestamp;
    private long lastSequence;
    private string? lastSession;
    private bool quarantined;
    private string? lastFingerprint;
    private QuotaRelayReadOnlySnapshot? lastSnapshot;

    public QuotaRelayHttpsViewer(Uri origin,string sourceDeviceId,
        string viewerToken,HttpClient? httpClient=null)
    {
        if(origin is null||!origin.IsAbsoluteUri||
           origin.Scheme!=Uri.UriSchemeHttps||
           !string.IsNullOrEmpty(origin.UserInfo)||
           !string.IsNullOrEmpty(origin.Query)||
           !string.IsNullOrEmpty(origin.Fragment)||
           origin.AbsolutePath!="/"||
           string.IsNullOrWhiteSpace(origin.DnsSafeHost))
            throw new ArgumentException("Only a bare HTTPS origin is supported.",nameof(origin));
        if(!ValidId(sourceDeviceId,48))
            throw new ArgumentException("Invalid source device.",nameof(sourceDeviceId));
        if(!ValidToken(viewerToken))
            throw new ArgumentException("Invalid viewer enrollment.",nameof(viewerToken));
        endpoint=new Uri(origin,"v1/quota/"+sourceDeviceId);
        sourceId=sourceDeviceId;
        token=viewerToken;
        ownsHttp=httpClient is null;
        http=httpClient??new HttpClient(new HttpClientHandler{
            AllowAutoRedirect=false,UseCookies=false
        }){Timeout=TimeSpan.FromSeconds(8)};
    }

    public async Task<QuotaRelayReadOnlyResult> FetchAsync(
        DateTimeOffset now,CancellationToken cancellationToken=default)
    {
        QuotaRelayReadOnlyResult Held(string reason)=>new(reason,null);
        if(quarantined)return Held("HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET");
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,endpoint);
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
            request.Headers.Accept.ParseAdd("application/json");
            using var response=await http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead,cancellationToken);
            if(response.StatusCode!=HttpStatusCode.OK)return Held("HOLD_REMOTE_HTTP_STATUS");
            if(response.Content.Headers.ContentLength is long size && size>MaxBytes)
                return Held("HOLD_REMOTE_OVERSIZED");
            await using var stream=await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer=new MemoryStream();
            var bytes=new byte[1024];
            while(true)
            {
                var n=await stream.ReadAsync(bytes,cancellationToken);
                if(n==0)break;
                if(buffer.Length+n>MaxBytes)return Held("HOLD_REMOTE_OVERSIZED");
                buffer.Write(bytes,0,n);
            }
            using var document=JsonDocument.Parse(buffer.ToArray(),
                new JsonDocumentOptions{MaxDepth=8});
            var root=document.RootElement;
            if(!Exact(root,["schemaVersion","status","frame","actualSendAuthorized",
                  "automaticRetryAllowed","productionReservationAuthorized",
                  "senderTransitionAuthorized"])||
               root.GetProperty("schemaVersion").GetInt32()!=1||
               root.GetProperty("status").GetString() is not ("FRESH_READ_ONLY" or "AGED_READ_ONLY")||
               new[]{"actualSendAuthorized","automaticRetryAllowed",
                       "productionReservationAuthorized","senderTransitionAuthorized"}
                   .Any(key=>root.GetProperty(key).ValueKind!=JsonValueKind.False))
                return Held("HOLD_INVALID_RESPONSE");
            var frame=root.GetProperty("frame");
            if(!Exact(frame,["sourceDeviceId","sourceSessionId","sequence",
                  "collectedAtUtcMs","accountIdSha256","fiveHour","sevenDay"])||
               frame.GetProperty("sourceDeviceId").GetString()!=sourceId)
                return Held("HOLD_INVALID_FRAME");
            var account=frame.GetProperty("accountIdSha256").GetString();
            var session=frame.GetProperty("sourceSessionId").GetString();
            if(!ValidHex(account,64)||!ValidHex(session,32)||
               !frame.GetProperty("sequence").TryGetInt64(out var sequence)||
               sequence<1||sequence>1_000_000_000||
               !frame.GetProperty("collectedAtUtcMs").TryGetInt64(out var collected)||
               collected<1_577_836_800_000||collected>4_133_980_800_000||
               collected>now.ToUnixTimeMilliseconds()+3000||
               now.ToUnixTimeMilliseconds()-collected>=360_000)
                return Held("HOLD_STALE_OR_INVALID_FRAME");
            if(priorAccountHash is not null && priorAccountHash!=account)
            {
                quarantined=true;return Held("HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET");
            }
            var fingerprint=Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(frame.GetRawText())));
            if(lastTimestamp!=0 && collected==lastTimestamp &&
               lastSession==session && lastSequence==sequence &&
               lastFingerprint==fingerprint && lastSnapshot is not null)
            {
                return new(now.ToUnixTimeMilliseconds()-collected<120_000
                    ? "FRESH_READ_ONLY":"AGED_READ_ONLY",lastSnapshot);
            }
            if(lastTimestamp!=0 && (collected<=lastTimestamp ||
               (lastSession==session && sequence<=lastSequence) ||
               (lastSession!=session && sequence!=1)))
                return Held("HOLD_REPLAY_OR_REORDERED_VIEW");
            var five=ParseWindow(frame.GetProperty("fiveHour"),collected,21_600_000);
            var seven=ParseWindow(frame.GetProperty("sevenDay"),collected,691_200_000);
            if(five is null||seven is null||
               (five.Value.Percent is null && seven.Value.Percent is null))
                return Held("HOLD_INVALID_WINDOW");
            lastTimestamp=collected;
            lastSequence=sequence;
            lastSession=session;
            lastFingerprint=fingerprint;
            priorAccountHash=account;
            // Cached source identity stays private; response never reveals account hash.
            lastSnapshot=new QuotaRelayReadOnlySnapshot(sourceId,
                five.Value.Percent,seven.Value.Percent,
                five.Value.ResetAt,seven.Value.ResetAt,
                DateTimeOffset.FromUnixTimeMilliseconds(collected));
            return new(now.ToUnixTimeMilliseconds()-collected<120_000
                ? "FRESH_READ_ONLY":"AGED_READ_ONLY",lastSnapshot);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or
            IOException or JsonException or InvalidOperationException or FormatException or
            ArgumentException or OverflowException)
        {
            return Held("HOLD_REMOTE_READ_FAILURE");
        }
    }

    private static (int? Percent,DateTimeOffset? ResetAt)? ParseWindow(
        JsonElement root,long observed,long maxAhead)
    {
        if(!Exact(root,["remainingPercent","resetAtUtcMs"]))return null;
        int? percent=null;
        var p=root.GetProperty("remainingPercent");
        if(p.ValueKind!=JsonValueKind.Null)
        {
            if(!p.TryGetInt32(out var n)||n<0||n>100)return null;
            percent=n;
        }
        DateTimeOffset? reset=null;
        var r=root.GetProperty("resetAtUtcMs");
        if(r.ValueKind!=JsonValueKind.Null)
        {
            if(!r.TryGetInt64(out var time)||time<1_577_836_800_000||
               time>4_133_980_800_000||time<observed-600_000||
               time>observed+maxAhead)return null;
            reset=DateTimeOffset.FromUnixTimeMilliseconds(time);
        }
        return (percent,reset);
    }
    private static bool Exact(JsonElement root,string[] keys)
    {
        if(root.ValueKind!=JsonValueKind.Object)return false;
        var names=root.EnumerateObject().Select(x=>x.Name).ToArray();
        return names.Length==keys.Length &&
            names.Distinct(StringComparer.Ordinal).Count()==names.Length &&
            names.All(name=>keys.Contains(name,StringComparer.Ordinal));
    }
    private static bool ValidId(string? text,int maximum)=>
        !string.IsNullOrEmpty(text)&&text.Length<=maximum &&
        text.All(c=>c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                 or >= '0' and <= '9' or '-');
    private static bool ValidToken(string? text)=>
        text is {Length:>=32 and <=256} &&
        text.All(c=>c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                 or >= '0' and <= '9' or '-' or '_');
    private static bool ValidHex(string? s,int len)=>
        s is not null && s.Length==len &&
        s.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public void Dispose(){if(ownsHttp)http.Dispose();}
}

public sealed record QuotaRelayReadOnlySnapshot(
    string SourceDeviceId,int? FiveHourRemainingPercent,int? SevenDayRemainingPercent,
    DateTimeOffset? FiveHourResetAt,DateTimeOffset? SevenDayResetAt,
    DateTimeOffset ObservedAt);

public sealed record QuotaRelayReadOnlyResult(
    string Status,QuotaRelayReadOnlySnapshot? Snapshot)
{
    public bool ActualSendAuthorized=>false;
    public bool SenderTransitionAuthorized=>false;
    public bool TelegramAllowed=>false;
    public bool AccountVerified=>false;
}
