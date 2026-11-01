# User-owned GitHub routing migration — 2026-11-01 KST

Status: SOURCE_ROUTING_MIGRATED / WINDOWS_OFFLINE_VERIFIED / PERSONAL_RELEASES_0 / PAGES_NOT_CONFIGURED / NATIVE_SWIFT_FULL_TEST_PENDING.

## Ownership and provenance

- Writable Git `origin`: https://github.com/sirious0911/codex-account-switcher.git
- Reference original: https://github.com/liuzhao1225/codex-account-switcher
- This is a fork of Zhao Liu's MIT project. Preserve LICENSE, npm/LICENSE, upstream author metadata and all original copyright notices.
- No attempts to modify the original repository. The changes are made only in the personal fork development branch.

## Active source changes

- Windows native update metadata and release links: `sirious0911/codex-account-switcher`.
- Windows .NET assembly `RepositoryUrl`: user-owned fork.
- macOS SwiftUI forecast user-agent project URL: personal fork.
- macOS `SUFeedURL`: `https://sirious0911.github.io/codex-account-switcher/updates/macos/appcast.xml`.
- macOS release feed loader reads releases and signed feed XML **only** from `sirious0911/codex-account-switcher`; no personal release results in no generated feed.
- npm helper package: `@sirious0911/codex-account-switcher` (name reserved in source; registry publishing **not performed**). Its runtime release downloads come from package `repository.url`, now personal fork. The original creator remains attributed in npm author metadata.
- npm postinstall guidance, source smoke tests and GitHub Actions generated package path are user-owned.
- GitHub website source, site canonical links, sitemap, `llms.txt` and routing checker are migrated to the personal Pages URL, **but Pages has not been set up**.
- README in Korean / Chinese and npm README distinguish personal source and unpublished release artifacts; provenance is retained.
- `CITATION.cff` points to the personal source; original author remains attributed.

## Remaining historical upstream references

- LICENSE, npm/LICENSE, original author profile, commit history, historic release/research docs, original creator biography and original attribution links remain deliberately unchanged. They are source evidence/credit **not download or update endpoints**.
- Existing platform bundle identifier and original UI label are preserved for compatibility in this step; not a new ownership claim.
- Older original website textual descriptions in `site/**` may still describe the upstream project's published releases. The personal-fork banner clarifies that no personal EXE/DMG is published. Further public-site editorial cleanup is separate from actual source routing.

## Verification

- On 2026-11-01 KST: `node --test scripts/tests/*.test.mjs`: 30 PASS / 1 SKIP / 0 FAIL.
- `node scripts/check-site-geo.mjs`: 16 HTML pages and 16 sitemap URLs PASS.
- Windows native WPF Release build: success / warning 0 / error 0.
- Windows UI synthetic checks: PASS.
- Windows mock monitor checks: 27/27 PASS.
- `npm pack ./npm --ignore-scripts --dry-run --json`: verified scoped archive filename `sirious0911-codex-account-switcher-0.2.1.tgz`.
- Personal GitHub Releases API: 0 releases.
- Personal GitHub Pages API: 404 (not configured).
- Fork reference regression tests: five synthetic cases, no external account or Telegram actions.

## Next prerequisites (not done here)

1. Build and run full Swift regression on an appropriate toolchain.
2. Create a first personal GitHub Release containing our Windows EXE/macOS DMG and both checksums; do not copy unchanged upstream binaries under our fork label.
3. For macOS automatic updates, sign the feed/DMG using an authorized personal key matching the app's embedded public verification key. The currently retained original Sparkle public key is **not a personal signing credential** and does not establish readiness for forked updates.
4. Configure personal GitHub Pages and verify actual HTTPS availability; a generated URL alone does not prove it is live.
5. Publish npm package to the user's accessible npm scope **only if explicitly requested**, then verify real installation.
6. Keep native app account switching and Telegram actions unchanged until their functional integration is tested; no additional security-blocking feature is introduced.

All changes here are source routing and documentation only. No upstream mutation, personal release, Pages enablement, npm publish, production install, Codex login/account change, reset-credit redemption, or Telegram send occurred.
