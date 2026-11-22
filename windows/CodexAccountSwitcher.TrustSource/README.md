# P0-15 Windows administrative trust-source reader (read-only candidate)

This is the missing **Windows OS input adapter** for the already implemented
P0-15 signed HEAD verification. It is not a trust-store provisioner and
does **not** authorize a Codex Owner to start or send a message.

## Administrator-controlled operating contract

The Windows 64-bit registry view must contain a policy key:

`HKEY_LOCAL_MACHINE\SOFTWARE\CodexAccountSwitcher\OwnerTrustV1`

Only an operator with independent authorization may provision this key.
Required values and types are:

| Value | Windows type | Meaning |
| --- | --- | --- |
| `ApprovalHeadSpkiPem` | REG_SZ | Externally approved Ed25519 public SPKI key |
| `IssuerHeadSpkiPem` | REG_SZ | Independent Ed25519 public SPKI key |
| `MinimumRevision` | REG_QWORD | Administrator-protected monotonic revision |
| `MinimumGeneration` | REG_QWORD | Administrator-protected monotonic generation |

The registry key owner must be BUILTIN\Administrators or SYSTEM, and its
DACL must be protected from inheritance. No non-admin/non-SYSTEM trustee may
have a write, delete, change-permissions, or change-owner allow ACE.
This reader accepts only **public** keys; no private key is stored here.

The signed HEAD envelope is a bounded file at:

`%ProgramData%\CodexAccountSwitcher\OwnerTrustV1\head-envelope.json`

The file must have a protected DACL, SYSTEM/admin ownership, a single file
link, and no reparse ancestors. Bytes are obtained from a single open Windows
file handle via the frozen P0-14 `WindowsAdmissionFileEvidence` primitive.

**Important:** The registry floor and keys are re-read after the HEAD bytes
are captured. This only checks for within-call changes. The final trust
decision must still pass the *separate* P0-15 dual Ed25519 signature checker,
check the manifest/revocation status, and re-read the independent monotonic
floor after verification. The OS input reader never grants admission.

Neither the registry key nor the HEAD file is created or changed by this
project. Administrator key issuance, protected rollback-floor updates,
rotation/revocation, and rollback recovery remain **HOLD** until explicitly
designed, independently reviewed, and authorized. Do not place private
signing keys, DPAPI blobs, or user-specific credentials under the key.

## Build and read-only probe

```powershell
dotnet build windows/CodexAccountSwitcher.TrustSource/CodexAccountSwitcher.TrustSource.csproj -c Release
dotnet run --no-build --project windows/CodexAccountSwitcher.TrustSource/CodexAccountSwitcher.TrustSource.csproj -c Release
```

When the policy has never been registered, expected output:

```text
HOLD_P015_ADMIN_POLICY_MISSING
```

Exit status 2 is the deliberate fail-closed result. A source-presence
check may print `P015_PROTECTED_INPUTS_PRESENT_SIGNATURE_UNCHECKED_NO_SEND`;
this **does not** mean the signed HEAD was verified or the Windows Owner may
start, and must never be used as production dispatch authorization.

No operating trust registration, DPAPI decrypt, SQLite access, account
switch, network request, process launch, or actual greeting occurs.

## Reproducibility

The project links the Git-tracked frozen
`windows/P014WpfSource/dedicated-thread-v72-native-20261119/WindowsAdmissionFileEvidence.cs`.
It does not depend on `windows/artifacts/` or any generated binary.

The live `WindowsOwnerReadOnlyAdmission.HoldReason` remains an
**unconditional** trust HOLD; this candidate is not wired into live dispatch.

## P0-15 Windows-to-Node read-only verification inputs

The opt-in `--emit-public-trust-input-v1` diagnostic mode emits a single
strict JSON object (signed HEAD envelope as base64, **public** pinned SPKI
keys, protected revision/generation floor and HEAD file identity), only after
the Windows source reader checks administrative owner/DACL permissions.
An OS policy missing or any read failure emits only a HOLD reason to stderr.

This mode ALWAYS returns process exit code 2, even when public inputs
exist. A source-input frame is **not** evidence that Ed25519 signatures have
been verified and is never permission to start or send via Owner.

The separate `--synthetic-acl-checks` argument runs 10 in-memory SDDL
policy cases without reading the HKLM trust key or protected HEAD file.

The HEAD file itself now rejects non-administrator write/delete/owner/DACL
allow ACEs, in addition to owner, protected DACL, file identity and single
link conditions. This ACL check is from the same open file handle before and
after reading the bytes.

A future Node reader may inspect these public verification inputs using
`makeWindowsNativeSignedHeadReader`; the **diagnostic** native launcher
must have four independently verified file digests: EXE, DLL,
`.deps.json` and `.runtimeconfig.json`. Pinning the EXE alone cannot
secure the managed application. The current build's file hashes are not
independent operator-issued trust anchors.

No real signature/root issuance, CurrentUser DPAPI, operating SQLite,
Windows product Gate or actual greeting is enabled by this command.

## Handle-relative, non-creating HEAD open (2026-11-21)

The Windows HEAD reader now opens the operating system's ProgramData
directory handle, then `CodexAccountSwitcher`, `OwnerTrustV1`, and
`head-envelope.json` by **NT handle-relative** lookup with
`OBJ_DONT_REPARSE`. It requires the application-managed parent directory
handles to be owned by SYSTEM/Administrators, to have protected DACLs, and to
deny nonadministrator write/create/delete/ACL-change permissions. Open
handle file IDs and security descriptors are rechecked after reading HEAD.

Important bug fix: Win32 `OPEN_EXISTING = 3` is **not** the NT
`FILE_OPEN = 1` disposition. In `NtCreateFile`, numeric 3 means
`FILE_OPEN_IF` (can create a missing directory). This was reproduced in a
fresh disposable temp directory and changed to `NtFileOpenOnly = 1`.
The two values are named separately; the synthetic native fixture checks
that missing path components are rejected **without creating any child**.

```powershell
dotnet run --project windows/CodexAccountSwitcher.TrustSource/CodexAccountSwitcher.TrustSource.csproj -c Release -- --synthetic-relative-head-checks
```

Expected: `P015_RELATIVE_HEAD_SYNTHETIC_PASS tests=2 operatingTrustWrites=0 actualMessages=0`.

Only a negative fixture path is tested; no administrator-provisioned positive
HEAD/registry or device-independent root has been created. Positive operation
and Windows Owner admission are still HOLD. File handles used by the relative
reader are not executable/dispatch capabilities.
