import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { readFileSync } from "node:fs";
import { activeIdentityMatches, localCredentialAccountId, validatedWireFrame } from "../active-a-no-copy-contract.mjs";

const account = "SYNTHETIC_ACCOUNT";
const rpc = { account: { accountId: account } };
const source = {
  rateLimitsByLimitId: { codex: {
    planType: "plus",
    primary: { usedPercent: 20, windowDurationMins: 300, resetsAt: 2000000000 },
    secondary: { usedPercent: 60, windowDurationMins: 10080, resetsAt: 2000600000 },
  }},
  rateLimitResetCredits: { availableCount: 2 },
};
const now = 1800000000000;

test("local credential account ID decodes without persistent copy", () => {
  assert.equal(localCredentialAccountId({ tokens: { account_id: account } }), account);
  const claims = { "https://api.openai.com/auth": { chatgpt_account_id: account } };
  const encoded = Buffer.from(JSON.stringify(claims)).toString("base64url");
  assert.equal(localCredentialAccountId({
    tokens: { id_token: "a." + encoded + ".signature" }
  }), account);
  assert.equal(localCredentialAccountId({ tokens: { id_token: "malformed" } }), null);
  assert.equal(localCredentialAccountId({ tokens: { account_id: "bad\nid" } }), null);
  assert.equal(localCredentialAccountId({ }), null);
});

test("RPC identity must exactly match local active credential identity", () => {
  assert.equal(activeIdentityMatches(account, rpc), true);
  assert.equal(activeIdentityMatches(account, { account: { accountId: "OTHER" } }), false);
  assert.equal(activeIdentityMatches(account, { account: { email: "some@example.test" } }), false);
  assert.equal(activeIdentityMatches("", rpc), false);
  assert.equal(activeIdentityMatches(account, {}), false);
});

test("wire preserves actual 5h 7d reset-credit units and receiver schema", () => {
  const line = validatedWireFrame(account, source, now);
  assert.ok(line?.endsWith("\n"));
  const object = JSON.parse(line);
  assert.deepEqual(Object.keys(object), ["kind", "observedAtMs", "raw"]);
  assert.equal(object.raw.accountId, account);
  assert.equal(object.raw.rateLimitsByLimitId.codex.primary.usedPercent, 20);
  assert.equal(object.raw.rateLimitsByLimitId.codex.primary.windowDurationMins, 300);
  assert.equal(object.raw.rateLimitsByLimitId.codex.secondary.usedPercent, 60);
  assert.equal(object.raw.rateLimitsByLimitId.codex.secondary.windowDurationMins, 10080);
  assert.equal(object.raw.rateLimitResetCredits.availableCount, 2);
  assert.ok(Buffer.byteLength(line, "utf8") <= 8193);
  assert.doesNotMatch(line, /access_token|refresh_token|id_token|auth\.json/);
});

test("invalid, missing, ill-sized or misleading values cannot be published", () => {
  assert.equal(validatedWireFrame("", source, now), null);
  assert.equal(validatedWireFrame(account, {}, now), null);
  assert.equal(validatedWireFrame(account, source, -1), null);
  assert.equal(validatedWireFrame(account, source, 0), null);
  assert.equal(validatedWireFrame(account, {
    rateLimitsByLimitId: { codex: {
      primary: { ...source.rateLimitsByLimitId.codex.primary, windowDurationMins: 240 },
    }},
  }, now), null);
  const variant = structuredClone(source);
  variant.rateLimitsByLimitId.codex.planType = "bad\nplan";
  variant.rateLimitResetCredits.availableCount = -5;
  const parsed = JSON.parse(validatedWireFrame(account, variant, now));
  assert.equal(parsed.raw.rateLimitsByLimitId.codex.planType, null);
  assert.equal(parsed.raw.rateLimitResetCredits.availableCount, null);
});

test("standalone synthetic CLI does not need Codex or write credentials", () => {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
  const result = spawnSync(process.execPath, ["scripts/active-a-no-copy.mjs", "--synthetic"], {
    cwd: root, encoding: "utf8", windowsHide: true, timeout: 10000,
    env: { ...process.env, CODEX_SWITCHER_A_ONLY_LIVE_APPROVED: "" },
  });
  assert.equal(result.status, 0, result.stderr);
  const out = JSON.parse(result.stdout);
  assert.equal(out.status, "SYNTHETIC_PASS");
  assert.equal(out.rateLimitCalls, 0);
  assert.equal(out.profileCreated, false);
  assert.equal(out.credentialCopied, false);
  assert.equal(out.pipeWrites, 0);
  assert.deepEqual(out.topLevelWireKeys, ["kind", "observedAtMs", "raw"]);
});

test("live CLI cannot start without the explicit two-part gate", () => {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
  const result = spawnSync(process.execPath, ["scripts/active-a-no-copy.mjs", "--live-a-once"], {
    cwd: root, encoding: "utf8", windowsHide: true, timeout: 10000,
    env: { ...process.env, CODEX_SWITCHER_A_ONLY_LIVE_APPROVED: "" },
  });
  assert.equal(result.status, 2, result.stderr);
  const out = JSON.parse(result.stdout);
  assert.equal(out.reason, "LIVE_GATE_NOT_APPROVED");
});

test("one-shot diagnostic has no profile write or account mutation commands", () => {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
  const text = readFileSync(resolve(root, "scripts/active-a-no-copy.mjs"), "utf8");
  assert.doesNotMatch(text, /(?:writeFile|copyFile|mkdir|rename|unlink|rmSync)\s*\(/);
  assert.doesNotMatch(text, /method:\s*["']account\/(?:login|logout|rateLimitResetCredit\/consume)/);
  assert.match(text, /app\.request\("account\/rateLimits\/read"/);
  assert.match(text, /app\.request\("account\/read"/);
});
