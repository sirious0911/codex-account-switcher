using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// OFFLINE synthetic 5H/7D status projection ONLY. This candidate review
/// does not authenticate device origin, verify HTTPS, read accounts,
/// enable Sender mode, write user settings, or authorize Telegram.
/// </summary>
public static class QuotaViewV1ReadOnlyReview
{
    private const long MinTime=1577836800000;
    private const long MaxTime=4133980800000;
    private const long MaxAge=600000;
    private const long MaxFutureSkew=3000;
    private static readonly HashSet<string> RequiredTop=new(StringComparer.Ordinal)
    {
        "schemaVersion","sourceDeviceId","sourceSessionId","sequence",
        "collectedAtUtcMs","accountIdSha256","fiveHour","sevenDay",
        "actualSendAuthorized","automaticRetryAllowed",
        "productionReservationAuthorized","senderTransitionAuthorized"
    };
    private static readonly HashSet<string> RequiredQuota=new(StringComparer.Ordinal)
    {
        "remainingPercent","resetAtUtcMs"
    };
    private static readonly string[] PrivilegeKeys=
    [
        "actualSendAuthorized","automaticRetryAllowed",
        "productionReservationAuthorized","senderTransitionAuthorized"
    ];
    private static readonly Regex DeviceRegex=new(
        "^[A-Za-z0-9-]{1,48}$",RegexOptions.CultureInvariant);
    private static readonly Regex SessionRegex=new(
        "^[a-f0-9]{32}$",RegexOptions.CultureInvariant);
    private static readonly Regex HashRegex=new(
        "^[a-f0-9]{64}$",RegexOptions.CultureInvariant);

    public static QuotaViewV1Inspection Inspect(
        string? raw,long trustedNowUtcMs,string? previousRaw=null)
    {
        if(!Timestamp(trustedNowUtcMs))
            return Hold("HOLD_UNTRUSTED_VIEW_CLOCK");
        var current=Decode(raw);
        if(current is null)return Hold("HOLD_INVALID_VIEW_FRAME");
        if(current.CollectedAtUtcMs>trustedNowUtcMs+MaxFutureSkew ||
           trustedNowUtcMs-current.CollectedAtUtcMs>MaxAge)
            return Hold("HOLD_STALE_OR_FUTURE_VIEW");
        if(previousRaw is not null)
        {
            var previous=Decode(previousRaw);
            if(previous is null)return Hold("HOLD_INVALID_PREVIOUS_VIEW");
            if(current.SourceDeviceId!=previous.SourceDeviceId)
                return Hold("HOLD_SOURCE_DEVICE_CONFLICT");
            if(current.AccountIdSha256!=previous.AccountIdSha256)
                return Hold("HOLD_ACCOUNT_IDENTITY_CHANGED");
            if(current.CollectedAtUtcMs<=previous.CollectedAtUtcMs)
                return Hold("HOLD_REPLAY_OR_REORDERED_VIEW");
            if(current.SourceSessionId==previous.SourceSessionId &&
               current.Sequence<=previous.Sequence)
                return Hold("HOLD_REPLAY_OR_REORDERED_VIEW");
            if(current.SourceSessionId!=previous.SourceSessionId &&
               current.Sequence!=1)
                return Hold("HOLD_NEW_SESSION_SEQUENCE_NOT_INITIAL");
        }
        return new("OFFLINE_VIEW_CANDIDATE_ONLY",current);
    }
    private static QuotaViewV1Inspection Hold(string status)=>new(status,null);
    private static bool Timestamp(long x)=>x>=MinTime&&x<=MaxTime;

    private static QuotaViewV1Snapshot? Decode(string? raw)
    {
        if(raw is null||Encoding.UTF8.GetByteCount(raw)>2048)return null;
        try
        {
            using var doc=JsonDocument.Parse(raw,new JsonDocumentOptions
            {
                AllowTrailingCommas=false,
                CommentHandling=JsonCommentHandling.Disallow,
                MaxDepth=8
            });
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object ||
               !ValidateNoDuplicateKeys(root,0) ||
               !ExactObject(root,RequiredTop))
                return null;
            if(!ReadNumber(root,"schemaVersion",out var schema)||schema!=1||
               !ReadString(root,"sourceDeviceId",DeviceRegex,out var device)||
               !ReadString(root,"sourceSessionId",SessionRegex,out var session)||
               !ReadNumber(root,"sequence",out var sequence)||sequence<1||
               sequence>1000000000||
               !ReadNumber(root,"collectedAtUtcMs",out var observed)||
               !Timestamp(observed)||
               !ReadString(root,"accountIdSha256",HashRegex,out var hash)||
               PrivilegeKeys.Any(key=>root.GetProperty(key).ValueKind!=
                   JsonValueKind.False))
                return null;

            if(!TryQuota(root.GetProperty("fiveHour"),observed,
                6*60*60*1000L,out var five)||
               !TryQuota(root.GetProperty("sevenDay"),observed,
                8*24*60*60*1000L,out var seven)||
               (five!.RemainingPercent is null&&
                seven!.RemainingPercent is null))
                return null;
            return new(
                device!,session!,sequence,observed,hash!,five!,seven!);
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException
            or ArgumentException or OverflowException)
        {
            return null;
        }
    }
    private static bool ExactObject(JsonElement obj,HashSet<string> keys)
    {
        if(obj.ValueKind!=JsonValueKind.Object)return false;
        var found=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in obj.EnumerateObject())found.Add(p.Name);
        return found.SetEquals(keys);
    }
    private static bool ValidateNoDuplicateKeys(JsonElement obj,int depth)
    {
        if(depth>8)return false;
        if(obj.ValueKind==JsonValueKind.Object)
        {
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var p in obj.EnumerateObject())
                if(!seen.Add(p.Name)||
                   !ValidateNoDuplicateKeys(p.Value,depth+1))
                    return false;
        }
        else if(obj.ValueKind==JsonValueKind.Array)
        {
            foreach(var child in obj.EnumerateArray())
                if(!ValidateNoDuplicateKeys(child,depth+1))return false;
        }
        return true;
    }
    private static bool ReadString(JsonElement root,string name,Regex pattern,
        out string? value)
    {
        value=null;
        var p=root.GetProperty(name);
        if(p.ValueKind!=JsonValueKind.String)return false;
        var candidate=p.GetString();
        if(candidate is null||!pattern.IsMatch(candidate))return false;
        value=candidate;
        return true;
    }
    private static bool ReadNumber(JsonElement root,string name,out long value)
    {
        value=0;
        return Number(root.GetProperty(name),out value);
    }
    private static bool Number(JsonElement entry,out long value)
    {
        value=0;
        if(entry.ValueKind!=JsonValueKind.Number||
           !entry.TryGetDouble(out var d)||
           !double.IsFinite(d)||
           d!=Math.Truncate(d)||
           d<0 || d>9007199254740991d)
            return false;
        value=(long)d;
        return true;
    }
    private static bool TryQuota(JsonElement item,long observed,long maxAhead,
        out QuotaViewV1Window? quota)
    {
        quota=null;
        if(!ExactObject(item,RequiredQuota))return false;
        int? remaining=null;
        long? reset=null;
        var percent=item.GetProperty("remainingPercent");
        if(percent.ValueKind!=JsonValueKind.Null)
        {
            if(!Number(percent,out var p)||p>100)return false;
            remaining=(int)p;
        }
        var resetElement=item.GetProperty("resetAtUtcMs");
        if(resetElement.ValueKind!=JsonValueKind.Null)
        {
            if(!Number(resetElement,out var r)||!Timestamp(r)||
               r<observed-600000 ||r>observed+maxAhead)
                return false;
            reset=r;
        }
        quota=new(remaining,reset);
        return true;
    }
}

/// <summary>Never a trustworthy account, device, TLS, or Sender assertion.</summary>
public sealed record QuotaViewV1Window(int? RemainingPercent,long? ResetAtUtcMs);
public sealed record QuotaViewV1Snapshot(
    string SourceDeviceId,string SourceSessionId,long Sequence,
    long CollectedAtUtcMs,string AccountIdSha256,
    QuotaViewV1Window FiveHour,QuotaViewV1Window SevenDay);
public sealed record QuotaViewV1Inspection(
    string Status,QuotaViewV1Snapshot? View)
{
    public bool ActualSendAuthorized=>false;
    public bool AutomaticRetryAllowed=>false;
    public bool ProductionReservationAuthorized=>false;
    public bool SenderTransitionAuthorized=>false;
    public bool RealDeviceAuthenticated=>false;
    public bool RealCrossHostSyncAuthorized=>false;
}
