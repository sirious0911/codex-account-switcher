# Windows Named Pipe sender for external Codex usage minibar

Date: 2026-11-02 KST

Status: SENDER_SOURCE_IMPLEMENTED / WINDOWS_SYNTHETIC_PIPE_PASS / REAL_CROSS_PROCESS_INTEGRATION_PENDING

## Verified receiver contract (CodexPro development #14)

- Read-only inspected `C:\Codex\projects\codex-usage-monitor-prototype\dotnet-core\SwitcherLocalPipeReceiver.cs` and `SwitcherObservationDecoder.cs` without modification.
- Pipe: `codex-usage-monitor-switcher-v1`; receiver is Windows CurrentUserOnly, single inbound client, byte mode.
- Transport: one UTF-8 JSON object per line, <= 8192 bytes excluding LF.
- Wire: `{kind:"monitorObservation",observedAtMs:<unix ms>,raw:<CodexMonitorPayloadEncoder object>}`.
- Decoder expects `raw.accountId`, `raw.rateLimitsByLimitId.codex.primary` (5h/300 minutes), `.secondary` (7d/10080 minutes), and `raw.rateLimitResetCredits.availableCount`; missing metrics remain UNKNOWN.
- Receiver intentionally drops the account identity for display and invalidates values on source disconnection. It is not an account-verified publisher.

## Implemented in personal account-switcher

- `windows/CodexAccountSwitcher.Core/SwitcherLocalPipeSender.cs` is an asynchronous, in-memory, bounded latest-observation publisher with same-user pipe compatibility.
- `windows/CodexAccountSwitcher/App.xaml.cs` constructs the sender, publishes from the existing `CoreClient.MonitorObserved` event (after retaining the prior local monitor worker), and stops the sender on Quit.
- Only SwiftHost's existing observation is reused: no additional Codex App Server call, real login, account switch, credit consumption, Telegram, persistent outbox, or external network service.
- If receiver is missing or restarts, publisher is nonblocking, retries connection and discards stale/duplicate or oversized frames.
- `windows/CodexAccountSwitcher.MonitorChecks/SwitcherLocalPipeSenderTests.cs` adds an isolated real Windows Named Pipe synthetic sender/receiver fixture; `Program.cs` invokes it.

## Evidence

- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release`: 69/69 PASS; real account reads 0; account switches 0; Telegram sends 0.
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo`: 0 warnings, 0 errors.
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release`: PASS.
- `node --test scripts/tests/*.test.mjs`: 51 PASS / 1 SKIP / 0 FAIL.
- `git diff --check`: exit 0. All files remain local, unstaged; no Commit/Push/Release/installation.

## Still unverified

- The independent #14 `--switcher-listen` GUI has NOT yet consumed the newly implemented sender's observations in a real two-process integration test.
- The original CI-packaged EXE is an earlier binary; SOURCE implementation does not mean an updated EXE is installed or released.
- The SwiftHost event currently represents each refreshed profile, while #14 receiver deliberately ignores account identity. The independent minibar's value cannot yet be claimed to be the active Codex Desktop account's quota; verify this distinction in the integration gate.
- Windows native UI behavior under receiver disconnect/reconnection requires team #14 integration checks.

## Handoff

Development #14 should first test a compatible offline receiver against a separately launched synthetic publisher, then validate fresh 5H/7D/reset-credit display and UNKNOWN behavior. After explicit separate LIVE approval and account association checks, test normal fork running; do not do any second Codex usage query in the independent minibar.

Work Windows: none. The #14 repository remains unchanged.
