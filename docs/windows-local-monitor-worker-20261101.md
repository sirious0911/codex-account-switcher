# Windows local usage monitor worker — personal fork

Date: 2026-11-01 KST

## Implemented

The Windows Codex Account Switcher now attaches a **local in-process monitor worker** to the existing `CoreClient.MonitorObserved` event. Its purpose is to record usage observations and preserve the monitor's already-developed event decisions across application restarts. There is **no separate Node installation or process** for normal Windows usage.

The three existing portable .NET source files were copied **unchanged** from `C:\Codex\projects\codex-usage-monitor-prototype\dotnet-core` into `monitor/dotnet-core`:

- `UsageEventState.cs`: SHA256 `adfe60800dc45c4e67f6034aaa820ac24beeb81632dfcd4e4e5b6172e5ae2424`
- `SyntheticUsageNormalizer.cs`: SHA256 `a37881037643cc388646d0f493c17bb36ae58ba5805e52e4f2c3ddfcc9464ab6`
- `MockDeliveryController.cs`: SHA256 `a816c95453dd781566099da62dcbf12435fe8c2f6b956ee85d29ed14ec665005`

The original external monitoring repository was not modified. The new `windows/CodexAccountSwitcher.Core/LocalMonitorWorker.cs` composes these implementations and its dependencies. `CodexAccountSwitcher.Core.csproj` references the portable `CodexUsageMonitor.Core.csproj` so the existing WPF executable includes the worker **without requiring Node.js**.

Application behavior:

1. Existing SwiftHost sends fresh `monitorObservation` messages when the provider usage read succeeds; stale cache and failed reads are not converted into new events.
2. Existing Windows `CoreClient` raises `MonitorObserved`. `App.xaml.cs` subscribes once on startup, creates a local worker under the configured application data directory, and unsubscribes on normal quit.
3. Worker loads one persisted event ledger **per provider account**, keyed by a stable SHA256-derived directory name, normalizes the reading using the reused .NET implementation, and advances the event detector.
4. State commits use the original monitor's atomic state writer. A leftover `state.json.tmp` from an interrupted write is removed when the last complete ledger is successfully read.
5. Generated events are stored as **local mock-only outbox** entries; each mock reservation is persisted and then acknowledged. No HTTP call, Telegram token, real message delivery or standalone Node process exists in this runtime code.
6. An error in the supplementary monitor worker is reported without blocking the upstream Codex account-switching UI.

## Verified Windows local tests

- Native WPF Release build PASS, **0 warnings / 0 errors**, including the portable monitor library.
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release`: **52/52 PASS**; 42 prior parser/transport checks plus 10 new persisted-worker checks.
- New tests cover first observation baseline, two observations before event confirmation, at-most-once mock outbox delivery, re-instantiation/restart restored state, out-of-order observations, two-account separation, hashed folder naming, interrupted temporary-file recovery, and unknown plan producing no alert.
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release`: PASS.
- `node --test scripts/tests/*.test.mjs`: 48 PASS / 1 SKIP / 0 FAIL.
- `node scripts/check-site-geo.mjs`: 16 HTML and 16 sitemap URLs PASS.

Windows bundled EXE self-test is also extended to use an isolated `SELFTEST_ACCOUNT` monitor observation with null plan/permission. It checks ledger recovery after re-instantiating the worker and has no access to real accounts. **The new full GitHub Windows package CI must still confirm bundled EXE success on this commit.**

## What is NOT completed

- Actual Codex usage observations currently include `planType = null` and `ordinaryUsageAllowed = null`. The monitor displays usage and persists local observations, but cannot infer account plan or generate a confirmed credit/recovery alert from that incomplete identity. Synthetic fixtures with explicitly supplied plan/permission validate the original event algorithm only.
- There is no real Telegram sender and no user account/login action in this change. No production alerts were sent.
- macOS app has not yet been wired to the portable local event worker. The original Swift account switcher remains supported.
- True abrupt power-loss durability and integration with multiple simultaneously running app instances were not tested; the monitor's existing atomic snapshot mechanism is reused as is. The 52-case suite checks graceful restarted worker instances and leftover incomplete writes.
- User-facing UI currently shows the existing Swift usage snapshot. It does not yet show persistent local mock outbox history.
- No app deployment, GitHub Release publish, npm publication, original GitHub change or Draft PR merge occurred.
