# P0: Freeze all upstream self-update entry points

Date: 2026-11-01 KST

Status: SOURCE_BLOCKED / WINDOWS_OFFLINE_PASS / MACOS_NATIVE_UNVERIFIED / PRODUCTION_NO_GO

## Finding and risk

The personal fork deliberately disables automatic reset-credit consumption. However, the inherited Windows updater originally polled GitHub Releases belonging to liuzhao1225, and the macOS Sparkle updater pointed to the original upstream feed. Installing a newer upstream package could replace the fork and thereby remove the fork's safety controls. This is a material deployment regression risk, independent of the correctness of the reset-credit RPC block.

## Remediation

- Windows NativeSettings: Automatic updates hard-false, setter inert; CheckUpdatesAsync completes without network or release lookup; OpenUpdate inert.
- Windows application startup: no hourly update timer and no startup check.
- Windows settings: no update enable/check/install UI, current version still visible.
- macOS AppUpdater: ObservableObject-compatible inert implementation without importing or starting Sparkle. No feed request, download or install.
- macOS settings: no update enable/check/install UI; current version still visible.
- Swift package manifest: Sparkle dependency and target linkage removed.
- macOS application packaging: no embedded Sparkle framework, SUFeedURL, automatic checks, updater signing loop or upstream feed metadata.
- Mac updater tests: replace network/error propagation expectations with disabled update behavior.
- Windows UI checks: assert enable attempts are ignored, CheckUpdatesAsync cannot reveal an install URL, and OpenUpdate is inert.
- Node source security tests: three extra fail-closed checks to detect updater reintroduction.

## Direct verification

- Windows repository Node checks: 33 PASS, 1 SKIP, 0 FAIL.
- Windows .NET 10 WPF Release build: successful, warning 0, error 0.
- Windows WPF synthetic UI and updater no-op runtime assertions: PASS.
- git diff --check: PASS.

## Important limitations

- No Windows Swift toolchain or full upstream Swift runtime suite.
- No macOS SwiftUI/AppKit compilation, no actual Sparkle bundle or app packaging run.
- Release CI packaging/test workflows for the original upstream are **not** evidence that this fork now ships a working DMG or automatic update mechanism. Release remains HOLD.
- The old upstream npm installer and documentation are retained only as source history. Do not use them to install this safety fork: they may point to upstream releases.
- This does not disable intentionally user-driven **manual** Codex account switching; user actions for LIVE credentials remain separately gated.
- No credential access, Codex app-server use, Telegram send, application install or auto-start took place in this change.

## Remaining gate

Use a controlled macOS Swift compiler to build Package.swift after Sparkle removal, run the complete native Swift tests, and verify package Info.plist has no SU update keys or embedded Sparkle framework. On Windows, build the Swift shared host with an approved isolated toolchain. Review and explicitly approve any fork-owned signed updater before permitting update UI.

Neither main merge nor production deployment is authorized.
