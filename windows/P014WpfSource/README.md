# P0-14 WPF candidate — source-only reproducibility snapshot

This directory preserves **source-only** copies of the reviewed Windows WPF
candidate, its V7.2 source dependencies, and the Owner JS-bundle test fixtures.
It excludes all `windows/artifacts/**/bin`, `obj`, credentials, operating
enrollments, local SQLite files, and the 24 MB `SwiftRuntime.zip` packaging
archive. A working source/build/test reproduction **is not a release binary**.

## Reproduce on a Windows machine with the .NET 10 SDK

From the repository root:

```powershell
Push-Location windows/P014WpfSource/dedicated-thread-v72-native-20261119
dotnet run --project IntegratedChecks/IntegratedChecks.csproj -c Release
Pop-Location
```

Expected result: `RESULT PASS=82 FAIL=0`.

The working directory matters: integrated test 72 intentionally verifies a
separately hash-pinned Owner JS bundle from a sibling folder. The 15 `.mjs`
sources from `codex-auto-greeting-stage-20261120` are copied here for that
test, but **no Owner process or Codex send is started** by this procedure.

The `DedicatedThreadV72Candidate.csproj` builds the WPF application
candidate and may omit the optional `SwitcherRuntime.zip` embedded resource
because the packaged Swift archive is deliberately absent. Do not run, deploy,
or publish that candidate as a production application.

## Provenance and boundaries

The initial 174 source files were copied byte-for-byte from
`windows/artifacts/<relative-path>` to
`windows/P014WpfSource/<relative-path>`. Later reviewed **source-only**
patches intentionally changed four of those original 174 files:

- `dedicated-thread-cycle-starter-20261119/DedicatedThreadBinding.cs`
- `dedicated-thread-v72-native-20261119/IntegratedChecks/Program.cs`
- `dedicated-thread-v72-native-20261119/App.xaml.cs`
- `dedicated-thread-v72-native-20261119/DedicatedThreadV72Candidate.csproj`

The remaining 170 original source files retain their initial raw SHA256.
A new fifth file `SwitcherActiveProfileReadOnlyChannel.cs` was added
under the native WPF candidate. This is no longer an unchanged original
freeze: it is a staged, reproducible reviewed source with explicit patches.

The P0-16 repair reads either the older single-object binding format or
the current per-account array format. Import of a legacy single object
**always forces automatic-send master/5H/7D OFF**; only an explicit
user-driven Upsert migrates the file to an array. No existing on-disk user
binding is changed by tests or by merely reading it. Synthetic fixtures verify 82/82 integrated tests: legacy single-object
OFF preservation and explicit Upsert-only migration, plus strict array
record shape, duplicate profile/thread prevention and malformed input
rejection. Array entries with a forbidden message or wrong thread UUID
remain inspectable by the existing due/Owner admission gates, which
continue to issue their documented, specific fail-closed verdicts. The real existing binding is *not* opened
by the WPF regression suite.

The test suite exercises registration fixtures on synthetic temporary
files, not real CurrentUser DPAPI enrollment or a production SQLite
Sender ledger.

A separate, smaller `windows/CodexAccountSwitcher.AdmissionCandidate/`
project tests the P0-14 handle and SQLite fixture without the full WPF
dependency graph.

- Existing live Windows V7.2 executable remains untouched.
- `WindowsOwnerReadOnlyAdmission` remains fail-closed without an independent
  operating trust root.
- This snapshot may be included in a future commit after independent source
  review. Local staging or an index-only reproduction is **not** a clean
  remote-clone verification until a commit becomes available.
- The original `windows/artifacts/` remains ignored and unmodified.

## P0-18: live one-bit active-account proof (source only)

The reviewed **future** WPF candidate now owns a separate current-user,
same-interactive-session named pipe, created only after the Swift core
has completed initialization. It returns **only**
`PASS_ACTIVE_BINDING_MATCH_READ_ONLY` or a generic HOLD, with a
request nonce echoed to avoid stale/replayed replies. The live WPF
dispatcher checks the current `AccountSnapshot.ActiveAccountID` and
`ActiveIdentityConfirmed` against the saved profile and its account
email fingerprint, requires exactly one profile and binding, and requires
`desktop-default`/`안녕` in the saved binding, and also checks the
existing `ActiveMonitorPublicationGate.ActiveProfile` equals the live
snapshot account. That gate immediately returns null upon
`SwitchRequested()` before the asynchronous Swift account state changes.
Automatic send master/5H/7D preferences are deliberately **not** part of
this identity proof: toggling preferences cannot invalidate a valid account
match and a match **never** grants send permission.
No account ID, email, thread ID, credential, quota or message content
is returned. No login, account switch, thread start/resume, send, or
Owner activation exists in the channel. It is **not** an exclusive
Sender lease and is never a send authorization.

The endpoint uses `PipeOptions.CurrentUserOnly`, a separate name from
the existing graceful-quit pipe, a per-request timeout and a bounded
128-byte ASCII request. It is disposed at WPF shutdown. Its startup
failure cannot break core quota reporting or enable any sender. Synthetic
tests include current-profile match/HOLD and actual local pipe requests,
invalid method denial, overlength denial/closed connection.

**Important:** The running Windows V7.2 has NOT been replaced and does not
have this P0-18 endpoint. Its result is currently
`HOLD_ACTIVE_PROOF_ENDPOINT_UNAVAILABLE`. No current live Switcher
active-profile proof has been obtained. The stored `accounts.json`
active account matched the pinned profile at read time, but that saved
file is not a live-memory or running Swift core snapshot.

## P0-19: single-EXE publish input gate (source-only)

The reviewed P0-18 WPF candidate can be published to an **isolated,
non-operating** directory as a Windows x64 single executable. This is
NOT the running V7.2 binary. A normal `dotnet build` does not require
a Swift runtime and does not prove that the generated WinExe can start.

Before `PublishSingleFile=true` may proceed, the project now requires:
- An explicitly supplied, existing `SwiftRuntimeArchive` ZIP path.
- An explicitly supplied `SwiftRuntimeExpectedSha256` from a separately
  reviewed source. MSBuild GetFileHash compares the **actual ZIP bytes**
  with this SHA256.
- A canonical Windows-backslash `PathMap` mapping the real source root
  to a constant virtual location, plus `ContinuousIntegrationBuild=true`
  and `Deterministic=true`, passed to the candidate AND referenced Core
  projects.

The candidate fails the publish gate when any of these inputs are absent or
the actual archive hash mismatches. Ordinary source-only builds still work.

Example command (in a **disposable** staging directory, NEVER the installed
app's path):

```powershell
$sourceRoot = (Resolve-Path .).Path
$sourcePathMap = "$sourceRoot=C:\P019\source"
dotnet publish .\windows\P014WpfSource\dedicated-thread-v72-native-20261119\DedicatedThreadV72Candidate.csproj `
  -c Release -r win-x64 --self-contained true -o "<disposable output>" `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
  -p:ContinuousIntegrationBuild=true -p:Deterministic=true `
  "-p:PathMap=$sourcePathMap" `
  -p:SwiftRuntimeArchive="<independently reviewed ZIP path>" `
  -p:SwiftRuntimeExpectedSha256="<independently reviewed 64-hex SHA256>" --nologo
```

The 2026-11-21 P0-19 **format-only fixture** used an ignored local ZIP
from 2026-11-20 with SHA256
`2a0f46fc5dd2691254aedf54fdcd36d8a865f34bd234d1f083e00ff7dfc98ba2`.
Its embedded ZIP resource hash was independently extracted from the
published assembly and matched this input. That fixture **is not**
independently proven to have been rebuilt from the currently staged
Swift source. No Swift toolchain or signing tool was found on the test PC.

Two independent **Git-index-only** source snapshots, in different
disposable Windows directories, were published using the same ZIP SHA256
and the *Windows-backslash* source-path mapping
`<individual-root>=C:\P019\source`.

The resulting single EXE was **byte-identical** in both runs:

- SHA256 `4ec4267b0e3712bdb9f1ff3adc8bd6d9a648992595fa54853d73ddd81223ebd3`.
- 108,697,507 bytes, one EXE per isolated output directory.
- The referenced `CodexAccountSwitcher.Core.dll` was byte-identical.
- Without the correct Windows PathMap, the same source files initially
  produced two distinct EXE SHA256 values. Forward-slash path mapping
  did not normalize the compiler's Windows absolute source paths.

The preview output is UNSIGNED, reports Windows app version 0.1.26
(the same as the existing V7.2 operating file), and has NO approved
independent executable identity pin, administrator-protected P0-15
signer/floor, or CurrentUser DPAPI/SQLite Owner admission. The old and
new binaries are distinct even if the version number matches. Do not
overwrite or run the existing application or use this preview for an
actual automatic greeting. Production remains NO_GO until a fresh,
independently reviewed Swift runtime, release version, signed binary
provenance, and operating consent/admission gates are approved.

The package format check does **not** run the EXE. In particular,
`--self-test` extracts the embedded runtime under the normal user's
LocalApplicationData runtime cache; the offline format inspection avoids
that user-environment change. A future sanctioned isolated runtime test
must first define a verified fully isolated cache path.

## P0-20: Fresh Swift runtime source-bound CI preview

The 2026-11-20 staged SwiftRuntime.zip is explicitly a **V7.2 runtime
extracted from the deployed application**, not a fresh build of the current
Swift sources. It is suitable only for P0-19 *single-file format* tests.

The source-only Windows CI change at
`scripts/p020-source-bound-preview.ps1` now runs **after** the existing
`scripts/package-windows.ps1` freshly builds SwiftHost and after CI
records `SwiftRuntime.zip.sha256` and `SwiftHost-source-commit.txt`.

It requires the GitHub checkout HEAD to equal the workflow source commit,
a fully clean Git checkout (including untracked files and build scripts),
a freshly generated ZIP with the
recorded SHA256, and a single `SwitcherHost.exe` inside that ZIP whose
SHA256 equals the CI host receipt. **The ZIP and receipt come from the
same job and are not an independent source witness.** P0-20 now also finds
the actual just-built Swift release product through
`swift build --package-path <source root> -c release --show-bin-path`,
requires its `SwitcherHost.exe` to exist, and compares that file's SHA256
directly to the embedded ZIP host. Missing Swift or mismatched binaries
fail closed before producing a candidate. Only after those checks will it
compile the P0-18 WPF candidate using the same ZIP SHA pin and canonical
Windows PathMap. The resulting **UNSIGNED, NON-INSTALLABLE** single-file
candidate is held under `RUNNER_TEMP`, is never executed and is not
uploaded as a release. Non-GitHub-CI and non-runner-temp outputs are blocked. The workflow
also watches the new P0-20 script path in PR and push path filters, and
an output folder containing any unexpected file or directory is rejected.
The existing WPF synthetic 82-case suite runs from its required directory.

This is a pipeline source change only. It has **not run in the hosted
Swift 6.3.3 runner**; Swift is not installed on the current personal Windows
PC, and the locally staged changes are not a published CI checkout. CI evidence
is therefore still **PENDING**, not a Swift binary identity PASS.

The isolated local mock-`swift` positive/negative tests validate the
host-SHA comparison logic only. They used an older ZIP and a synthetic
Swift executable locator and must **not** be used to assert that a fresh
Swift compiler built the current code. Even when the hosted CI passes,
this is CI provenance, not an independent publisher signature.

CI receipt checks support same-job source/host linkage but **do not provide**
independent publisher signing, admin-protected P0-15 trust keys, a true
operator-approved monotonically protected floor, Owner/Sender permission,
or actual automatic message authorization. The installed Windows V7.2
and all send preferences remain unchanged.


## P0-21: Read-only P0-15 Windows trust observation in the staged WPF candidate

The staged P0-18 WPF source now links the existing **Windows OS-protected
P0-15 trust-source reader** through the same checked native C# modules as
`windows/CodexAccountSwitcher.TrustSource`. At candidate startup, after
the single-instance mutex is obtained, it logs a **fixed HOLD status only**,
using the read-only `WindowsOwnerReadOnlyAdmission.ProtectedTrustSourceStatus()`.

This observation uses the fixed 64-bit HKLM OwnerTrustV1 key and handle-
relative ProgramData HEAD path. The existing reader verifies Windows owner
and protected DACL conditions, but it **does not verify Ed25519 signatures**,
P0-12 enrollment, the independently protected rollback-floor history,
CurrentUser DPAPI, exclusive Sender ownership or active-profile admission.
Even when protected public inputs are present, the status is
`HOLD_OWNER_SIGNED_HEAD_AND_ENROLLMENT_NOT_ADMITTED`; missing inputs
retain the underlying `HOLD_P015_...` error. The owner/due dispatch
`HoldReason` and `IsReady` remain **unchanged unconditional HOLD**.
No private keys, email, thread identifiers, tokens or HEAD bytes are logged.

The WPF candidate links three existing code files from
`windows/CodexAccountSwitcher.TrustSource/`: ACL policy, read-only
admin trust source and handle-relative HEAD reader. **Any future clean
CI branch of this candidate must include these files and the already
bundled `WindowsAdmissionFileEvidence.cs`.** P0-20 Draft PR #2 does
not include the P0-21 changes and is not modified by this local source
development. P0-21 must receive its own scoped source review.

The WPF `IntegratedChecks` suite now includes a read-only P0-15
observation test and an in-memory *inputs present* fixture proving this
can never enable Owner; no actual administrator policy registration occurs.
The currently installed Windows V7.2 has no P0-18 active-proof endpoint.
The staged P0-21 status observation is not installed in V7.2.

```powershell
cd windows/P014WpfSource/dedicated-thread-v72-native-20261119
dotnet run --project IntegratedChecks/IntegratedChecks.csproj -c Release
```

This source-only integration is **not** an operating trust-store bootstrap
or a production release. Master/5H/7D greetings and actual sending remain OFF.
