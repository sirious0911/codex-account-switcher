# Codex usage monitor compatibility adapter — offline gate
Date: 2026-11-01 KST

## Status
SOURCE_EXACT2_CREATED; ISOLATED_SWIFT_20_OF_20_PASS; WINDOWS_SWIFT_FULL_SUITE_NOT_RUN; LIVE_NO_GO.

## Design
- Keep the existing independent monitor implementation and tests at [workspace:ws_5a9e0419541c71ea24a5f57c]\codex-usage-monitor-prototype unchanged.
- The new CodexMonitorPayloadEncoder.swift serializes the existing Codex AccountProfile/WeeklyUsage into the monitor's normalizeSynthetic(raw, observedAtMs) input schema.
- This is a pure in-process data transformation: no Swift subprocess, HTTP, filesystem access, CLI execution, Telegram sending or account switching.
- The event detector and Telegram mock-outbox stay in the existing Node/.NET monitor until an explicit integration and ownership Gate. Do not create a second event detector.
- The optional five-hour window is UNKNOWN unless both percentage and reset timestamp exist.
- The account ID must come from the provider and cannot be replaced by email or a local UUID.
- planType and ordinaryUsageAllowed must come from a separately validated read-only data source. When missing, they are encoded as JSON null. No plan or permission is inferred.
- Usage percentages are transformed from remaining to used (100 - remaining), durations are 300/10080 minutes, and reset timestamps use Unix seconds.
- The result contains no email, display name, token, authentication JSON, reset-credit redemption instruction, or Telegram recipient.

## Verification evidence
- Swift 6.2.1 isolated Linux fixture compilation: 20/20 PASS (12 existing + 8 new). Exact new SOURCE and TEST SHA256 matched the files staged on Windows.
- SHA256 of CodexMonitorPayloadEncoder.swift: 3c7426f861421ce3143bbb2f889db7915b5cb3b5af03de43388ce845f1c55c2c
- SHA256 of CodexMonitorPayloadEncoderTests.swift: 8f53a012235c512df4b621bd6bd805a6e82838c70d14fe069b78b4aa4b8242bd
- Node monitor 62/62 PASS and .NET monitor 58/58 PASS independently re-executed in Windows on 2026-11-01.
- Windows WPF fork application and fake Desktop close fixture previously built/passed.
- Not claimed: full upstream Swift tests, native Windows SwitcherHost build, actual macOS app, live credential access, real Telegram sending.

## Next controlled integration
1. Run the full Swift upstream suite in a matching isolated Windows/macOS environment.
2. Collect a synthetic Swift-encoded JSON payload; replay it through existing Node normalizeSynthetic and compare golden events before connecting any IPC.
3. Design a one-way, bounded, authenticated local-only transport between the UI and monitor with explicitly configured single owner; avoid storing provider identity in logs.
4. Connect the UI only after preserving legacy work, source/test SHA, and existing test results.
5. Independent security/compatibility review and separate approval before real Codex app-server usage and any Telegram delivery.
6. Preserve two distinct Telegram channels/bots: Monitor Bot and Alert Bot. Both are HOLD here.

This adapter does not call account switching or reset-credit consumption. The original project's optional features remain available and unchanged. No binary installation, main merge, or real Telegram transmission was performed in this development.
