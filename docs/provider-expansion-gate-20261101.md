# Provider-neutral expansion gate (2026-11-01 KST)

Status: **SOURCE DEVELOPMENT / NOT DEPLOYED / NO REAL ACCOUNT ACTIONS**

## Repository provenance

- Upstream: https://github.com/liuzhao1225/codex-account-switcher (MIT)
- User-owned fork: https://github.com/sirious0911/codex-account-switcher
- Development branch: feature/provider-extensibility
- Upstream history, LICENSE and existing implementation remain intact.

## Implemented in this gate

- Sources/SwitcherCore/AIProviderContracts.swift: provider-scoped identity, capability vocabulary, normalized quota windows, freshness, separated read/switch protocols, alert deduplication identifiers.
- Sources/SwitcherCore/CodexUsageSnapshotAdapter.swift: pure read-only projection of Codex usage into the provider-neutral model. Missing provider ID fails closed (no email/OS-local UUID fallback).
- Tests/SwitcherCoreTests/AIProviderContractsTests.swift: synthetic-only validation.
- No Claude login, token handling, quota retrieval, desktop switching or network calls have been implemented.

## Explicit boundaries

- No operational Codex/Claude account, credential file, CODEX_HOME, login, logout or desktop process access.
- No auto-rotation and no unattended account switching.
- No reset-credit redemption; upstream AutoReset feature exists and MUST remain disabled during operational review.
- No Telegram delivery; alert identity alone does not deliver exactly once.
- No cross-OS auth sync; each device retains separate credentials.
- Previous independent prototype at C:\Codex\projects\codex-usage-monitor-prototype is preserved and not embedded into this gate.
- The existing tray behavior, account operations and updater are unchanged by this gate.

## Integration gates

1. Compile and run upstream core tests plus new tests on both Windows and macOS in isolated runners.
2. Validate normal Codex Desktop close/reopen with isolated fixtures. Existing Windows shutdown issue #29 is unresolved without device-specific proof.
3. Audit authentication-store backends, file permissions, rollback and process identity before any live handoff.
4. Reuse existing Node/.NET usage-monitor tests and golden vectors. Do not claim parity until Swift vectors pass.
5. Add Claude Code adapter only after an independent login-store and API compatibility review. Treat Claude Desktop as a separate capability.
6. Design a single active Telegram sender, durable outbox, uncertain-state reconciliation and controlled Windows-to-Mac transfer.
7. Require independent validation and explicit LIVE approval before installation, account switching, credit usage, Telegram sends or auto-start.

## Key risks

- Provider account ID can be absent; adapter intentionally refuses to invent one for cross-device deduplication.
- Upstream stores reusable credentials in files, including on macOS; a neutral model does not improve that boundary.
- Changes in service APIs can break compatibility. Adapters must report UNKNOWN/STALE, not fabricate 0%.
- Alert event key by itself is not a distributed lock or delivery receipt.
- No actual cloud account access or Telegram calls were used during implementation.

Relevant upstream issue: https://github.com/liuzhao1225/codex-account-switcher/issues/29
