# Provider metadata + monitor UI parity — 2026-11-01 KST

## Scope

- Verified current OpenAI Codex app-server protocol `account/rateLimits/read` response includes OPTIONAL `rateLimitsByLimitId.codex.planType` (or legacy `rateLimits.planType`) and `ordinaryUsageAllowed` at the response root.
- Existing `CodexClient.readWeeklyUsage` now extracts these same-response optional fields and carries them through `WeeklyUsage`, keeping all original 5-hour/weekly credit normalization and its authentication workflow unchanged. No extra RPC or login performed.
- An absent, null or unrecognized value stays UNKNOWN (`nil`); no inference from percentages.
- The shared `AccountController` passes these values into the existing `CodexMonitorPayloadEncoder` when a fresh read succeeds. Background and stale/cache semantics remain as they were.
- The `WeeklyUsage` decoded cache needs to remain backward-compatible with missing new optional fields.
- Windows `LocalMonitorWorker.ReadHistory()` exposes a **read-only** recent-event projection from the existing local mock ledger. It shows event type, time and mock delivery status; no provider ID is shown in the visible row.
- Windows Monitor page now displays the recent event history below the current quota snapshot using an injected history reader. Existing account switching and settings UI are preserved.
- macOS SwiftUI popover adds a **Usage Monitor** page showing each account's current 5-hour/weekly remaining percentages, reset credits and stale/UNKNOWN status using the existing `AppModel.usageStates`. It uses the original refresh method and does not initiate an account switch. This macOS view is display parity only: no macOS persistent mock outbox or Telegram connection is claimed.

## Evidence

Official schema:
- https://github.com/openai/codex/blob/main/codex-rs/app-server-protocol/src/protocol/v2/account.rs
- https://github.com/openai/codex/blob/main/codex-rs/app-server-protocol/schema/json/v2/GetAccountRateLimitsResponse.json
- https://learn.chatgpt.com/docs/app-server

New Swift synthetic tests use fixture responses to confirm both the present and missing fields, without credentials or external Codex calls.

Local Windows results:
- WPF Release build: 0 warnings, 0 errors.
- Windows monitor persistence/transport/history suite: **55/55 PASS**.
- Windows original UI + monitor history fixture checks: PASS.
- Node regression tests: 48 PASS / 1 SKIP / 0 FAIL.
- `git diff --check`: PASS.

The GitHub Windows/macOS Swift CI on this change must pass before marking the source complete. No app installation, original GitHub edits, account switching, reset-credit redemption, Telegram transmission, Pages/Release publication or main-branch application merge occurred.
