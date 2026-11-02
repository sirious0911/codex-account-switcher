import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join, resolve } from "node:path";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { assessV0149AccountRead, localCredentialAccountId,
  localCredentialEmail, validatedWireFrame } from "../active-a-no-copy-contract.mjs";

const account = "SYNTHETIC_ACCOUNT";
// The pinned rust-v0.149.0 ChatGPT response has no provider account ID.
const rpc = { account: { type: "chatgpt", email: "tester@example.invalid", planType: "plus" },
  requiresOpenaiAuth: true };
const source = { rateLimitsByLimitId: { codex: {
  planType: "plus",
  primary: { usedPercent: 20, windowDurationMins: 300, resetsAt: 2000000000 },
  secondary: { usedPercent: 60, windowDurationMins: 10080, resetsAt: 2000600000 },
}}, rateLimitResetCredits: { availableCount: 2 } };
const now = 1800000000000;
const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
function run(args, env = {}) {
  return spawnSync(process.execPath, ["scripts/active-a-no-copy.mjs", ...args], {
    cwd: root, encoding: "utf8", windowsHide: true, timeout: 10000,
    env: { ...process.env, CODEX_SWITCHER_A_ONLY_LIVE_APPROVED: "",
      CODEX_SWITCHER_A_V2_LIVE_APPROVED: "", ...env },
  });
}

test("local auth identity is extracted without creating a profile", () => {
  assert.equal(localCredentialAccountId({ tokens: { account_id: account } }), account);
  assert.equal(localCredentialEmail({ tokens: { account_id: account } }), null);
  assert.equal(localCredentialAccountId({ tokens: { account_id: "bad\nid" } }), null);
  assert.equal(localCredentialAccountId({ tokens: {} }), null);
});
test("official v0.149.0 account/read does not verify exact provider account ID", () => {
  assert.equal(Object.hasOwn(rpc.account, "accountId"), false);
  const result = assessV0149AccountRead({ id: account, email: "tester@example.invalid" }, rpc);
  assert.equal(result.status, "AUTH_FILE_PINNED");
  assert.equal(result.reason, "RPC_ACCOUNT_ID_NOT_EXPOSED");
  assert.equal(result.accountIdConfirmed, false);
  assert.equal(result.emailConsistencyChecked, true);
  assert.equal(assessV0149AccountRead({ id: account, email: null },
    { ...rpc, account: { ...rpc.account, email: null } }).accountIdConfirmed, false);
});
test("mismatched email, unsupported account or invented RPC identity fail", () => {
  const local = { id: account, email: "tester@example.invalid" };
  assert.equal(assessV0149AccountRead(local,
    { ...rpc, account: { ...rpc.account, email: "different@example.invalid" } }).reason,
    "ACCOUNT_EMAIL_MISMATCH");
  assert.equal(assessV0149AccountRead(local, { ...rpc, account: { type: "apiKey" } }).status, "REJECTED");
  assert.equal(assessV0149AccountRead(local, {
    account: { accountId: account, email: "tester@example.invalid" },
  }).reason, "ACCOUNT_READ_SCHEMA_MISMATCH");
  assert.equal(assessV0149AccountRead({ id: null }, rpc).status, "REJECTED");
  assert.equal(assessV0149AccountRead(local, {}).status, "REJECTED");
});
test("receiver wire preserves 5H, weekly and reset-credit schema", () => {
  const line = validatedWireFrame(account, source, now);
  assert.ok(line.endsWith("\n"));
  const obj = JSON.parse(line);
  assert.deepEqual(Object.keys(obj), ["kind", "observedAtMs", "raw"]);
  assert.equal(obj.raw.accountId, account);
  assert.equal(obj.raw.rateLimitsByLimitId.codex.primary.windowDurationMins, 300);
  assert.equal(obj.raw.rateLimitsByLimitId.codex.secondary.windowDurationMins, 10080);
  assert.equal(obj.raw.rateLimitResetCredits.availableCount, 2);
  assert.ok(Buffer.byteLength(line, "utf8") <= 8193);
  assert.equal(validatedWireFrame(account, {}, now), null);
  assert.equal(validatedWireFrame(account, source, 0), null);
  const wrong = structuredClone(source);
  wrong.rateLimitsByLimitId.codex.primary.windowDurationMins = 240;
  wrong.rateLimitsByLimitId.codex.secondary.windowDurationMins = 8000;
  assert.equal(validatedWireFrame(account, wrong, now), null);
});
test("synthetic CLI uses the official account/read response shape", () => {
  const child = run(["--synthetic"]);
  assert.equal(child.status, 0, child.stderr);
  const result = JSON.parse(child.stdout);
  assert.equal(result.status, "SYNTHETIC_PASS");
  assert.equal(result.rateLimitCalls, 0);
  assert.equal(result.pipeWrites, 0);
  assert.equal(result.credentialCopied, false);
  assert.deepEqual(result.topLevelWireKeys, ["kind", "observedAtMs", "raw"]);
});
test("consumed original LIVE Gate cannot be replayed with prior flags", () => {
  const child = run(["--live-a-once", "--allow-app-server-side-effects"],
    { CODEX_SWITCHER_A_ONLY_LIVE_APPROVED: "YES" });
  assert.equal(child.status, 2, child.stderr);
  assert.equal(JSON.parse(child.stdout).reason, "EXPIRED_LIVE_GATE_CONSUMED");
});
test("new LIVE Gate requires separate authorization", () => {
  const child = run(["--live-a-once-v2"]);
  assert.equal(child.status, 2, child.stderr);
  assert.equal(JSON.parse(child.stdout).reason, "LIVE_V2_GATE_NOT_APPROVED");
});
test("sanitized QA receipt is durable and cannot overwrite a previous result", () => {
  const dir = mkdtempSync(join(tmpdir(), "codex-a-qa-"));
  const receipt = join(dir, "synthetic-result.json");
  try {
    const child = run(["--synthetic-receipt"],
      { CODEX_SWITCHER_A_ONLY_RECEIPT_PATH: receipt });
    assert.equal(child.status, 0, child.stderr);
    const report = JSON.parse(readFileSync(receipt, "utf8"));
    assert.equal(report.status, "FAILED_NO_LIVE_PASS");
    assert.equal(report.stage, "ACCOUNT_READ");
    assert.equal(report.reason, "ACCOUNT_READ_SCHEMA_MISMATCH");
    assert.equal(report.accountReadCalls, 1);
    assert.equal(report.rateLimitCalls, 0);
    assert.equal(report.accountIdVerifiedByRpc, false);
    const second = run(["--synthetic-receipt"],
      { CODEX_SWITCHER_A_ONLY_RECEIPT_PATH: receipt });
    assert.equal(second.status, 2);
    assert.equal(JSON.parse(second.stdout).reason, "RECEIPT_CREATE_FAILED");
    assert.deepEqual(JSON.parse(readFileSync(receipt, "utf8")), report);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});
test("no auth import, account switch, login or reset consumption introduced", () => {
  const text = readFileSync(resolve(root, "scripts/active-a-no-copy.mjs"), "utf8");
  assert.doesNotMatch(text, /(?:copyFile|rename|unlink|rmSync|mkdirSync)\s*\(/);
  assert.doesNotMatch(text, /app\.request\("account\/(?:login|logout|rateLimitResetCredit\/consume)/);
  assert.match(text, /app\.request\("account\/rateLimits\/read"/);
  assert.match(text, /app\.request\("account\/read"/);
  assert.match(text, /open\(file, "wx", 0o600\)/);
});
