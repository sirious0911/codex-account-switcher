# Codex Account Switcher Personal — active-only Windows IPC handoff

Date: 2026-11-02 KST

STATUS: ACTIVE_ONLY_IPC_SOURCE_PASS / WINDOWS_SYNTHETIC_GATE_85_PASS / SWIFT_BUILD_PENDING / LIVE_DEPLOYMENT_HOLD

## Incoming handoff

- CodexPro development #14 reported CROSS_PROCESS_INTEGRATION_PASS and 377/377 synthetic tests.
- Incoming SHA256: 99f9b5b1b4170447157ca9ce8eca9a6e3870a2da519cb8d6e6a0c27a287b130f.
- The independent receiver cannot infer the current selected account when receiving several profiles and therefore clears mixed-profile values to UNKNOWN.

## Personal fork implementation

- Sources/SwitcherCore/CodexMonitorPayloadEncoder.swift: attach local profileID UUID to the internal observation struct. The raw provider quota JSON is unchanged.
- Sources/SwitcherHost/Host.swift: attach profileID to the existing SwiftHost stdio monitorObservation envelope. No extra App Server calls.
- windows/CodexAccountSwitcher.Core/CodexMonitorWireSample.cs: parse optional profileID as Guid; legacy synthetic events remain usable by the original local worker but cannot publish externally without profile correlation.
- windows/CodexAccountSwitcher.Core/ActiveMonitorPublicationGate.cs: the external stream requires an exact matching active profile UUID, ActiveIdentityConfirmed=true, IsMutating=false, and no pending switch.
- windows/CodexAccountSwitcher.Core/CoreClient.cs: synchronously signal a switch request before the host switch command.
- windows/CodexAccountSwitcher/App.xaml.cs: invalidate the external sender on a switch request, active profile change or failed identity confirmation; the existing local monitor worker still processes all profiles.
- windows/CodexAccountSwitcher.Core/SwitcherLocalPipeSender.cs: Invalidate() discards pending frames, raises epoch/observed-at watermark and disconnects the pipe so the receiver immediately clears values. An in-memory control frame wakes the idle writer; epoch checks prevent pre-switch replay.
- windows/CodexAccountSwitcher.MonitorChecks/SwitcherLocalPipeSenderTests.cs: A/B/unknown/mutating synthetic gates, actual pipe disconnection/reconnection, stale replay and source-envelope parsing.
- Tests/SwitcherCoreTests/CodexMonitorPayloadEncoderTests.swift: new static test for internal profile UUID correlation, without forwarding that UUID in the raw payload. This Swift test is NOT locally executed.

## Preserved receiver wire contract

- Named Pipe: codex-usage-monitor-switcher-v1.
- External pipe JSON still contains exactly three top-level fields: kind, observedAtMs, raw. The new internal profileID is deliberately not exposed to the independent receiver.
- Raw metrics: rateLimitsByLimitId.codex.primary (5H), secondary (7D), rateLimitResetCredits.availableCount.
- No independent usage request, browser login, account switch, reset-credit redemption or Telegram sender was added.

## Local verification

- Windows Release build: PASS; warnings 0, errors 0.
- Windows MonitorChecks: 85/85 PASS, real reads 0, real switches 0, Telegram sends 0.
- Windows UI checks: PASS.
- Node tests: 51 PASS, 1 SKIP, 0 FAIL.
- git diff --check: exit code 0 (line ending normalization warnings only).
- Local Swift toolchain: NOT INSTALLED; swift test returned exit 127. Swift compilation and Swift tests are pending, not PASS.
- No updated portable EXE packaging/install, real SwiftHost-to-.NET-to-#14 active-profile integration, Git Commit/Push, Work Window or production deployment.

## Next owner and gates

1. Codex / development #14: compile the changed SwiftHost and run Swift tests on an environment with a working Swift toolchain. Verify profileID flows through the stdio envelope to Windows parser.
2. Development #14: perform a new synthetic two-process test with the actual latest active-only source: verify A only, switch A-to-B invalidation to UNKNOWN, B only, no A replay, and original multi-profile usage refresh retained.
3. Only after source and synthetic gates pass: separately approve real app launch/install and live account switching. The independent minibar must not issue its own Codex rate-limit read.
4. Actual account switching, production deployment and public release remain on HOLD; Claude Code remains excluded.

CodexPro development #14 workspace has not been modified by this work.
