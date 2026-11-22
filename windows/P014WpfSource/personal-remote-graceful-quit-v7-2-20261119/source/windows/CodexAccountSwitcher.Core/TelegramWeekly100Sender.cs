using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Explicitly gated Telegram Bot API adapter. Not instantiated by the Windows
/// application. In production, caller must first obtain explicit consent and
/// set up a single sender; no tokens or chat IDs are saved by this component.
/// </summary>
public sealed class TelegramWeekly100Sender : IDisposable
{
    private readonly HttpClient http;
    private readonly bool transportEnabled;
    private bool disposed;

    // Real transports disable redirects so the bot token in the URL is never
    // forwarded to another HTTP host.
    public TelegramWeekly100Sender(bool transportEnabled = false)
        : this(new HttpClientHandler { AllowAutoRedirect = false }, transportEnabled) { }

    // Permits tests to inject a completely in-memory handler.
    public TelegramWeekly100Sender(HttpMessageHandler handler, bool transportEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        http = new HttpClient(handler, disposeHandler: true) {
            Timeout = TimeSpan.FromSeconds(10)
        };
        this.transportEnabled = transportEnabled;
    }

    public bool IsEnabled => transportEnabled && !disposed;

    /// <summary>
    /// Requires both transport-level opt-in and single-use user consent.
    /// A successful Telegram response is the only acknowledged outcome.
    /// Once any HTTP attempt starts, failures are indeterminate: NEVER
    /// automatically retry, because Telegram sendMessage has no idempotency key.
    /// </summary>
    public async Task<TelegramWeekly100SendOutcome> SendOnceAsync(
        Weekly100Alert alert, string? botToken, string? chatID,
        TelegramWeekly100SendConsent? consent,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || consent is null)
            return TelegramWeekly100SendOutcome.Blocked;

        if (alert is null || alert.Kind != "WEEKLY_100_RECOVERED" ||
            alert.Lane != "week" || alert.RemainingPercent != 100 ||
            alert.Status != "MOCK_RESERVED" || alert.MockOwner != "MOCK_WINDOWS")
            return TelegramWeekly100SendOutcome.Blocked;

        if (!HasValidAddress(botToken, chatID))
            return TelegramWeekly100SendOutcome.Blocked;

        // Reserve this approval for one and only one HTTP request. The token
        // cannot be reused even if the first request's outcome is uncertain.
        if (!consent.TryConsume())
            return TelegramWeekly100SendOutcome.Blocked;

        var message = FormatText(alert);
        // Fixed HTTPS host; credentials exist only in the request URI.
        var uri = new Uri("https://api.telegram.org/bot" + botToken +
            "/sendMessage", UriKind.Absolute);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) {
                Content = JsonContent.Create(new {
                    chat_id = chatID,
                    text = message,
                    disable_web_page_preview = true
                })
            };
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK ||
                response.Content.Headers.ContentLength is > 4096)
                return TelegramWeekly100SendOutcome.Uncertain;

            using var input = await response.Content.ReadAsStreamAsync(
                cancellationToken).ConfigureAwait(false);
            // Cap even chunked responses which have no Content-Length header.
            var payload = new byte[4097];
            var length = 0;
            while (length < payload.Length)
            {
                var n = await input.ReadAsync(payload.AsMemory(length),
                    cancellationToken).ConfigureAwait(false);
                if (n == 0) break;
                length += n;
            }
            if (length > 4096) return TelegramWeekly100SendOutcome.Uncertain;
            using var result = JsonDocument.Parse(payload.AsMemory(0, length),
                new JsonDocumentOptions { MaxDepth = 8 });
            var root = result.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("ok", out var ok) &&
                ok.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("result", out var sent) &&
                sent.ValueKind == JsonValueKind.Object &&
                sent.TryGetProperty("message_id", out var number) &&
                number.TryGetInt64(out var messageID) && messageID > 0)
                return TelegramWeekly100SendOutcome.Acknowledged;
            return TelegramWeekly100SendOutcome.Uncertain;
        }
        catch (Exception error) when (error is HttpRequestException or
            TaskCanceledException or IOException or JsonException)
        {
            // Intentionally do not print exception.Message: it can contain
            // the URI and bot token. The outcome is also not a retry signal.
            return TelegramWeekly100SendOutcome.Uncertain;
        }
    }

    // Validation only. No token discovery, persistence, network, or logging.
    public static bool HasValidAddress(string? botToken, string? chatID) =>
        !string.IsNullOrEmpty(botToken) && botToken.Length <= 160 &&
        Regex.IsMatch(botToken, @"^[0-9]{5,20}:[A-Za-z0-9_-]{15,120}$",
            RegexOptions.CultureInvariant) &&
        !string.IsNullOrWhiteSpace(chatID) && chatID.Length <= 80 &&
        Regex.IsMatch(chatID, @"^-?[0-9]{1,20}$",
            RegexOptions.CultureInvariant);

    public static string FormatText(Weekly100Alert alert)
    {
        if (alert is null || alert.Kind != "WEEKLY_100_RECOVERED" ||
            alert.Lane != "week" || alert.RemainingPercent != 100)
            throw new ArgumentException("Invalid confirmed weekly recovery event");
        var when = alert.ObservedAt.ToOffset(TimeSpan.FromHours(9))
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return $"Codex 주간(7일) 한도 초기화 확인 (관측 {when} KST)";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        http.Dispose();
    }
}

public enum TelegramWeekly100SendOutcome
{
    Blocked,
    Acknowledged,
    Uncertain
}

public sealed class TelegramWeekly100SendConsent
{
    private int consumed;

    // The caller must create this only in response to explicit user approval.
    // Interlocked prevents multiple asynchronous callers from sharing one
    // consent to send more than one Telegram message.
    internal bool TryConsume() =>
        System.Threading.Interlocked.Exchange(ref consumed, 1) == 0;
}


