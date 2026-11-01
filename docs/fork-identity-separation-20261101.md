# Personal fork identity separation — 2026-11-01 KST

## Why this change is necessary

The user's personal fork is separate from the original GitHub repo, but its earlier Windows/macOS builds still reused several upstream runtime identifiers and installer targets. Running the original and personal applications on one computer could therefore overwrite each other's .app files, local app data, registry startup settings or Windows runtime caches.

This change separates **the account switcher application**, not the underlying Codex Desktop login. Both applications still intentionally target the active `CODEX_HOME` (or `~/.codex`) when actually switching Codex accounts.

## New personal-only identifiers

| Purpose | Personal fork |
|---|---|
| GitHub source and updates | `sirious0911/codex-account-switcher` |
| macOS Bundle ID | `com.sirious0911.codex-account-switcher` |
| macOS installed .app | `Codex Account Switcher Personal.app` |
| macOS app data | `~/Library/Application Support/Codex Account Switcher Personal/` |
| macOS event ledger | app-data `usage-monitor/` |
| Windows default app data | `%LOCALAPPDATA%\Codex Account Switcher Personal\` |
| Windows Swift runtime cache | `%LOCALAPPDATA%\Codex Account Switcher Personal Runtime\` |
| Windows default install directory | `%LOCALAPPDATA%\Programs\Codex Account Switcher Personal\` |
| Windows installed EXE | `Codex Account Switcher Personal.exe` |
| Windows Start-menu shortcut | `Codex Account Switcher Personal.lnk` |
| Windows settings registry | `HKCU\Software\Sirious0911\CodexAccountSwitcher` |
| Windows startup entry | `CodexAccountSwitcherPersonal` |

Existing release downloadable **asset filenames** remain `Codex-Account-Switcher-windows-x64.exe` and `Codex-Account-Switcher-macos-arm64.dmg` to retain the personal repository's existing release contract. The installed app name and identity are different.

The fork's `AccountStore()` defaults to its own app-data path and does not automatically move the original app's legacy `Codex Account Switcher Lite` account records. Explicit test-injected `baseURL`/legacy behavior is preserved. No original installation is modified by the fork installer.

## CI and offline tests

- `scripts/tests/fork-install-identity.test.mjs` covers the bundle identity, Windows data/runtime/startup paths, distinct npm install targets, and an original-app preservation fixture on macOS.
- Existing npm installer tests use the personal app name and Bundle ID; original installer behavior (version checks, upgrades, rollback, side-by-side unrelated file preservation) remains.
- macOS preview CI validates `CFBundleIdentifier`, executable, personal update feed, ad-hoc bundle signature and archive checksum.
- Unified release CI checks the mounted .app's Bundle ID before packaging, if a future release is explicitly approved.
- Full Windows WPF/UI/monitor, macOS Swift/core and npm tests are required for completion.

## Deliberate limits

This is identity separation, **not a new security-blocking feature**. It does not disable original optional reset-credit behavior, account switching, updater or scheduled update options. Those functions remain in the fork.

A fork-side Codex account switch still changes the **shared Codex Desktop credential state** used by Codex CLI/Desktop (the intended purpose of the switcher). Side-by-side app installation therefore does not mean the two apps control independent Codex sessions.

No live credential login/read, account switching, Telegram send, macOS user installation, production EXE installation, GitHub Release, Pages deployment, or main-branch application merge occurred.
