using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Pure, non-authorizing Windows reader for OFFLINE central Authority API V1.
/// This does not create a server, issue credentials, read the network, or grant
/// message-delivery capability. A response accepted by this parser is still
/// an untrusted MOCK observation, not a production security assertion.
/// </summary>
public static class AuthorityWireV1ReadOnlyReview
{
    private const int MaxEnvelopeUtf8Bytes = 4096;
    private static readonly HashSet<string> TopFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "code", "operation", "result",
        "actualSendAuthorized", "automaticRetryAllowed",
        "productionReservationAuthorized", "senderTransitionAuthorized"
    };
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "OFFLINE_OBSERVED_ONLY", "HOLD_UNAUTHENTICATED",
        "HOLD_FORBIDDEN_ROLE", "HOLD_INVALID_REQUEST",
        "HOLD_UNKNOWN_ROUTE", "HOLD_STORAGE_UNCERTAIN"
    };
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    {
        "offline-fixture", "status", "event", "acquire", "renew",
        "release", "reserve", "uncertain"
    };
    private static readonly HashSet<string> KnownResultStatuses = new(StringComparer.Ordinal)
    {
        "OBSERVED_ONLY", "OBSERVED_EVENT", "NO_EVENT_OBSERVED",
        "MOCK_LEASE_GRANTED", "ACTIVE_OWNER_EXISTS", "INVALID_AUTHORITY_STATE",
        "MOCK_RELEASED", "NOT_OWNER", "MOCK_RENEWED",
        "FENCE_REJECTED", "EVENT_ALREADY_RESERVED",
        "MOCK_EVENT_RESERVED_NO_SEND", "MOCK_MARKED_UNKNOWN",
        "NOT_OWNED_OR_ALREADY_HANDLED"
    };
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "bearerToken", "password", "credential", "chatId",
        "telegramToken", "authorization", "apiKey", "secret",
        "email", "rawAccountId", "sendPermit"
    };
    private static readonly HashSet<string> AuthorityFlagNames = new(StringComparer.Ordinal)
    {
        "actualSendAuthorized", "automaticRetryAllowed",
        "productionReservationAuthorized", "senderTransitionAuthorized"
    };

    public static AuthorityWireV1Inspection Inspect(string? json)
    {
        if(string.IsNullOrWhiteSpace(json) ||
            Encoding.UTF8.GetByteCount(json)>MaxEnvelopeUtf8Bytes)
            return new(AuthorityWireV1Verdict.InvalidEnvelope);

        try
        {
            using var document=JsonDocument.Parse(json,new JsonDocumentOptions
            {
                AllowTrailingCommas=false,
                CommentHandling=JsonCommentHandling.Disallow,
                MaxDepth=12
            });
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object ||
               !ValidateNoDuplicatesSecretsOrPrivileges(root,0))
                return new(AuthorityWireV1Verdict.InvalidEnvelope);

            var actualTop=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in root.EnumerateObject())
                actualTop.Add(property.Name);
            if(!actualTop.SetEquals(TopFields))
                return new(AuthorityWireV1Verdict.InvalidEnvelope);

            if(!root.TryGetProperty("schemaVersion",out var version) ||
               version.ValueKind!=JsonValueKind.Number ||
               !version.TryGetInt32(out var versionNumber) ||
               versionNumber!=1 ||
               !root.TryGetProperty("code",out var codeElement) ||
               codeElement.ValueKind!=JsonValueKind.String ||
               !Codes.Contains(codeElement.GetString()??"") ||
               !root.TryGetProperty("operation",out var operationElement) ||
               operationElement.ValueKind!=JsonValueKind.String ||
               !Operations.Contains(operationElement.GetString()??""))
                return new(AuthorityWireV1Verdict.InvalidEnvelope);

            foreach(var key in AuthorityFlagNames)
                if(!root.TryGetProperty(key,out var flag) ||
                   flag.ValueKind!=JsonValueKind.False)
                    return new(AuthorityWireV1Verdict.InvalidEnvelope);

            var code=codeElement.GetString()!;
            var result=root.GetProperty("result");
            if(code=="OFFLINE_OBSERVED_ONLY")
            {
                if(result.ValueKind!=JsonValueKind.Object ||
                   !result.TryGetProperty("status",out var status) ||
                   status.ValueKind!=JsonValueKind.String ||
                   !KnownResultStatuses.Contains(status.GetString()??""))
                    return new(AuthorityWireV1Verdict.InvalidEnvelope);
                return new(AuthorityWireV1Verdict.OfflineObservationOnly);
            }
            if(result.ValueKind!=JsonValueKind.Null)
                return new(AuthorityWireV1Verdict.InvalidEnvelope);

            return new(AuthorityWireV1Verdict.FailClosedHold);
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or
            ArgumentException or DecoderFallbackException)
        {
            return new(AuthorityWireV1Verdict.InvalidEnvelope);
        }
    }

    private static bool ValidateNoDuplicatesSecretsOrPrivileges(
        JsonElement node,int level)
    {
        if(level>12)return false;
        if(node.ValueKind==JsonValueKind.Object)
        {
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in node.EnumerateObject())
            {
                if(!seen.Add(property.Name) ||
                    SensitiveKeys.Contains(property.Name))
                    return false;

                if(AuthorityFlagNames.Contains(property.Name) &&
                   property.Value.ValueKind!=JsonValueKind.False)
                    return false;

                if(!ValidateNoDuplicatesSecretsOrPrivileges(
                    property.Value,level+1))
                    return false;
            }
        }
        else if(node.ValueKind==JsonValueKind.Array)
        {
            foreach(var item in node.EnumerateArray())
                if(!ValidateNoDuplicatesSecretsOrPrivileges(item,level+1))
                    return false;
        }
        return true;
    }
}

public enum AuthorityWireV1Verdict
{
    InvalidEnvelope,
    FailClosedHold,
    OfflineObservationOnly
}

/// <summary>Every result is observational; no branch is a send authorization.</summary>
public sealed record AuthorityWireV1Inspection(
    AuthorityWireV1Verdict Verdict)
{
    public bool ActualSendAuthorized=>false;
    public bool ProductionReservationAuthorized=>false;
    public bool AutomaticRetryAllowed=>false;
    public bool SenderTransitionAuthorized=>false;
}
