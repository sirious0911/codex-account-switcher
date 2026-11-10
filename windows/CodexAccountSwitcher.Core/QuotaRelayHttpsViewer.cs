using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// P3 HTTPS client: read-only authenticated 5H/7D viewing candidate.
/// Does not install itself into the operating taskbar, read local credentials,
/// authorize Sender, or deliver Telegram. TLS certificate validation is NEVER bypassed.
/// </summary>
public sealed class QuotaRelayHttpsViewer : IDisposable
{
    private const int MaxEnvelopeBytes=4096;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly Uri endpoint;
    private readonly string sourceDeviceId;
    private readonly string viewerToken;
    private readonly QuotaViewV1MemoryCache cache;

    public QuotaRelayHttpsViewer(Uri serverUrl,string sourceDeviceId,
        string viewerToken,HttpClient? httpClient=null)
    {
        if(serverUrl is null || !serverUrl.IsAbsoluteUri ||
           !string.Equals(serverUrl.Scheme,Uri.UriSchemeHttps,StringComparison.Ordinal) ||
           !string.IsNullOrEmpty(serverUrl.UserInfo) ||
           !string.IsNullOrEmpty(serverUrl.Query) ||
           !string.IsNullOrEmpty(serverUrl.Fragment) ||
           serverUrl.AbsolutePath!="/" ||
           string.IsNullOrWhiteSpace(serverUrl.DnsSafeHost))
            throw new ArgumentException("A bare HTTPS relay origin is required.",nameof(serverUrl));
        if(string.IsNullOrEmpty(sourceDeviceId) ||sourceDeviceId.Length>48 ||
           sourceDeviceId.Any(x=>!(x is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                  or >= '0' and <= '9' or '-')))
            throw new ArgumentException("Invalid enrolled source device.",nameof(sourceDeviceId));
        if(string.IsNullOrEmpty(viewerToken) ||viewerToken.Length is <32 or >256 ||
           viewerToken.Any(x=>!(x is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                 or >= '0' and <= '9' or '_' or '-')))
            throw new ArgumentException("An enrolled viewer token is required.",nameof(viewerToken));
        endpoint=new Uri(serverUrl,"v1/quota/"+sourceDeviceId);
        this.sourceDeviceId=sourceDeviceId;
        this.viewerToken=viewerToken;
        cache=new QuotaViewV1MemoryCache([sourceDeviceId]);
        ownsHttp=httpClient is null;
        http=httpClient??new HttpClient(new HttpClientHandler {
            AllowAutoRedirect=false,UseCookies=false,
            ServerCertificateCustomValidationCallback=null
        }){Timeout=TimeSpan.FromSeconds(8)};
    }

    public async Task<QuotaViewerMiniBarPreview> FetchAsync(
        DateTimeOffset trustedNow,CancellationToken cancellationToken=default)
    {
        QuotaViewerMiniBarPreview Hold(string reason)=>
            QuotaViewerMiniBarOfflineAdapter.Project(
                new QuotaViewV1Inspection(reason,null),sourceDeviceId,trustedNow);
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,endpoint);
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",viewerToken);
            request.Headers.Accept.ParseAdd("application/json");
            using var response=await http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead,cancellationToken);
            if(response.StatusCode!=HttpStatusCode.OK)
                return Hold("HOLD_REMOTE_HTTP_STATUS");
            if(response.Content.Headers.ContentLength is long n && n>MaxEnvelopeBytes)
                return Hold("HOLD_REMOTE_OVERSIZED");
            await using var stream=await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memory=new MemoryStream();
            var bytes=new byte[1024];
            while(true)
            {
                var count=await stream.ReadAsync(bytes,cancellationToken);
                if(count==0)break;
                if(memory.Length+count>MaxEnvelopeBytes)
                    return Hold("HOLD_REMOTE_OVERSIZED");
                memory.Write(bytes,0,count);
            }
            using var document=JsonDocument.Parse(memory.ToArray(),new JsonDocumentOptions {
                AllowTrailingCommas=false,CommentHandling=JsonCommentHandling.Disallow,MaxDepth=8
            });
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)
                return Hold("HOLD_REMOTE_INVALID_FRAME");
            string[] expected=[
                "schemaVersion","status","frame","actualSendAuthorized",
                "automaticRetryAllowed","productionReservationAuthorized",
                "senderTransitionAuthorized"];
            var properties=root.EnumerateObject().ToArray();
            if(properties.Length!=expected.Length ||
               properties.Select(x=>x.Name).Distinct(StringComparer.Ordinal).Count()!=expected.Length||
               properties.Any(x=>!expected.Contains(x.Name,StringComparer.Ordinal)) ||
               root.GetProperty("schemaVersion").ValueKind!=JsonValueKind.Number ||
               root.GetProperty("schemaVersion").GetInt32()!=1 ||
               root.GetProperty("status").GetString() is not ("FRESH_READ_ONLY" or "AGED_READ_ONLY")||
               expected[3..].Any(key=>root.GetProperty(key).ValueKind!=JsonValueKind.False))
                return Hold("HOLD_REMOTE_INVALID_FRAME");
            var raw=root.GetProperty("frame").GetRawText();
            var inspection=cache.Ingest(raw,trustedNow.ToUnixTimeMilliseconds());
            if(inspection.Status!="OFFLINE_VIEW_STORED_CANDIDATE")
                return Hold(inspection.Status);
            return QuotaViewerMiniBarOfflineAdapter.Project(cache,sourceDeviceId,trustedNow);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { throw; }
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or
            IOException or JsonException or InvalidOperationException or FormatException or
            OverflowException or ArgumentException)
        {
            return Hold("HOLD_REMOTE_READ_FAILURE");
        }
    }

    public void Dispose()
    {
        if(ownsHttp)http.Dispose();
    }
}
