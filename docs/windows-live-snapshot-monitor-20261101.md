# Windows usage monitor UI integration — 2026-11-01 KST

Status: WINDOWS_WPF_UI_SOURCE_IMPLEMENTED / OFFLINE_TESTS_PASS / USER_ACCOUNT_LIVE_NOT_TESTED

## Scope implemented

The personal Codex account-switcher fork now exposes a separate **Usage Monitor** page directly from the existing Windows accounts window.

- A monitor icon opens the page, preserving the original compact account list, management and account switch confirmation UX.
- `MonitorOverview.From(AccountSnapshot)` projects the existing Swift-host state that `CoreClient` already receives. It does **not** require a second Codex login, credential read, monitor polling request, subprocess or a second usage provider.
- Each account shows: 5-hour remaining quota + reset timestamp when reported; weekly remaining quota + reset timestamp; reset-credit count (including real zero versus UNKNOWN); active account marker if confirmed.
- Missing usage/window/credits remain UNKNOWN. When a snapshot contains a usage error, the page labels it as last-known data instead of pretending it is a fresh provider reading.
- Refresh button invokes the existing `refresh` command from the Swift host. Opening the page does not switch accounts or send extra commands.
- English and Simplified Chinese labels added to the shared Swift localization table, preserving the upstream's original supported language set.
- Existing manual account selection, confirmation screen, automatic reset-credit option, and the user's personal fork update URL are unmodified.

## Direct Windows tests

- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo`: PASS, 0 warnings, 0 errors.
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release`: PASS; original settings/account-selection checks plus isolated monitor-page checks.
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release`: **37/37 PASS** (previous 27 parser checks plus 10 new account snapshot/monitor projection checks).
- `node --test scripts/tests/*.test.mjs`: 30 PASS / 1 SKIP / 0 FAIL.
- `node scripts/check-site-geo.mjs`: 16 HTML pages and 16 sitemap URLs PASS.
- `git diff --check`: PASS.

Tests used synthetic account snapshots, not operational credentials.

## Functional boundary

This milestone is **real WPF presentation binding to the existing Swift account state**. It is not a completed integration of the separately developed Node event-detection service, a persistent alert queue, cross-machine synchronization, Claude Code, or Telegram messages.

The established upstream Codex Desktop manual switch/rollback logic remains unchanged. The GitHub native Windows integration workflow already tested isolated transport and Desktop close fixtures; this milestone independently checks that navigating to usage monitoring does not invoke account switching.

No GitHub Release, local installation, login, account transfer, actual reset-credit use, Telegram send, or user credential modification was performed. The new feature remains on the personal development branch Draft PR.
