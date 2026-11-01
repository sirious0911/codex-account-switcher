# Codex usage monitor — personal fork shared offline engine

Date: 2026-11-01 KST

This directory contains the existing, **unchanged** Node offline reference event detector and mock outbox from the user's separate `codex-usage-monitor-prototype` project. Both have been copied into the personal `sirious0911/codex-account-switcher` repository so the fork can build and test them without depending on files outside the repository.

## Reused source files

- `offline-core.mjs`: existing pure normalization, quota/recovery event detection, duplicate key derivation, durable test-state helpers and mock message formatting.
- `mock-outbox.mjs`: existing mock-only delivery reservation, acknowledgment and single-owner simulation.
- `tests/*.mjs`: three original test files (45 event cases, 13 mock-outbox cases, 4 Node parity golden checks).
- `synthetic-pipeline.mjs`: new **in-memory** adapter using the original detector and outbox. Never reads real Codex profiles and never sends a message.
- `mock-telegram-routing.mjs`: format-only role labels for `MOCK_MONITOR_BOT` and `MOCK_ALERT_BOT`; no bot tokens, chat IDs or external calls.

Source bytes at initial migration:

- Original and copied `offline-core.mjs`: SHA256 `46300b900dc7c7b72bc47e61d470f5c5a3b737ed2d9d3aadd35af0768b0db662`
- Original and copied `mock-outbox.mjs`: SHA256 `61e0fac36148bcfaf45268cc90dfb5ff834ff7da49823aef13fdea9453c41af8`

The external prototype repository was not modified. Further work should use the version in this personal Fork as the canonical reference for new integration, rather than changing the old monitor to diverge independently.

## Tested data contract

The standalone Swift `CodexMonitorPayloadEncoder` defines a JSON contract with `accountId`, `ordinaryUsageAllowed`, `rateLimitsByLimitId.codex`, `planType`, `primary` (5h), `secondary` (7d), and `rateLimitResetCredits`. The new integration tests create synthetic readings with that shape and run them through the original Node normalization, event detector and mock outbox.

**Important live-data gap:** The current `AccountSnapshot` / `WeeklyUsage` interface exposed to the Windows UI does not include a verified `planType` or `ordinaryUsageAllowed`. The Swift encoder accepts those as **optional external inputs**, but the application has not yet integrated an authoritative producer. Therefore a real early-recovery event cannot be inferred from the existing Windows UI snapshot alone. Neither a plan name nor usage permission is invented.

## Read-only integration tests

```shell
node --test scripts/tests/*.test.mjs
node monitor/tests/offline-tests.mjs
node monitor/tests/mock-outbox-tests.mjs
node monitor/tests/parity-tests.mjs
```

The fork's standard `scripts/tests/*.test.mjs` runner includes the full original monitor cases through `monitor-existing-engine.test.mjs`; these tests also execute on the fork GitHub Actions Node CI.

- Existing detector cases: 45/45 PASS.
- Existing outbox cases: 13/13 PASS.
- Existing Node parity golden checks: 4/4 PASS.
- New fork synthetic engine + mock two-bot routing tests: 10/10 PASS.
- No actual Telegram messages, Codex API calls or account changes.

## Remaining integration

- Provide verified source values for plan and ordinary-usage permission if available from the service; unknown remains unknown.
- Link the Swift usage observation producer to this event service through a local cross-platform transport suitable for both Windows and macOS, with automated fixture integration.
- Decide where to persist the event ledger and who owns Telegram delivery for each installed machine; then test restart and duplicate message handling with mock destinations.
- Only after the above, add actual Telegram connections and app UI notifications by separate user instruction.

No separate security-blocking feature was added to the original Codex account switcher, and its manual account switching, optional reset-credit and self-update features remain intact.
