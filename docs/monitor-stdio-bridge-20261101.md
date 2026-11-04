# Swift usage refresh → Windows host monitor observation / Node fixture pipeline

Date: 2026-11-01 KST

## Purpose

Connect successful Swift usage refreshes to a read-only monitor observation over the **existing** SwitcherHost JSON-lines stdout channel. Use the original Node detector, copied into this personal Fork, without altering account-switching, reset-credit, or update functions.

## Implemented data path

1. `AccountController.performWeeklyUsageRefresh`: after an account's **fresh** `readWeeklyUsage` succeeds, encodes `CodexMonitorObservation` and invokes a new `onMonitorObservation` callback. Startup cache entries and stale refresh failures do not invoke this callback.
2. `SwitcherHost`: emits `{"kind":"monitorObservation","observedAtMs":...,"raw":{...}}` as an ordinary stdout message alongside existing `snapshot`, `completed` and platform messages. Does not start Node.
3. `CoreClient`: parses the new optional message and raises `MonitorObserved(CodexMonitorWireSample)` on the existing UI synchronization context. Original application message types and account switching commands remain unchanged.
4. `monitor/host-stream-preview.mjs`: an **offline** command-line receiver accepts JSON-lines data shaped like SwiftHost stdout, filters monitor samples, keeps independent account baselines and exercises the reused Node event detector and mock Monitor/Alert Bot message renderers.

The Windows desktop UI continues to display use of the existing `AccountSnapshot` as before. The `MonitorObserved` stream is an integration surface for a future local monitor worker. It is **not yet a running service** launched by the Windows desktop. No Telegram sender is connected.

## Current data availability

- Provider account ID: taken from existing account profile when present. No local UUID/email fallback.
- Five-hour and weekly quota: taken from the fresh `WeeklyUsage` result.
- Reset-credit count: taken from the fresh `WeeklyUsage` result.
- `planType` and `ordinaryUsageAllowed`: **null (UNKNOWN)**. Current `CodexClient.readWeeklyUsage` returns only the normalized `WeeklyUsage`, and no verified provider fields for the extra attributes are exposed through `AccountSnapshot`.
- Accordingly the **actual Swift-encoded samples do not trigger credit-increase or early-recovery alerts** yet, because the Node detector requires stable verified plan/account identity and confirmed permission for early-recovery classification. Synthetic fixtures with explicitly set plan/permission exercise event correctness only.

This is not an added account safety-blocking feature: the app's original capabilities remain intact. UNKNOWN is an accurate representation of information the provider adapter has not supplied.

## Verification

- Windows WPF Release build: PASS, warning 0, error 0.
- Windows monitor pure JSON + wire-contract checks: 42/42 PASS.
- Windows UI regression: PASS.
- `node --test scripts/tests/*.test.mjs`: 48 PASS / 1 SKIP / 0 FAIL.
- `node --test scripts/tests/monitor-host-stream.test.mjs`: five envelope/CLI/multi-account/UNKNOWN checks PASS.
- Swift `AccountControllerTests`: new test verifies that a fresh usage read creates one observation and an unsuccessful refresh creates none. **Full Swift CI is required before declaring this passed.**
- Windows PR workflow is extended to include the 42-case monitor transport test in addition to its existing Windows host, Swift and EXE self-test.

## Open tasks

1. Run and verify native macOS and Windows Swift CI on this commit, including the new callback test.
2. Test native SwiftHost stdout to `.NET CoreClient.MonitorObserved` with a fully isolated fake provider process (separate end-to-end gate).
3. If the provider offers documented plan and ordinary-usage attributes, add them to the read-only adapter without inferring them.
4. Build and test an installed background local worker that consumes monitor events, persists the deduplication ledger and drives the mock outbox across restarts.
5. Later, with explicit permission, connect actual Telegram Bot sending. No user login, credential mutation, actual switch, reset-credit consumption, npm publishing, GitHub Release deployment or main merge was performed here.
