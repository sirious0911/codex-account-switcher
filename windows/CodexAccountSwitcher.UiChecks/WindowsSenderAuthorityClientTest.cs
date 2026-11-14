using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexAccountSwitcher.Core;

internal static class WindowsSenderAuthorityClientTest
{
    private const string Device = "windows-1";
    private static readonly byte[] Key = Enumerable.Repeat((byte)0x01, 32).ToArray();
    private static readonly Uri BaseUri = new("https://authority.example.test:9443/");

    internal static void Run()
    {
        var vectorBody = "{\"epoch\":7,\"eventKey\":\"" +
            new string('c', 64) + "\",\"lane\":\"FIVE_HOUR_100\"}";
        var vectorSignature = WindowsSenderAuthorityClient.Sign(
            Key, Device, "POST", "/v1/events/reserve", 1794660000000,
            string.Concat(Enumerable.Repeat("ab", 16)), vectorBody);
        Assert(vectorSignature ==
            "9bfcf2c57e0c65e7ea35a005255820e8522fa7038720d55478c3f8c6b531ab79",
            "Windows .NET HMAC must exactly match the Node 24 authority reference.");

        Assert(!WindowsSenderAuthorityClient.ValidAuthorityUri(
            new Uri("http://authority.example.test:9443/")) &&
            !WindowsSenderAuthorityClient.ValidAuthorityUri(
            new Uri("https://user:pass@authority.example.test/")) &&
            !WindowsSenderAuthorityClient.ValidAuthorityUri(
            new Uri("https://authority.example.test/path")) &&
            WindowsSenderAuthorityClient.ValidAuthorityUri(BaseUri),
            "An Authority endpoint is explicit root-level HTTPS only.");
        AssertThrows(() => WindowsSenderAuthorityHttpsTransport.Create(
            BaseUri, "not-a-sha256-pin"),
            "Production HTTPS transport must refuse a missing or malformed TLS pin.");

        var handler = new SyntheticAuthorityHandler();
        using var http = new HttpClient(handler);
        using var client = new WindowsSenderAuthorityClient(
            BaseUri, Device, Key, http);
        var status = client.StatusAsync().GetAwaiter().GetResult();
        Assert(status.Code == "STATUS" &&
            status.Lease is { OwnerDevice: null, Epoch: 0 } &&
            status.AuthorityTimeMs is > 0 &&
            !status.ActualTelegramSendAuthorized &&
            !status.AllowsAutomaticRetries,
            "Signed status is observation only and cannot authorize Telegram.");
        var acquire = client.AcquireAsync(20000).GetAwaiter().GetResult();
        Assert(acquire.Code == "LEASE_ACQUIRED" &&
            acquire.Epoch == 7 && acquire.ExpiresAtMs is > 0 &&
            !acquire.ActualTelegramSendAuthorized,
            "An acquired shared lease is not Telegram send authorization.");
        var renew = client.RenewAsync(7, 15000).GetAwaiter().GetResult();
        Assert(renew.Code == "LEASE_RENEWED" && renew.Epoch == 7,
            "Real product renew call must use typed epoch and TTL.");
        var key = new string('c', 64);
        var reserve = client.ReserveEventAsync(
            7, key, TelegramNotificationLane.FiveHourRecovery)
            .GetAwaiter().GetResult();
        Assert(reserve.Code == "EVENT_RESERVED_NO_SEND" &&
            reserve.EventKey == key && !reserve.ActualTelegramSendAuthorized,
            "Remote immutable reservation is not consent to send Telegram.");
        var eventRecord = client.EventAsync(key).GetAwaiter().GetResult();
        Assert(eventRecord.Code == "EVENT_FOUND" &&
            eventRecord.Event is {
                Lane: "FIVE_HOUR_100", State: "RESERVED_BEFORE_SEND",
                OwnerDevice: Device, Epoch: 7
            }, "Reserved event owner, state and fencing epoch must be typed.");
        Assert(client.MarkUncertainAsync(7, key).GetAwaiter().GetResult()
                .Code == "EVENT_MARKED_UNCERTAIN",
            "An ambiguous send outcome can be permanently marked uncertain.");
        Assert(client.ReleaseAsync(7).GetAwaiter().GetResult().Code ==
                "LEASE_RELEASED",
            "Explicit release must send the matching fence epoch.");
        Assert(handler.SignedCalls == 7 &&
            handler.UniqueNonces.Count == 7 &&
            handler.TransportRequests == 7,
            "All seven API calls use unique HMAC-signed requests.");
        Assert(handler.LastReservedBody == "{\"epoch\":7,\"eventKey\":\"" +
                key + "\",\"lane\":\"FIVE_HOUR_100\"}",
            "C# JSON field layout must match Node authority's strict POST schema.");

        var count = handler.TransportRequests;
        Assert(client.AcquireAsync(50).GetAwaiter().GetResult().Code ==
                "HOLD_CLIENT_INPUT" &&
            client.ReserveEventAsync(7, "BAD",
                TelegramNotificationLane.WeeklyRecovery)
                .GetAwaiter().GetResult().Code == "HOLD_CLIENT_INPUT" &&
            handler.TransportRequests == count,
            "Malformed TTL or event ID must be rejected before any network call.");

        string SafeReply(string code, string extra) =>
            "{\"schemaVersion\":1,\"code\":\"" + code +
            "\",\"actualSendAuthorized\":false,\"automaticRetryAllowed\":false," +
            "\"remoteSenderActivated\":false," + extra + "}";

        handler.ResponseOverride = SafeReply("LEASE_RENEWED",
            "\"epoch\":8,\"expiresAtMs\":" +
            (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000));
        Assert(client.RenewAsync(7, 15000).GetAwaiter().GetResult().Code ==
                "HOLD_RESPONSE_MISMATCH",
            "A renew response for a different fencing epoch must fail closed.");

        handler.ResponseOverride = SafeReply("LEASE_RELEASED", "\"epoch\":8");
        Assert(client.ReleaseAsync(7).GetAwaiter().GetResult().Code ==
                "HOLD_RESPONSE_MISMATCH",
            "A release acknowledgment must match the requested epoch.");

        handler.ResponseOverride = SafeReply("EVENT_RESERVED_NO_SEND",
            "\"eventKey\":\"" + new string('d', 64) + "\"");
        Assert(client.ReserveEventAsync(7, key,
                TelegramNotificationLane.FiveHourRecovery)
                .GetAwaiter().GetResult().Code == "HOLD_RESPONSE_MISMATCH",
            "Authority event reservation response must match the requested event ID.");

        handler.ResponseOverride = SafeReply("STATUS",
            "\"authorityTimeMs\":" +
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
            ",\"lease\":{\"owner\":\"invalid/name\",\"epoch\":2,\"expiresAtMs\":9999999999999}");
        Assert(client.StatusAsync().GetAwaiter().GetResult().Code ==
                "HOLD_CLOCK_OR_SCHEMA",
            "Malformed remote owner identity must not become a trusted lease.");

        handler.ResponseOverride = "{\"schemaVersion\":1,\"code\":\"LEASE_ACQUIRED\"," +
            "\"actualSendAuthorized\":true,\"automaticRetryAllowed\":false," +
            "\"remoteSenderActivated\":false,\"epoch\":7," +
            "\"expiresAtMs\":9999999999999}";
        Assert(client.AcquireAsync(20000).GetAwaiter().GetResult().Code ==
                "HOLD_UNEXPECTED_AUTHORIZATION",
            "A compromised response claiming actual Telegram permission fails closed.");

        handler.ResponseOverride = "{\"schemaVersion\":1,\"code\":\"STATUS\"," +
            "\"actualSendAuthorized\":false,\"actualSendAuthorized\":false," +
            "\"automaticRetryAllowed\":false,\"remoteSenderActivated\":false," +
            "\"authorityTimeMs\":1794660000000,\"lease\":{}}";
        Assert(client.StatusAsync().GetAwaiter().GetResult().Code ==
                "HOLD_RESPONSE",
            "Duplicate signed response fields must be rejected.");

        handler.ResponseOverride = "{\"schemaVersion\":1,\"code\":\"LEASE_ACQUIRED\"," +
            "\"actualSendAuthorized\":false,\"automaticRetryAllowed\":false," +
            "\"remoteSenderActivated\":false,\"epoch\":7," +
            "\"expiresAtMs\":1}";
        Assert(client.AcquireAsync(20000).GetAwaiter().GetResult().Code ==
                "HOLD_RESPONSE",
            "An expired or forged far-future lease cannot be accepted.");

        handler.ResponseOverride = null;
        handler.ForceStatus = HttpStatusCode.Redirect;
        Assert(client.StatusAsync().GetAwaiter().GetResult().Code == "HOLD_HTTP",
            "Redirects and unexpected HTTP statuses are never trusted.");
        handler.ForceStatus = HttpStatusCode.OK;
        handler.ForceThrow = true;
        Assert(client.AcquireAsync(20000).GetAwaiter().GetResult().Code ==
                "HOLD_NETWORK_OR_UNCERTAIN",
            "Connection failures or possibly committed requests must not retry.");

        client.Dispose();
        Assert(client.StatusAsync().GetAwaiter().GetResult().Code ==
                "HOLD_CLIENT_CLOSED",
            "Disposing the client revokes signing and zeroes the in-memory key.");
        Console.WriteLine("WINDOWS_AUTHORITY_NODE_HMAC_AND_SEVEN_AUTHENTICATED_API_CALLS_PASS");
    }

    private static void Assert(bool pass, string message)
    {
        if (!pass) throw new InvalidOperationException(message);
    }

    private static void AssertThrows(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class SyntheticAuthorityHandler : HttpMessageHandler
    {
        public int SignedCalls { get; private set; }
        public int TransportRequests { get; private set; }
        public HashSet<string> UniqueNonces { get; } = new(StringComparer.Ordinal);
        public string? LastReservedBody { get; private set; }
        public string? ResponseOverride { get; set; }
        public HttpStatusCode ForceStatus { get; set; } = HttpStatusCode.OK;
        public bool ForceThrow { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            TransportRequests++;
            if (ForceThrow)
                throw new HttpRequestException("synthetic network loss");
            Assert(request.RequestUri?.Scheme == "https" &&
                   request.RequestUri.Host == BaseUri.Host &&
                   request.Version == HttpVersion.Version11,
                "No authority request may downgrade to HTTP or change hosts.");
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" :
                await request.Content.ReadAsStringAsync(cancellationToken);
            var method = request.Method.Method;
            string Header(string name) => request.Headers.GetValues(name).Single();
            var id = Header("x-device-id");
            var nonce = Header("x-nonce");
            var time = long.Parse(Header("x-timestamp-ms"),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert(id == Device && nonce.Length == 32 &&
                long.Abs(time - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 30000,
                "Device, nonce and signed timestamp must match the server envelope.");
            var expected = WindowsSenderAuthorityClient.Sign(
                Key, id, method, path, time, nonce, body);
            Assert(Header("x-signature") == expected && UniqueNonces.Add(nonce),
                "Every exact path and body must carry a new matching HMAC.");
            SignedCalls++;
            if (method == "POST")
                Assert(request.Content?.Headers.ContentType?.ToString() ==
                    "application/json",
                    "Node strict HTTPS API accepts application/json without charset.");
            var responseBody = ResponseOverride ?? path switch
            {
                "/v1/status" => "{\"code\":\"STATUS\",\"authorityTimeMs\":" + time +
                    ",\"lease\":{\"owner\":null,\"epoch\":0,\"expiresAtMs\":0}}",
                "/v1/lease/acquire" => Lease(time, "LEASE_ACQUIRED"),
                "/v1/lease/renew" => Lease(time, "LEASE_RENEWED"),
                "/v1/lease/release" => "{\"code\":\"LEASE_RELEASED\",\"epoch\":7}",
                "/v1/events/reserve" => Reserve(body),
                "/v1/events/uncertain" => "{\"code\":\"EVENT_MARKED_UNCERTAIN\"}",
                _ when path.StartsWith("/v1/events/", StringComparison.Ordinal) =>
                    "{\"code\":\"EVENT_FOUND\",\"event\":{\"lane\":\"FIVE_HOUR_100\",\"state\":\"RESERVED_BEFORE_SEND\",\"owner\":\"windows-1\",\"epoch\":7}}",
                _ => "{\"code\":\"HOLD_ROUTE\"}"
            };
            var prefix = "{\"schemaVersion\":1,\"actualSendAuthorized\":false," +
                "\"automaticRetryAllowed\":false,\"remoteSenderActivated\":false,";
            var data = ResponseOverride is null
                ? prefix + responseBody[1..]
                : responseBody;
            return new HttpResponseMessage(ForceStatus)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(data))
            };
        }

        private static string Lease(long time, string code) =>
            "{\"code\":\"" + code + "\",\"epoch\":7,\"expiresAtMs\":" +
            (time + 20000) + "}";

        private string Reserve(string body)
        {
            LastReservedBody = body;
            using var doc = JsonDocument.Parse(body);
            return "{\"code\":\"EVENT_RESERVED_NO_SEND\",\"eventKey\":\"" +
                doc.RootElement.GetProperty("eventKey").GetString() + "\"}";
        }
    }
}
