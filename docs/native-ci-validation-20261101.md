# Personal fork native CI verification — 2026-11-01 KST

## Scope

User-owned source repository: https://github.com/sirious0911/codex-account-switcher

Verified application source commit:
`f34fda087d013d2ba7cc3bd248bc2f21a1ee50ae`
on `feature/provider-extensibility`.

The *verification workflow itself* is separately committed on the user-owned fork's `main` branch; it checks out the development branch explicitly, and is never a release/publishing workflow.

## Completed CI evidence

### CI run 1 — entire source regression PASS

https://github.com/sirious0911/codex-account-switcher/actions/runs/41276124136

- macOS 26 runner, Swift 6.3.3 `swift test`: XCTest 20 passed, Swift Testing 126 tests passed, failures 0.
- Windows GitHub runner: WPF Release build PASS, UI synthetic checks PASS, monitor checks 27/27 PASS.
- Ubuntu runner: Node 30 PASS, 1 SKIP, 0 FAIL, site URL consistency PASS.
- All three GitHub Actions jobs concluded SUCCESS.

### CI run 2 — app-bundle packaging PASS

https://github.com/sirious0911/codex-account-switcher/actions/runs/41276608742

- Three jobs SUCCESS including macOS full `swift test`.
- `scripts/package-local-app.sh` created `Codex Account Switcher.app` on the ephemeral macOS arm64 runner.
- Ad-hoc signing and `codesign --verify --deep --strict` succeeded.
- Package `SUFeedURL` was read directly from `Info.plist` and matched `https://sirious0911.github.io/codex-account-switcher/updates/macos/appcast.xml`.
- This is **not** an Apple Developer ID signature, notarization, DMG Release, or installed production application.
- Default Windows runner did not have the Swift compiler installed; Windows Swift `SwitcherHost` was not verified in this run.

## CI run 3 — Windows native Swift PASS

https://github.com/sirious0911/codex-account-switcher/actions/runs/41276764453

- Four GitHub Actions jobs concluded SUCCESS.
- Ephemeral GitHub Windows runner installed Swift 6.4.1; no user Windows host SDK installation.
- `swift build -c release --product SwitcherHost` PASS.
- Windows native `swift test`: 20 XCTest tests plus 74 Swift Testing cases PASS, failures 0.
- The Swift Windows SDK emitted non-fatal `wchar_t` diagnostic warnings. This run is not a warning-free native build claim.
- Other jobs: macOS Swift full test and temporary app packaging PASS, Windows WPF/monitor PASS and Node/site PASS.
- The test compiled the real personal-fork development branch source commit `f34fda087d013d2ba7cc3bd248bc2f21a1ee50ae`.

## Full Windows self-contained EXE validation — PASS

The repository's native PR-triggered Windows workflow completed SUCCESS:

https://github.com/sirious0911/codex-account-switcher/actions/runs/41277026115

- Installed Swift 6.3.3 on an ephemeral GitHub Windows runner.
- Rebuilt WPF UI and checked normal Codex Desktop close / Store package path refresh with isolated fixtures.
- `swift test` executed the Windows shared core suite successfully; `SwitcherHost` Release build succeeded.
- Native transport and shared storage checks: PASS.
- Windows WPF UI fixture checks: PASS.
- `scripts/package-windows.ps1` produced a single self-contained `Codex-Account-Switcher-windows-x64.exe` and SHA256 checksum file.
- The published workflow's `--self-test` launched the EXE in an environment with Swift and .NET SDK paths removed and reported: `PASS: bundled .NET, extracted Swift runtime, shared host and isolated settings.`
- Actions artifact `Codex-Account-Switcher-windows-x64` uploaded, artifact ID `13072035360`, ZIP size `96664949` bytes.
- This is a **CI test artifact**, not an official GitHub Release, code-signing certificate, or proof of actual Codex account switching.

Other automatic PR workflows:
- GitHub CI macOS tests and site: https://github.com/sirious0911/codex-account-switcher/actions/runs/41277026164 — SUCCESS.
- npm installer synthetic CI Windows/macOS: https://github.com/sirious0911/codex-account-switcher/actions/runs/41277026271 — SUCCESS.
- All changes to main were CI workflow definitions, **not an application source merge**.

## Unchanged operational boundaries

- The user's original GitHub repository remains unmodified.
- Existing upstream reset-credit and update features were restored and not blocked in this fork.
- Real Codex accounts, credentials, account switching, credit use, Telegram and updater network requests were not exercised by these synthetic/CI tests.
- No GitHub Release, npm publish, personal Pages deployment, main merge, user EXE installation or production macOS app installation was performed.
- Personal repo still requires a real signed Windows native Swift runtime before producing a distributable Windows EXE, and a personal Apple signing/notarization setup before public Mac release.
