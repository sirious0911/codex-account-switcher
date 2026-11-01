# macOS native persistent monitor ledger — 2026-11-01 KST

Status: MAC_SOURCE_IMPLEMENTED / SYNTHETIC_TESTS_ADDED / CI_GATE_PENDING / LIVE_NOT_DEPLOYED

## Scope

The personal `sirious0911/codex-account-switcher` development branch now contains a pure Swift, macOS-native, in-process monitor ledger (`Sources/CodexAccountSwitcher/MacMonitorLedger.swift`). It requires neither Node.js nor a .NET installation on a user's Mac.

The macOS `AppModel` subscribes to the existing `onMonitorObservation` callback emitted **only** when `CodexClient.readWeeklyUsage` succeeds. It writes an account-specific local JSON ledger in the app's user Application Support directory and updates published in-memory event history. The macOS Usage Monitor popover shows that saved mock event history (event kind, observed time, mock delivery status) below the existing usage summaries.

The implementation is entirely local: no additional Codex API requests, account switching, reset-credit consumption, Telegram token, HTTP transport or message sending. Existing Codex client, manual account switching and auto-reset settings are unchanged.

## Decision parity with Windows .NET monitor

The macOS detector mirrors the already-developed `monitor/dotnet-core/UsageEventState.cs` rules:

- The first verified account+plan observation is a silent baseline.
- A change must be observed twice with distinct, increasing timestamps before confirmation.
- Observations older than the baseline are ignored; gaps longer than 24 hours rebaseline.
- The event kind and stable SHA256 key are composed from provider account ID, verified plan, event kind, lane, reset anchor and invariant numeric values.
- Credit increase is eligible once account/plan identity is verified. A 5-hour or weekly early recovery additionally requires explicit `ordinaryUsageAllowed == true` on both observations.
- No plan or no verified account identity means no event. Unknown permission does not authorize recovery events.
- A previously emitted key never becomes a second event; past emitted keys and local mock-completion status are saved across app restarts.
- All state paths use SHA256 account digests, not raw provider IDs, and writes use Foundation atomic file replacement.

The emitted history's `mockCompleted` field means **a local mock record completed**, not that Telegram delivered anything. This is not live bot delivery.

## Tests

New `Tests/CodexAccountSwitcherTests/MacMonitorLedgerTests.swift` covers:

1. Credit increase after two readings, stable Windows-compatible SHA256 key, mock-only completion, restart replay and stale readings.
2. Early recovery after two readings and matching Windows event key.
3. Unknown plan and multiple accounts.
4. Transient changes and observations separated by more than 24 hours.
5. Corrupt saved state must fail instead of silently overwriting or losing event history.

Previously verified Windows monitor suite: 55/55 PASS, Windows UI PASS, Node tests 48 PASS / 1 SKIP.

The new Swift tests and menu UI **must be compiled and executed by the macOS GitHub CI** on the current commit before PASS can be claimed. Full Windows CI must also continue to pass.

## Remaining

- True abrupt power-loss and simultaneous-process write ownership have not been proven on macOS. The app is expected to run a single instance.
- macOS does not expose a sender or Telegram bot and does not share physical state directories with Windows; the rule set and key format are aligned, not the machine-specific storage.
- Real user plan/permission values have not been read. If the provider reports UNKNOWN, no event should be invented.
- No distribution build signing/notarization, macOS/Windows user installation, real Codex account action, GitHub Release or main-branch application merge is performed.
