# Personal fork — native-device verification checklist (Windows / Mac mini)

Updated: 2026-11-01 KST

Scope: This is a **pre-release verification protocol**, not proof of a live install or approval for live Codex account switching. It is intended for the user's personally owned Windows PC and upcoming macOS device. The original application and its saved accounts must remain untouched.

## Evidence available before touching a real account

- Repository: https://github.com/sirious0911/codex-account-switcher
- Development branch: `feature/provider-extensibility`; existing Draft PR #1.
- Windows GitHub Actions archive: `Codex-Account-Switcher-windows-x64` (portable EXE and SHA256).
- macOS GitHub Actions archive: `Codex-Account-Switcher-macos-adhoc-preview` (.app ZIP and SHA256; 14-day retention).
- Official releases: none as of 2026-11-01.
- macOS `.app` is **ad-hoc signed, not notarized**. The preview is a test artifact, not a finished installer.
- CI validates Windows native host and WPF app; macOS Swift UI and app bundle; synthetic account switching and restoration; and local mock-only event ledgers.

## Phase A — Safe package / side-by-side inspection (read-only)

### Windows

1. Download the exact named artifact from the linked GitHub Actions run; independently verify its SHA256 checksum. Do not download an unverified similarly named executable.
2. Before executing, confirm that the original program's account data and its startup registry entry are not under the **personal fork** paths:
   - fork app data: `%LOCALAPPDATA%\Codex Account Switcher Personal\`
   - fork runtime cache: `%LOCALAPPDATA%\Codex Account Switcher Personal Runtime\`
   - fork startup value: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CodexAccountSwitcherPersonal`
   - fork application setting: `HKCU\Software\Sirious0911\CodexAccountSwitcher`.
3. The packaged EXE supports `--self-test`, which runs with isolated directories and synthetic data instead of a normal app login. This CI mode is already exercised on Windows GitHub Actions.
4. No real application startup, account switch, registry change, installation or signing-bypass is required for this phase.

### macOS

1. Download the ad-hoc preview ZIP and check the accompanying SHA256 file.
2. Inspect `Contents/Info.plist` for Bundle ID `com.sirious0911.codex-account-switcher`, name `Codex Account Switcher Personal.app`, and self-owned `SUFeedURL`.
3. Verify the app's bundle code signature with `codesign --verify --deep --strict`.
4. Confirm that the original `Codex Account Switcher.app` (if installed) has a different Bundle ID, and the personal app's default account directory is `~/Library/Application Support/Codex Account Switcher Personal/`.
5. The ad-hoc preview **will not provide the same Gatekeeper experience as a notarized Developer ID app**. Do not disable Gatekeeper or bypass macOS protections merely to run this unsigned preview.
6. Do not launch the app against the user's real `~/.codex` during this read-only inspection.

## Phase B — Isolated synthetic switch / restoration (CI automated, no real Codex login)

- Existing cross-platform `LocalSwitchIdentityTests` exercise real `AccountStore` filesystem data with synthetic `auth.json` fixtures and a no-op desktop controller.
- New tests assert that two independent app data roots retain their own saved account records, while the **explicitly test-injected synthetic active Codex home** changes when switching profiles.
- Injected wrong target identity must fail in `verifyTargetIdentity`, restore the original active synthetic credentials and preserve the original app's stored profile and registry.
- Account identities and fake credentials are test-only values. No real tokens, service API calls or desktop process terminations are used.
- Continuous validation: both macOS Swift `swift test` and Windows Swift `swift test`, plus Windows WPF/UI/native-host/EXE tests.

## Phase C — Actual read-only account information check (separate deliberate live step)

**Not executed or authorized by this document.**

Before any real application launch, verify original user account backups, supported Codex CLI/Desktop versions, account ownership and how to exit/restore. Only after a separate explicit user decision should the team read a *single account's* `account/rateLimits/read` response to confirm plan, permission, 5-hour and weekly quota and credits. Never export raw auth tokens into logs, screenshots, Git commits or Chat Bridge.

A live usage read may still launch the native Codex app-server and synchronize account details, so it is not equivalent to a passive file inspection. Stop if unexpected account mutations or desktop restarts occur.

## Phase D — Live account switch / return (separate high-impact user-approved step)

**Not performed in this task.**

Perform only if the user explicitly approves switching their real active Codex Desktop login. Establish current-account identity, preserve original credentials, close the correct desktop instance, select the target account, confirm identity, then switch back. Validate original account restoration, session history preservation and no modification to original switcher app data. Do not proceed if any unexpected identity appears.

## Phase E — Publication (not performed)

Publishing a public personal GitHub Release requires a release checklist, an approved version tag, matching Windows and macOS assets/checksums, correct per-platform app identifiers, valid personal macOS Developer ID signature and notarization, and a signed Sparkle update feed. The existing personal GitHub repository currently has zero Releases. CI preview artifacts are not release assets.

## Explicit boundaries

- Do not edit the upstream repo.
- Do not merge Draft PR to `main` without a separate decision.
- Do not use Work Windows.
- Do not introduce security bypasses or extra account feature blocks.
- Preserve the fork's optional reset-credit and updater capabilities.
- Keep Telegram in mock-only mode until a separate permission to send real messages.
