using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Usage-only observation from the existing SwiftHost stdio channel.
/// It includes the provider account identifier and quota counters, never auth tokens.
/// No background process, network transport, or Telegram call is made here.
/// </summary>
public sealed record CodexMonitorWireSample(long ObservedAtMilliseconds, string RawJson, Guid? ProfileID = null)
{
    public static bool TryParse(JsonElement message, out CodexMonitorWireSample? sample)
    {
        sample = null;
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("kind", out var kind) ||
            kind.ValueKind != JsonValueKind.String ||
            kind.GetString() != "monitorObservation" ||
            !message.TryGetProperty("observedAtMs", out var time) ||
            time.ValueKind != JsonValueKind.Number ||
            !time.TryGetInt64(out var observedAtMs) ||
            observedAtMs <= 0 ||
            !message.TryGetProperty("raw", out var raw) ||
            raw.ValueKind != JsonValueKind.Object)
            return false;

        Guid? profileID = null;
        if (message.TryGetProperty("profileID", out var profileElement))
        {
            if (profileElement.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(profileElement.GetString(), out var id)) return false;
            profileID = id;
        }
        var json = raw.GetRawText();
        if (!MonitorSyntheticPreview.TryRead(json, out _)) return false;
        sample = new CodexMonitorWireSample(observedAtMs, json, profileID);
        return true;
    }
}
