// Optional standalone A-only bridge, never normal account-switcher startup.
// Default command is offline synthetic. Live requires an explicit two-part gate.
// No fork account registry, profile creation, auth copying or state mutation calls.
// Note: launching Codex app-server may itself write logs/cache or refresh auth;
// no strict OS-level read-only guarantee is claimed.
import { spawn } from "node:child_process";
import { readFile, stat, open } from "node:fs/promises";
import { existsSync } from "node:fs";
import { createHash } from "node:crypto";
import { createConnection } from "node:net";
import { createInterface } from "node:readline";
import { homedir } from "node:os";
import { join, resolve, isAbsolute } from "node:path";
import { assessV0149AccountRead, localCredentialAccountId, localCredentialEmail, validatedWireFrame } from "./active-a-no-copy-contract.mjs";

const mode = process.argv[2] ?? "--help";
const pathToPipe = String.raw`\\.\pipe\codex-usage-monitor-switcher-v1`;
const holdMs = 20_000;
let stage = "NOT_STARTED";
let rpcCalls = 0;
let accountReadCalls = 0;
let rateLimitCalls = 0;
let pipeWrites = 0;
let identityScope = "UNVERIFIED";
let privateReceipt = null;

// Only a new, explicit, exclusive private QA receipt is allowed to be written.
// It is never placed in the Codex auth home or normal Fork profile registry.
async function beginReceipt() {
  stage = "RECEIPT_PREPARE";
  const file = process.env.CODEX_SWITCHER_A_ONLY_RECEIPT_PATH;
  if (!file) throw Error("RECEIPT_PATH_MISSING");
  if (!isAbsolute(file) || !/\.json$/i.test(file) ||
      /[\\/]\.codex[\\/]/i.test(file) ||
      /[\\/]Codex Account Switcher Personal[\\/]/i.test(file))
    throw Error("RECEIPT_PATH_INVALID");
  try {
    privateReceipt = await open(file, "wx", 0o600);
    await privateReceipt.writeFile(JSON.stringify({ status: "STARTED", stage, sensitiveValuesIncluded: false }) + "\n");
    await privateReceipt.sync();
  } catch { throw Error("RECEIPT_CREATE_FAILED"); }
}
async function finishReport(status, reason = null, detail = {}) {
  const report = { status, stage, reason, rpcCalls, accountReadCalls, rateLimitCalls,
    pipeWrites, identityScope, accountIdVerifiedByRpc: false, ...detail };
  if (privateReceipt) {
    try {
      const bytes = Buffer.from(JSON.stringify(report) + "\n", "utf8");
      await privateReceipt.write(bytes, 0, bytes.length, 0);
      await privateReceipt.truncate(bytes.length);
      await privateReceipt.sync();
      await privateReceipt.close();
      privateReceipt = null;
    } catch {
      status = "FAILED_NO_LIVE_PASS";
      report.status = status;
      report.reason = "RECEIPT_PERSIST_FAILED";
    }
  }
  safeOutput(report.status, report.reason, report);
}

function safeOutput(status, reason = null, detail = {}) {
  process.stdout.write(JSON.stringify({ status, ...(reason ? { reason } : {}), ...detail }) + "\n");
}

function digest(bytes) { return createHash("sha256").update(bytes).digest("hex"); }
async function credentialState(path) {
  const info = await stat(path);
  if (!info.isFile() || info.size > 2_000_000 || info.size === 0) throw Error("AUTH_METADATA_UNAVAILABLE");
  const bytes = await readFile(path);
  let parsed;
  try { parsed = JSON.parse(bytes.toString("utf8")); }
  catch { throw Error("CREDENTIAL_FORMAT_UNKNOWN"); }
  const id = localCredentialAccountId(parsed);
  if (!id) throw Error("CREDENTIAL_IDENTITY_UNKNOWN");
  // Never write, print, serialize or send the token-bearing object.
  return { id, email: localCredentialEmail(parsed), sha: digest(bytes), size: info.size };
}

class AppServer {
  constructor(executable, env) {
    this.child = spawn(executable, ["app-server", "--stdio"], {
      windowsHide: true, stdio: ["pipe", "pipe", "pipe"], env,
    });
    this.waiters = new Map();
    this.error = null;
    this.nextID = 0;
    const lines = createInterface({ input: this.child.stdout });
    this.child.stderr.on("data", () => {}); // discard potentially sensitive diagnostics
    lines.on("line", line => {
      if (line.length > 1_000_000) { this.abort("OUTPUT_LIMIT"); return; }
      let value;
      try { value = JSON.parse(line); }
      catch { this.abort("INVALID_RPC"); return; }
      if (!Number.isInteger(value?.id)) return;
      const waiter = this.waiters.get(value.id);
      if (!waiter) return;
      this.waiters.delete(value.id);
      clearTimeout(waiter.timeout);
      if (value.error || !value.result) waiter.reject(Error("RPC_REJECTED"));
      else waiter.resolve(value.result);
    });
    this.child.on("error", () => this.abort("APP_SERVER_FAILED"));
    this.child.on("exit", () => this.abort("APP_SERVER_TERMINATED"));
  }
  abort(reason) {
    this.error = reason;
    for (const waiter of this.waiters.values()) {
      clearTimeout(waiter.timeout);
      waiter.reject(Error(reason));
    }
    this.waiters.clear();
  }
  send(data) {
    if (this.error || !this.child.stdin.writable) throw Error("APP_SERVER_NOT_READY");
    this.child.stdin.write(JSON.stringify(data) + "\n");
  }
  request(method, params) {
    const id = this.nextID++;
    return new Promise((resolve, reject) => {
      if (this.error) { reject(Error(this.error)); return; }
      const timeout = setTimeout(() => {
        this.waiters.delete(id);
        reject(Error("APP_SERVER_TIMEOUT"));
        this.child.kill();
      }, 20_000);
      this.waiters.set(id, { resolve, reject, timeout });
      try { this.send({ id, method, params }); }
      catch { clearTimeout(timeout); this.waiters.delete(id); reject(Error("RPC_WRITE_FAILED")); }
    });
  }
  async close() {
    this.child.kill();
    await new Promise(resolve => {
      if (this.child.exitCode !== null || this.child.signalCode !== null) return resolve();
      this.child.once("exit", resolve);
      setTimeout(resolve, 2000);
    });
  }
}

async function sendSingleFrame(line, authPath, original) {
  stage = "PIPE_CONNECT";
  const socket = createConnection(pathToPipe);
  const connected = await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(Error("PIPE_CONNECTION_TIMEOUT")), 4000);
    socket.once("connect", () => { clearTimeout(timeout); resolve(true); });
    socket.once("error", () => { clearTimeout(timeout); reject(Error("PIPE_UNAVAILABLE")); });
  }).catch(error => { socket.destroy(); throw error; });
  if (!connected) throw Error("PIPE_NOT_CONNECTED");
  try {
    const check = await credentialState(authPath);
    if (original.id !== check.id || original.sha !== check.sha) throw Error("IDENTITY_CHANGED");
    stage = "PIPE_WRITE";
    await new Promise((resolve, reject) => socket.write(line, error => error ? reject(Error("PIPE_WRITE_FAILED")) : resolve()));
    pipeWrites++;
    stage = "PIPE_HOLD";
    const until = Date.now() + holdMs;
    while (Date.now() < until && !socket.destroyed) {
      await new Promise(resolve => setTimeout(resolve, 1000));
      const latest = await credentialState(authPath);
      if (latest.id !== original.id || latest.sha !== original.sha) throw Error("IDENTITY_CHANGED");
    }
    if (socket.destroyed) throw Error("PIPE_DISCONNECTED");
  } finally { socket.destroy(); }
}

async function main() {
  if (mode === "--synthetic") {
    const identity = { account: { type: "chatgpt", email: null, planType: "plus" }, requiresOpenaiAuth: true };
    const expected = "SYNTHETIC_A";
    const sample = {
      rateLimitsByLimitId: { codex: {
        planType: "plus",
        primary: { usedPercent: 30, windowDurationMins: 300, resetsAt: 2000000000 },
        secondary: { usedPercent: 14, windowDurationMins: 10080, resetsAt: 2000600000 },
      } },
      rateLimitResetCredits: { availableCount: 1 },
    };
    const frame = validatedWireFrame(expected, sample, 1800000000000);
    const assessment = assessV0149AccountRead({ id: expected, email: null }, identity);
    if (assessment.status !== "AUTH_FILE_PINNED" || assessment.accountIdConfirmed || !frame)
      throw Error("SYNTHETIC_CONTRACT_FAILED");
    safeOutput("SYNTHETIC_PASS", null, {
      credentialBytesRead: false, rateLimitCalls: 0, pipeWrites: 0,
      profileCreated: false, credentialCopied: false,
      topLevelWireKeys: Object.keys(JSON.parse(frame)),
    });
    return;
  }
  if (mode === "--synthetic-receipt") {
    await beginReceipt();
    stage = "ACCOUNT_READ";
    rpcCalls = 2;
    accountReadCalls = 1;
    await finishReport("FAILED_NO_LIVE_PASS", "ACCOUNT_READ_SCHEMA_MISMATCH");
    return;
  }
  // Explicitly retire the consumed v1 gate. Only a distinct v2 gate could run.
  if (mode === "--live-a-once") throw Error("EXPIRED_LIVE_GATE_CONSUMED");
  if (mode !== "--live-a-once-v2") {
    safeOutput("LIVE_NOT_STARTED", "Use --synthetic for offline checks. Live requires a new Gate.");
    return;
  }
  if (process.platform !== "win32" ||
      process.env.CODEX_SWITCHER_A_V2_LIVE_APPROVED !== "YES" ||
      !process.argv.includes("--allow-app-server-side-effects")) {
    throw Error("LIVE_V2_GATE_NOT_APPROVED");
  }
  await beginReceipt();
  stage = "PREFLIGHT";
  const defaultHome = resolve(join(homedir(), ".codex"));
  const home = resolve(process.env.CODEX_HOME || defaultHome);
  // This diagnostic is scoped to the original default active A credential.
  // Nondefault CODEX_HOME requires a separately verified active-account mapping.
  if (home.toLowerCase() !== defaultHome.toLowerCase()) throw Error("NONDEFAULT_CODEX_HOME");
  const authPath = join(home, "auth.json");
  const executable = join(process.env.APPDATA || "", "npm", "node_modules", "@openai",
    "codex", "node_modules", "@openai", "codex-win32-x64", "vendor",
    "x86_64-pc-windows-msvc", "bin", "codex.exe");
  if (!existsSync(authPath) || !existsSync(executable)) throw Error("REQUIRED_PATH_MISSING");
  const before = await credentialState(authPath);
  stage = "APP_SERVER_SPAWN";
  const app = new AppServer(executable, { ...process.env, CODEX_HOME: home });
  let frame;
  try {
    stage = "INITIALIZE";
    rpcCalls++;
    await app.request("initialize", {
      clientInfo: { name: "codex_switcher_a_only_probe", title: "A-only Codex Usage Test", version: "0.1.26" },
    });
    app.send({ method: "initialized", params: {} });
    stage = "ACCOUNT_READ";
    rpcCalls++;
    accountReadCalls++;
    const currentIdentity = await app.request("account/read", { refreshToken: false });
    stage = "IDENTITY_SCOPE";
    const assessment = assessV0149AccountRead(before, currentIdentity);
    if (assessment.status !== "AUTH_FILE_PINNED") throw Error(assessment.reason);
    // Official v0.149.0 does not expose a provider ID in account/read.
    identityScope = "AUTH_FILE_PINNED_RPC_ID_UNVERIFIED";
    stage = "RATE_LIMITS_READ";
    rpcCalls++;
    rateLimitCalls++;
    const usage = await app.request("account/rateLimits/read", {});
    stage = "AUTH_POSTCHECK";
    const after = await credentialState(authPath);
    if (before.sha !== after.sha || before.id !== after.id) throw Error("AUTH_FILE_CHANGED");
    frame = validatedWireFrame(before.id, usage, Date.now());
    if (!frame) throw Error("USAGE_SCHEMA_INVALID");
  } finally { await app.close(); }
  await sendSingleFrame(frame, authPath, before);
  stage = "COMPLETE";
  await finishReport("LIVE_A_AUTH_FILE_PINNED_OBSERVED", null, {
    accountIdentityConfirmed: false, accountIdVerifiedByRpc: false,
    profileCreated: false, credentialCopied: false, authFileUnchanged: true,
    automaticRepeat: false,
  });
}

main().catch(async error => {
  // Never reveal raw RPC, auth JSON, account ID, email, file contents or token.
  const known = new Set([
    "LIVE_GATE_NOT_APPROVED", "REQUIRED_PATH_MISSING", "NONDEFAULT_CODEX_HOME", "AUTH_METADATA_UNAVAILABLE",
    "CREDENTIAL_FORMAT_UNKNOWN", "CREDENTIAL_IDENTITY_UNKNOWN", "APP_SERVER_FAILED",
    "APP_SERVER_TERMINATED", "APP_SERVER_NOT_READY", "APP_SERVER_TIMEOUT",
    "OUTPUT_LIMIT", "INVALID_RPC", "RPC_REJECTED", "RPC_WRITE_FAILED",
    "ACCOUNT_READ_SCHEMA_MISMATCH", "ACCOUNT_EMAIL_MISMATCH", "ACCOUNT_EMAIL_INVALID",
    "LOCAL_IDENTITY_UNAVAILABLE", "AUTH_FILE_CHANGED", "USAGE_SCHEMA_INVALID",
    "RECEIPT_PATH_MISSING", "RECEIPT_PATH_INVALID", "RECEIPT_CREATE_FAILED",
    "EXPIRED_LIVE_GATE_CONSUMED", "LIVE_V2_GATE_NOT_APPROVED",
    "IDENTITY_CHANGED", "PIPE_CONNECTION_TIMEOUT", "PIPE_UNAVAILABLE",
    "PIPE_WRITE_FAILED", "PIPE_DISCONNECTED", "SYNTHETIC_CONTRACT_FAILED",
  ]);
  const reason = known.has(error.message) ? error.message : "UNEXPECTED_FAILURE";
  if (mode === "--live-a-once-v2" || mode === "--synthetic-receipt")
    await finishReport("FAILED_NO_LIVE_PASS", reason);
  else safeOutput("FAILED_NO_LIVE_PASS", reason);
  process.exitCode = 2;
});
