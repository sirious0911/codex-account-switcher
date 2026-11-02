using CodexAccountSwitcher.Core;

internal static class IntegratedMiniBarStateTests
{
    internal static void Run(Action<string, bool> check)
    {
        const string raw = """
            {"accountId":"fixture-a","ordinaryUsageAllowed":null,
             "rateLimitsByLimitId":{"codex":{"planType":null,
               "primary":{"usedPercent":25,"windowDurationMins":300},
               "secondary":{"usedPercent":50,"windowDurationMins":10080}}},
             "rateLimitResetCredits":{"availableCount":0}}
            """;
        var t = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        var a = Guid.Parse("11111111-1111-1111-1111-111111111111");
        CodexMonitorWireSample Sample(long offset = 0, string? body = null) =>
            new(t.ToUnixTimeMilliseconds() + offset, body ?? raw, a);
        var state = new IntegratedMiniBarState();
        check("integrated minibar starts UNKNOWN",
            state.Read(t).Status == MiniBarDisplayStatus.Unknown);
        check("in-process accepted observation",
            state.TryApply(Sample(), t));
        var first = state.Read(t);
        check("5H and 7D are distinct live-shaped counters",
            first.FiveHourRemaining == 75 && first.WeeklyRemaining == 50);
        check("zero reset credit is displayed as zero", first.ResetCreditsAvailable == 0);
        check("source identity never grants verification or send permission",
            !first.AccountVerified && !first.EventsAllowed && !first.TelegramAllowed);
        check("fresh boundary below 2 minutes",
            state.Read(t.AddMilliseconds(119_999)).Status == MiniBarDisplayStatus.FreshUnbound);
        check("exactly 2 minutes means aged unbound",
            state.Read(t.AddMinutes(2)).Status == MiniBarDisplayStatus.AgedUnbound);
        check("aged observation retains values",
            state.Read(t.AddMinutes(5)).FiveHourRemaining == 75);
        check("6 minutes expiration clears all counters",
            state.Read(t.AddMinutes(6)) is { Status: MiniBarDisplayStatus.Unknown,
                FiveHourRemaining: null, WeeklyRemaining: null, ResetCreditsAvailable: null });
        check("duplicate timestamp cannot repaint",
            !state.TryApply(Sample(), t.AddSeconds(1)) &&
            state.Read(t.AddSeconds(1)).FiveHourRemaining == 75);
        check("higher timestamp updates",
            state.TryApply(Sample(60_000), t.AddMinutes(1)));
        check("a different account fingerprint invalidates",
            !state.TryApply(Sample(120_000, raw.Replace("fixture-a", "fixture-b")),
                t.AddMinutes(2)) &&
            state.Read(t.AddMinutes(2)).Status == MiniBarDisplayStatus.Invalidated &&
            state.Read(t.AddMinutes(2)).ResetCreditsAvailable == null);
        check("post-invalidation older replay remains rejected",
            !state.TryApply(Sample(60_000), t.AddMinutes(2)));
        check("new account observation must be newer than invalidation",
            state.TryApply(Sample(180_000), t.AddMinutes(3)));
        state.Invalidate(t.AddMinutes(3));
        check("account switch clears values synchronously",
            state.Read(t.AddMinutes(3)).FiveHourRemaining == null);
        check("old queued frame cannot repopulate after switch",
            !state.TryApply(Sample(180_000), t.AddMinutes(3)));
        check("post-switch new source observation can repopulate",
            state.TryApply(Sample(180_001), t.AddMinutes(3).AddMilliseconds(1)));
        check("empty usage invalidates not zero",
            !state.TryApply(Sample(240_000, raw.Replace("\"availableCount\":0", "\"availableCount\":null")
                .Replace("\"usedPercent\":25", "\"usedPercent\":null")
                .Replace("\"usedPercent\":50", "\"usedPercent\":null")), t.AddMinutes(4)) &&
            state.Read(t.AddMinutes(4)).FiveHourRemaining == null);
        check("future frame fails closed",
            !state.TryApply(Sample(600_000), t.AddMinutes(4)) &&
            state.Read(t.AddMinutes(4)).Status == MiniBarDisplayStatus.Invalidated);
        check("stale source frame fails closed",
            !state.TryApply(Sample(0), t.AddMinutes(7)));
    }
}
