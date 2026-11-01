# P0 — Disable automatic reset-credit consumption (2026-11-01 KST)

Status: SOURCE_REMEDIATION_COMPLETE / NODE_STATIC_REGRESSION_PASS / WINDOWS_WPF_PASS / SWIFT_NATIVE_RUNTIME_PENDING / PRODUCTION_NO_GO.

## Objective

The upstream switcher contained an automatic reset-credit consumption workflow via the `account/rateLimitResetCredit/consume` RPC. This fork must never redeem credits automatically or expose an approval path for that feature. Manual account selection and read-only usage reporting remain distinct and unmodified.

## Changes

1. `Sources/SwitcherCore/Models.swift`: ignore legacy persisted `autoResetEnabled=true`; constructors force false; introduce `CodexClientError.resetConsumptionDisabled`.
2. `Sources/SwitcherCore/AccountController.swift`: eliminate auto-reset startup and execution path. Compatibility setter rejects enable and leaves settings false; observation/scheduling hooks remain inert.
3. `Sources/SwitcherCore/CodexClient.swift`: consumption method immediately throws before process or RPC; no consumption RPC method string is left in the concrete client.
4. `Sources/SwitcherHost/Host.swift`: reject any legacy `autoReset` command, including enable attempts.
5. `Sources/CodexAccountSwitcher/SettingsView.swift`: remove automatic redemption setting from macOS UI.
6. `windows/CodexAccountSwitcher/MainWindow.cs`: remove automatic redemption setting from Windows UI.
7. `Tests/SwitcherCoreTests/AccountControllerTests.swift`: replace old positive redemption workflow tests with expected-denial, legacy-true ignored, and no-consumption safety tests.
8. `Tests/CodexAccountSwitcherTests/CodexClientTests.swift`: replace API consumption-success tests with the pre-launch blocked-call test.
9. `windows/CodexAccountSwitcher.UiChecks/Program.cs`: verify Windows UI never offers the previously enabled toggle, even if a simulated old setting is true.
10. `scripts/tests/no-auto-reset-consumption.test.mjs`: six fail-closed source checks.

The experimental AutoReset.swift state machine, its isolated synthetic tests and the AccountClient protocol declaration are retained for source history/testing. They are **not connected to an executable consumption path**. The fail-closed concrete client is the final defense; no network redemption can be sent through it.

## Evidence on Windows CodexPro

- `node --test scripts/tests/no-auto-reset-consumption.test.mjs`: 6 PASS / 0 FAIL.
- `node --test scripts/tests/*.test.mjs`: 30 PASS / 1 SKIP / 0 FAIL (including six new cases).
- Windows app Release `.NET 10` build: PASS, warnings 0, errors 0.
- Windows synthetic UI checks: PASS, persisted true does not display redemption toggle.
- Windows monitor preview: 27/27 PASS.
- `git diff --check`: PASS.

## Limitations

- Windows Swift SDK, Visual Studio C++ Build Tools, and Windows SDK were not installed; full native Swift host/runtime test is PENDING.
- Linux Swift isolated or whole-package tests must be reported separately, not as Windows validation.
- No real Codex app-server, credentials, auth.json, reset-credit consumer, Telegram send, auto-switch, or startup deployment was used.
- Do not merge or deploy the Draft PR without independent source and runtime review.
