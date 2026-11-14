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
        string reset(DateTimeOffset? time)
        {
            if (!visible || time is null)
                return "다음 초기화: 정보 없음";
            if (frame.ObservedAt is DateTimeOffset observed &&
                time.Value <= observed)
                return "다음 초기화: 갱신 필요";
            return "다음 초기화: " +
                time.Value.ToOffset(TimeSpan.FromHours(9))
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " KST";
        }

        string expiryDetails()
        {
            if (!visible || credits is null)
                return "개별 만료일: 정보 없음";
            if (credits.Value == 0) return "사용 가능한 초기화권 없음";
            var dates = frame.CreditExpiresAt;
            if (dates is null || dates.Count == 0)
                return "개별 만료일: 제공되지 않음";
            var ordered = dates.OrderBy(x => !x.HasValue)
                .ThenBy(x => x).Take(Math.Min(credits.Value, 32)).ToArray();
            if (ordered.Length == 0) return "개별 만료일: 제공되지 않음";
            var lines = ordered.Select((date, i) =>
                (i + 1).ToString(CultureInfo.InvariantCulture) + "번 만료: " +
                (date is null ? "정보 없음" :
                    date.Value.ToOffset(TimeSpan.FromHours(9))
                        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " KST"));
            var rest = credits.Value - ordered.Length;
            return string.Join("\n", lines) +
                (rest > 0 ? "\n나머지 " + rest.ToString(CultureInfo.InvariantCulture) +
                    "개: 만료일 정보 없음" : "");
        }

        return new(
            icon(frame.FiveHourRemaining), icon(frame.WeeklyRemaining),
            credits?.ToString(CultureInfo.InvariantCulture) ?? "?",
            "Codex 5H: " + percent(frame.FiveHourRemaining) +
                "\n" + reset(frame.FiveHourResetsAt) + " · " + status,
            "Codex 7D: " + percent(frame.WeeklyRemaining) +
                "\n" + reset(frame.WeeklyResetsAt) + " · " + status,
            "Codex 리셋권: " + (credits?.ToString(CultureInfo.InvariantCulture) ?? "UNKNOWN") +
                "\n" + expiryDetails() + " · " + status,
            frame.Status);
    }
}
