# Codex Account Switcher — shared Sender Authority service

**Status (2026-11-13):** Source implementation and isolated tests. **NOT
deployed, NOT integrated with live Windows/Mac notification senders, and NOT
permission to transmit Telegram messages.**

This service is a proposed single authority host for Windows and a future Mac
mini to exchange **Sender ownership and immutable event reservations**.
Both devices can continue to view their local Codex 5H/7D quotas independently.
The service never reads Codex access tokens, Telegram Bot tokens or chat IDs.

## Implemented product API

Node.js 24 standard modules only (including `node:sqlite`). A single local
SQLite WAL database holds a monotonically increasing fencing epoch, one active
Sender lease, consumed request nonces and immutable event reservations.

| Route | Signed device role | Behavior |
| --- | --- | --- |
| `GET /v1/status` | Viewer or Sender | Observe lease and authority time |
| `POST /v1/lease/acquire` | Sender | First unexpired owner wins, increments epoch |
| `POST /v1/lease/renew` | Current Sender | Refresh same epoch, max TTL 60 seconds |
| `POST /v1/lease/release` | Current Sender | Release exact current owner+epoch |
| `POST /v1/events/reserve` | Current Sender | Unique 64-hex event ID, persistent one-time claim |
| `POST /v1/events/uncertain` | Current Sender | Mark uncertain, never automatically retry |
| `GET /v1/events/<64-hex-key>` | Viewer or Sender | Observe one reservation |

Requests are canonical HMAC-SHA256 signed over device ID, method, exact path,
timestamp, random nonce, and raw body SHA256. Device identities and immutable
Viewer/Sender roles come from **explicit server-side enrollment**, not client
assertions. Signed requests must be within 30 seconds of the authority clock,
and **every accepted request nonce** is inserted into SQLite before response.
The nonce and the corresponding operation commit in the same transaction.
Repeated/invalid nonce, unknown device, invalid HMAC, stale lease, duplicate
event, malformed JSON, expired request and invalid role all fail closed.

The HTTPS listener requires a supplied private TLS key/certificate (TLS 1.3),
rejects signed header duplication and oversized body payloads, and binds to
`127.0.0.1` unless an operator explicitly configures a nonloopback address
and `allowRemote: true`. It does **not** run when its module is imported.

## Explicit provisioning (future, not run by development)

The following illustrates an operator-managed workflow, **not an instruction
to perform it before Mac and Windows authentication are approved**:

1. Install Node 24 on the intended Mac mini. Allocate a dedicated local
   disk directory (no SMB/NFS DB sharing). Configure TLS with proper
   client-side certificate/CA validation.
2. Provision a *new, empty* database **once**, explicitly via
   `provisionSenderAuthorityDatabase(databasePath)` from
   `sender-authority.mjs`. Existing DBs are refused; no migration or reset.
3. Create a private JSON config (owner-only file mode `0600` on macOS) with
   the following **schema**. No real keys are included here:

   ```json
   {
     "databasePath": "/private/local/path/authority.sqlite",
     "tlsKeyPath": "/private/local/path/server-key.pem",
     "tlsCertPath": "/private/local/path/server-cert.pem",
     "listenHost": "127.0.0.1",
     "allowRemote": false,
     "port": 9443,
     "enrollments": [
       {"deviceId": "windows-1", "role": "sender", "hmacKeyHex": "REPLACE_WITH_UNIQUE_PRIVATE_64_HEX"},
       {"deviceId": "mac-1", "role": "viewer", "hmacKeyHex": "REPLACE_WITH_DIFFERENT_PRIVATE_64_HEX"}
     ]
   }
   ```

4. After an explicit deployment approval, start with
   `node shared-authority/service/authority-https.mjs --serve /private/config.json`.
   Without `--serve` and a valid private config it will not bind a port.
   An operator may later designate a verified Tailscale IP as `listenHost`,
   but Tailscale membership **is not** per-device Sender authorization.
5. Build Windows and Mac clients that validate the HTTPS server certificate,
   create correctly signed requests and verify authenticated current epoch
   **immediately before** any operation. Wire only after an independent
   real-device safety review.

## Not implemented / operation remains HOLD

- **No TLS certificates, device HMAC secrets, or database were installed**
  on the user's real machines. No service started, port opened, or Telegram
  call made in this development task.
- The existing Windows Telegram sender **does not call this server**.
  Accordingly, the current Windows-only manual and session-ON send paths
  are *not* protected by a distributed lease.
- The Mac Sender client, trusted clock operations, key rotation/revocation,
  authenticated server enrollment, audited backup/restore, cross-device
  health checks and automatic post-reboot ownership are unfinished.
- A lease and event reservation alone **do not prove exactly-once external
  delivery**; an owner may crash or lose its lease between checking and a
  Telegram Bot API call. A complete production gate must fence each send,
  preserve uncertain outcomes, enforce owner handoff, and avoid automatic
  retries after an ambiguous response.
- Credential-bearing config files need secure backup/permissions, endpoint
  TLS pinning and a controlled provisioning/rotation process. Do not expose
  this prototype API to an untrusted network.

## Offline verification

Run locally on Windows or macOS with Node 24 and OpenSSL available:

```sh
node --test shared-authority/service/sender-authority.test.mjs shared-authority/service/authority-https.test.mjs
```

Tests create temporary SQLite databases, random synthetic HMAC keys, and a
one-day ephemeral TLS certificate for **127.0.0.1 only**. They execute
Windows→Mac→Windows lease/fencing handoff, immutable event dedup across
restart, HMAC tampering/replay/clock checks, and real TLS 1.3 loopback
requests. All fixtures are deleted; no external URL or credential is used.

## Windows .NET authenticated client (unwired)

The Windows native source now includes
\`windows/CodexAccountSwitcher.Core/WindowsSenderAuthorityClient.cs\`.
It interoperates with this server's \`CODEX_SHARED_SENDER_AUTH_V1\` HMAC
canonical frame, unique nonce and exact JSON body. It implements all seven
signed status/lease/event operations; a typed reply can carry owner, epoch,
lease expiry and immutable event state. It rejects mismatched fence epochs,
event reservation IDs, malformed/duplicate JSON, clock skew and any claim
that the response grants Telegram sending or automatic retries.

\`WindowsSenderAuthorityHttpsTransport.Create\` requires HTTPS, TLS 1.3,
a valid system CA chain and a separately provisioned SHA256 certificate pin.
No test credential, URI, certificate or server identity is installed on
the user's Windows machine. The existing Windows Telegram dispatcher does
**not** instantiate the client; it must remain default OFF for distributed
ownership until real device provisioning, trust validation, send-time fencing,
and independent operation review are complete.

The Windows WPF regression suite exercises this product client through
an injected, **non-network** synthetic HTTP handler and checks a reference
HMAC vector computed by the Node authority implementation. A successful
lease or event reservation is an *observation*, not external send permission.
