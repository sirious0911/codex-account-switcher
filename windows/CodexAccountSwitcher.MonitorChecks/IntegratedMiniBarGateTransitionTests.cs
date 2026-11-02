using CodexAccountSwitcher.Core;

// Full display-only lifecycle using the same selection gate and visual state
// as App.OnClientChanged, App.OnSwitchRequested and App.OnMonitorObserved.
// No SwiftHost process, credentials, account switching or external transport.
internal static class IntegratedMiniBarGateTransitionTests
{
    internal static void Run(Action<string, bool> check)
    {
        var a = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var b = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var t = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        var gate = new ActiveMonitorPublicationGate();
        var display = new IntegratedMiniBarState();

        static AccountSnapshot Snapshot(Guid? id, bool confirmed = true,
            bool mutating = false, string? error = null) =>
            new([], id, new(), mutating, false, confirmed, error, new Dictionary<string, string>());
        static string Raw(string id, int used) => System.Text.Json.JsonSerializer.Serialize(new
        {
            accountId = id,
            ordinaryUsageAllowed = (bool?)null,
            rateLimitsByLimitId = new
            {
                codex = new
                {
                    planType = (string?)null,
                    primary = new { usedPercent = used, windowDurationMins = 300 },
                    secondary = new { usedPercent = 50, windowDurationMins = 10080 }
                }
            },
            rateLimitResetCredits = new { availableCount = 2 }
        });
        CodexMonitorWireSample Sample(Guid? id, string source, int used, long offset) =>
            new(t.ToUnixTimeMilliseconds() + offset, Raw(source, used), id);
        void Observe(AccountSnapshot snapshot, long at)
        {
            if (gate.Observe(snapshot)) display.Invalidate(t.AddMilliseconds(at));
        }
        bool Deliver(CodexMonitorWireSample sample, long at) =>
            gate.Allows(sample) && display.TryApply(sample, t.AddMilliseconds(at));
        void RequestSwitch(long at)
        {
            gate.SwitchRequested();
            display.Invalidate(t.AddMilliseconds(at));
        }

        check("integrated gate denies samples before an active identity",
            !Deliver(Sample(a, "source-a", 10, 1), 1) &&
            display.Read(t.AddMilliseconds(1)).FiveHourRemaining is null);

        Observe(Snapshot(a), 10);
        check("confirmed A selection accepts only A",
            Deliver(Sample(a, "source-a", 20, 11), 11) &&
            display.Read(t.AddMilliseconds(11)).FiveHourRemaining == 80);
        check("background B sample cannot overwrite A",
            !Deliver(Sample(b, "source-b", 90, 12), 12) &&
            display.Read(t.AddMilliseconds(12)).FiveHourRemaining == 80);
        check("unlabelled sample cannot overwrite A",
            !Deliver(Sample(null, "source-a", 90, 13), 13) &&
            display.Read(t.AddMilliseconds(13)).FiveHourRemaining == 80);

        Observe(Snapshot(a, error: "Transport unavailable"), 20);
        check("Swift transport failure clears the integrated display",
            display.Read(t.AddMilliseconds(20)).FiveHourRemaining is null &&
            !Deliver(Sample(a, "source-a", 30, 21), 21));
        Observe(Snapshot(a, error: "Transport unavailable"), 22);
        check("repeated failed state cannot reopen the display",
            !Deliver(Sample(a, "source-a", 10, 23), 23));

        Observe(Snapshot(a), 30);
        check("recovered confirmed A allows newly observed quotas",
            Deliver(Sample(a, "source-a", 25, 31), 31) &&
            display.Read(t.AddMilliseconds(31)).FiveHourRemaining == 75);

        RequestSwitch(40);
        check("switch request immediately clears A",
            display.Read(t.AddMilliseconds(40)).FiveHourRemaining is null &&
            !Deliver(Sample(a, "source-a", 0, 41), 41));
        Observe(Snapshot(a), 42);
        check("pre-switch snapshot does not restore old A",
            !Deliver(Sample(a, "source-a", 0, 43), 43));
        Observe(Snapshot(a, mutating: true), 44);
        check("mutating switch blocks both A and B",
            !Deliver(Sample(a, "source-a", 0, 45), 45) &&
            !Deliver(Sample(b, "source-b", 90, 45), 45));

        Observe(Snapshot(b), 50);
        check("B becomes visible only after completed confirmed switch",
            Deliver(Sample(b, "source-b", 60, 51), 51) &&
            display.Read(t.AddMilliseconds(51)).FiveHourRemaining == 40);
        check("late A frame cannot repaint B",
            !Deliver(Sample(a, "source-a", 0, 52), 52) &&
            display.Read(t.AddMilliseconds(52)).FiveHourRemaining == 40);
        check("late prior B frame cannot replay through cutoff",
            !Deliver(Sample(b, "source-b", 60, 50), 53) &&
            display.Read(t.AddMilliseconds(53)).FiveHourRemaining == 40);
        check("malformed B frame older than selection cutoff is ignored without blanking B",
            !Deliver(new CodexMonitorWireSample(t.ToUnixTimeMilliseconds() + 50, "{", b), 54) &&
            display.Read(t.AddMilliseconds(54)).FiveHourRemaining == 40);
        check("malformed duplicate B frame is ignored without blanking latest B",
            !Deliver(new CodexMonitorWireSample(t.ToUnixTimeMilliseconds() + 51, "{", b), 55) &&
            display.Read(t.AddMilliseconds(55)).FiveHourRemaining == 40);

        Observe(Snapshot(b, confirmed: false), 60);
        check("unknown B identity immediately clears quotas",
            display.Read(t.AddMilliseconds(60)).FiveHourRemaining is null &&
            !Deliver(Sample(b, "source-b", 0, 61), 61));
        Observe(Snapshot(b), 70);
        check("reconfirmation requires post-boundary observation",
            !Deliver(Sample(b, "source-b", 0, 69), 71) &&
            Deliver(Sample(b, "source-b", 35, 71), 71) &&
            display.Read(t.AddMilliseconds(71)).FiveHourRemaining == 65);
        check("entire A/B synthetic lifecycle cannot enable account or alerts",
            !display.Read(t.AddMilliseconds(71)).AccountVerified &&
            !display.Read(t.AddMilliseconds(71)).EventsAllowed &&
            !display.Read(t.AddMilliseconds(71)).TelegramAllowed);
    }
}
