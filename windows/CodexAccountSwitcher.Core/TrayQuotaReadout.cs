using System.Globalization;

namespace CodexAccountSwitcher.Core;

// Pure, side-effect-free text projection for native taskbar notification icons.
// "?" is UNKNOWN; an actual zero remains "0". No values are inferred.
public sealed record TrayQuotaReadout(
    string FiveHourIcon, string WeeklyIcon, string CreditsIcon,
    string FiveHourTooltip, string WeeklyTooltip, string CreditsTooltip,
    MiniBarDisplayStatus Status)
{
    public static TrayQuotaReadout From(MiniBarReadout frame)
    {
        bool visible = frame.Status is MiniBarDisplayStatus.FreshUnbound or MiniBarDisplayStatus.AgedUnbound;
        string percent(double? value) => visible && value.HasValue
            ? value.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "UNKNOWN";
        string icon(double? value) => visible && value.HasValue
            ? Math.Round(value.Value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
            : "?";
        var status = frame.Status switch
        {
            MiniBarDisplayStatus.FreshUnbound => "최신 · 계정 미확인",
            MiniBarDisplayStatus.AgedUnbound => "지연 · 계정 미확인",
            MiniBarDisplayStatus.Invalidated => "계정 전환/연결 오류",
            _ => "미조회/만료"
        };
        var credits = visible ? frame.ResetCreditsAvailable : null;
        return new(
            icon(frame.FiveHourRemaining), icon(frame.WeeklyRemaining),
            credits?.ToString(CultureInfo.InvariantCulture) ?? "?",
            "Codex 5H: " + percent(frame.FiveHourRemaining) + " · " + status,
            "Codex 7D: " + percent(frame.WeeklyRemaining) + " · " + status,
            "Codex 리셋권: " + (credits?.ToString(CultureInfo.InvariantCulture) ?? "UNKNOWN") + " · " + status,
            frame.Status);
    }
}
