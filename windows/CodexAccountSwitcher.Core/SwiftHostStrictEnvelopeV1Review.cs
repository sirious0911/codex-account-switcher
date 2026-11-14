using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Strict schema validator for SwiftHost monitorObservation stdout envelopes.
/// CoreClient invokes this before publishing a monitor sample. This check
/// validates syntax and field shape only; it neither authenticates the host
/// nor grants account, Telegram, device or Sender permissions.
///
/// A valid result contains the provider accountId inside Sample.RawJson.
/// That local-only in-memory result is sensitive application data: never log,
/// persist, transmit or interpret it as remote device authentication.
/// </summary>
public static class SwiftHostStrictEnvelopeV1Review
{
    public const int MaxEnvelopeUtf8Bytes=12_288;
    public const int MaxRawUtf8Bytes=MonitorSyntheticPreview.MaxJsonBytes;
    private const long MinUtcMs=1_577_836_800_000; // 2020-01-01
    private const long MaxUtcMs=4_102_444_800_000; // 2100-01-01 exclusive
    private const long MinUtcSeconds=1_577_836_800;
    private const long MaxUtcSeconds=4_102_444_800;
    private static readonly UTF8Encoding StrictUtf8=new(false,true);

    public static SwiftHostStrictEnvelopeV1Inspection Inspect(string? jsonLine)
    {
        if(jsonLine is null)
            return Hold("HOLD_EMPTY_ENVELOPE");
        int length;
        try { length=StrictUtf8.GetByteCount(jsonLine); }
        catch(EncoderFallbackException) {
            return Hold("HOLD_INVALID_ENVELOPE_UTF16");
        }
        if(length==0)return Hold("HOLD_EMPTY_ENVELOPE");
        if(length>MaxEnvelopeUtf8Bytes)
            return Hold("HOLD_OVER_LIMIT_ENVELOPE");
        try
        {
            using var doc=JsonDocument.Parse(jsonLine,new JsonDocumentOptions
            {
                MaxDepth=12,
                CommentHandling=JsonCommentHandling.Disallow,
                AllowTrailingCommas=false
            });
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)
                return Hold("HOLD_INVALID_ENVELOPE_SHAPE");

            // Compare the decoded JSON key names, not the original bytes.
            // This rejects both literal and Unicode-escaped duplicate names.
            if(HasDuplicateNames(root))
                return Hold("HOLD_DUPLICATE_JSON_PROPERTY");
            if(!ExactObject(root,"kind","profileID","observedAtMs","raw"))
                return Hold("HOLD_UNKNOWN_OR_MISSING_ENVELOPE_FIELD");

            var kind=root.GetProperty("kind");
            if(kind.ValueKind!=JsonValueKind.String ||
               kind.GetString()!="monitorObservation")
                return Hold("HOLD_INVALID_ENVELOPE_KIND");

            var profile=root.GetProperty("profileID");
            if(profile.ValueKind!=JsonValueKind.String)
                return Hold("HOLD_INVALID_ENVELOPE_PROFILE");
            var profileText=profile.GetString();
            if(profileText is null||profileText.Length!=36||
               !Guid.TryParseExact(profileText,"D",out var profileId)||
               profileId==Guid.Empty)
                return Hold("HOLD_INVALID_ENVELOPE_PROFILE");

            var observed=root.GetProperty("observedAtMs");
            if(observed.ValueKind!=JsonValueKind.Number||
               !observed.TryGetInt64(out var observedMs)||
               observedMs<MinUtcMs||observedMs>=MaxUtcMs)
                return Hold("HOLD_INVALID_ENVELOPE_TIME");

            var raw=root.GetProperty("raw");
            if(raw.ValueKind!=JsonValueKind.Object)
                return Hold("HOLD_INVALID_RAW_SHAPE");
            var rawText=raw.GetRawText();
            if(StrictUtf8.GetByteCount(rawText)>MaxRawUtf8Bytes)
                return Hold("HOLD_OVER_LIMIT_RAW");
            if(!ValidSwiftHostRaw(raw))
                return Hold("HOLD_INVALID_RAW_SCHEMA");
            if(!CodexMonitorWireSample.TryParse(root,out var sample)||
               sample is null||sample.ProfileID!=profileId||
               sample.ObservedAtMilliseconds!=observedMs)
                return Hold("HOLD_LEGACY_WIRE_PARSER_DISAGREES");
            return new("OFFLINE_STRICT_WIRE_CANDIDATE_ONLY",sample);
        }
        catch(JsonException){return Hold("HOLD_INVALID_ENVELOPE_JSON");}
        catch(EncoderFallbackException){return Hold("HOLD_INVALID_ENVELOPE_UTF16");}
        catch(ArgumentException){return Hold("HOLD_INVALID_ENVELOPE_JSON");}
        catch(InvalidOperationException){return Hold("HOLD_INVALID_ENVELOPE_JSON");}
    }

    private static SwiftHostStrictEnvelopeV1Inspection Hold(string status)=>
        new(status,null);

    private static bool ExactObject(JsonElement obj,params string[] fields)
    {
        if(obj.ValueKind!=JsonValueKind.Object)return false;
        var found=0;
        foreach(var property in obj.EnumerateObject())
        {
            if(!fields.Contains(property.Name,StringComparer.Ordinal))
                return false;
            found++;
        }
        return found==fields.Length;
    }

    private static bool HasDuplicateNames(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in element.EnumerateObject())
            {
                if(!seen.Add(property.Name)||
                   HasDuplicateNames(property.Value))
                    return true;
            }
        }
        else if(element.ValueKind==JsonValueKind.Array)
        {
            foreach(var child in element.EnumerateArray())
                if(HasDuplicateNames(child))return true;
        }
        return false;
    }

    private static bool ValidSwiftHostRaw(JsonElement raw)
    {
        if(!ExactObject(raw,"accountId","ordinaryUsageAllowed",
           "rateLimitsByLimitId","rateLimitResetCredits"))
            return false;

        var account=raw.GetProperty("accountId");
        if(account.ValueKind!=JsonValueKind.String)return false;
        var id=account.GetString();
        if(string.IsNullOrWhiteSpace(id)||StrictUtf8.GetByteCount(id)>256||
           id.Any(char.IsControl))
            return false;

        var allowed=raw.GetProperty("ordinaryUsageAllowed");
        if(allowed.ValueKind is not
           (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
            return false;
        var limits=raw.GetProperty("rateLimitsByLimitId");
        if(!ExactObject(limits,"codex"))return false;
        var codex=limits.GetProperty("codex");
        if(!ExactObject(codex,"planType","primary","secondary"))
            return false;
        var plan=codex.GetProperty("planType");
        if(plan.ValueKind!=JsonValueKind.Null)
        {
            if(plan.ValueKind!=JsonValueKind.String)return false;
            var label=plan.GetString();
            if(string.IsNullOrWhiteSpace(label)||
               StrictUtf8.GetByteCount(label)>128||
               label.Any(char.IsControl))
                return false;
        }
        if(!ValidWindow(codex.GetProperty("primary"),300,true)||
           !ValidWindow(codex.GetProperty("secondary"),10080,false))
            return false;
        var credits=raw.GetProperty("rateLimitResetCredits");
        if(credits.ValueKind!=JsonValueKind.Object||
           !credits.TryGetProperty("availableCount",out var available))
            return false;
        foreach(var property in credits.EnumerateObject())
            if(property.Name is not ("availableCount" or "credits"))
                return false;
        if(available.ValueKind!=JsonValueKind.Null &&
           (available.ValueKind!=JsonValueKind.Number||
            !available.TryGetInt32(out var count)||count<0))
            return false;
        // The SwiftHost encoder strips credit IDs and carries only expiry
        // dates for available Codex reset credits. Never admit an ID, bot
        // token, or other provider field through the monitor envelope.
        if(credits.TryGetProperty("credits",out var entries))
        {
            if(entries.ValueKind!=JsonValueKind.Array||
               entries.GetArrayLength()>32)
                return false;
            foreach(var entry in entries.EnumerateArray())
            {
                if(!ExactObject(entry,"status","expiresAt"))
                    return false;
                var status=entry.GetProperty("status");
                if(status.ValueKind!=JsonValueKind.String||
                   status.GetString()!="available")
                    return false;
                var expires=entry.GetProperty("expiresAt");
                if(expires.ValueKind!=JsonValueKind.Null &&
                   (expires.ValueKind!=JsonValueKind.Number||
                    !expires.TryGetInt64(out var epoch)||
                    epoch<MinUtcSeconds||epoch>=MaxUtcSeconds))
                    return false;
            }
        }
        return true;
    }

    private static bool ValidWindow(JsonElement window,
        int expectedMinutes,bool nullable)
    {
        if(nullable&&window.ValueKind==JsonValueKind.Null)return true;
        if(!ExactObject(window,"usedPercent","resetsAt","windowDurationMins"))
            return false;
        var used=window.GetProperty("usedPercent");
        var minutes=window.GetProperty("windowDurationMins");
        var reset=window.GetProperty("resetsAt");
        // The Swift normalized encoder serializes integer remaining%, so
        // usedPercent is integer too (unlike raw Codex provider float data).
        return used.ValueKind==JsonValueKind.Number&&
            used.TryGetInt32(out var pct)&&pct is >=0 and <=100 &&
            minutes.ValueKind==JsonValueKind.Number&&
            minutes.TryGetInt32(out var duration)&&
            duration==expectedMinutes &&
            reset.ValueKind==JsonValueKind.Number&&
            reset.TryGetInt64(out var seconds)&&
            seconds>=MinUtcSeconds&&seconds<MaxUtcSeconds;
    }
}

/// <summary>
/// Source-format inspection only; absolutely no operating or send rights.
/// </summary>
public sealed record SwiftHostStrictEnvelopeV1Inspection(
    string Status,CodexMonitorWireSample? Sample)
{
    public bool ActualSendAuthorized=>false;
    public bool AutomaticRetryAllowed=>false;
    public bool TelegramAllowed=>false;
    public bool RealDeviceAuthenticated=>false;
    public bool SenderTransitionAuthorized=>false;
    public bool ProductionReservationAuthorized=>false;
    public bool LiveUiConnected=>false;
    public bool RealCrossHostSyncAuthorized=>false;
}
