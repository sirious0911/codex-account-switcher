import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { previewHostMessages } from "../../monitor/host-stream-preview.mjs";

const T = 1793500000000;
const base = (accountId, credits = 1, planType = "plus") => ({
  accountId,
  ordinaryUsageAllowed: null,
  rateLimitsByLimitId: {
    codex: {
      planType,
      primary: { usedPercent: 35, windowDurationMins: 300, resetsAt: 1793520000 },
      secondary: { usedPercent: 65, windowDurationMins: 10080, resetsAt: 1794100000 }
    }
  },
  rateLimitResetCredits: { availableCount: credits }
});
const wire = (accountId, credits, time, planType) => ({
  kind: "monitorObservation", observedAtMs: time,
  raw: base(accountId, credits, planType)
});

test("SwiftHost stdout envelope reaches the existing Node monitor engine", () => {
  const result = previewHostMessages([
    { kind: "snapshot", state: {} },
    wire("MOCK_A", 1, T),
    wire("MOCK_A", 2, T + 60000),
    wire("MOCK_A", 2, T + 120000),
    { kind: "completed", id: 4 }
  ]);
  assert.equal(result.observedAccounts.length, 1);
  assert.deepEqual(result.observedAccounts[0].eventTypes, ["CREDIT_INCREASE"]);
  assert.deepEqual(result.observedAccounts[0].outboxStatuses, ["MOCK_COMPLETE"]);
  assert.equal(result.alertBot.length, 1);
  assert.equal(result.alertBot[0].target, "MOCK_ALERT_BOT");
  assert.equal(result.monitorBot.target, "MOCK_MONITOR_BOT");
});

test("interleaved accounts retain independent baselines and event keys", () => {
  const input = [
    wire("MOCK_A", 1, T), wire("MOCK_B", 2, T + 2000),
    wire("MOCK_A", 2, T + 60000), wire("MOCK_B", 3, T + 62000),
    wire("MOCK_A", 2, T + 120000), wire("MOCK_B", 3, T + 122000)
  ];
  const result = previewHostMessages(input);
  assert.deepEqual(result.observedAccounts.map(x => x.accountId), ["MOCK_A", "MOCK_B"]);
  assert.deepEqual(result.observedAccounts.map(x => x.eventTypes), [
    ["CREDIT_INCREASE"], ["CREDIT_INCREASE"]
  ]);
  assert.equal(result.alertBot.length, 2);
});

test("unknown plan emitted by current Swift refresh cannot produce event alerts", () => {
  const result = previewHostMessages([
    wire("MOCK_A", 1, T, null),
    wire("MOCK_A", 2, T + 60000, null),
    wire("MOCK_A", 2, T + 120000, null)
  ]);
  assert.deepEqual(result.observedAccounts[0].eventTypes, []);
  assert.deepEqual(result.alertBot, []);
  assert.equal(result.monitorBot.target, "MOCK_MONITOR_BOT");
});

test("unknown account identity cannot establish an alert baseline", () => {
  const result = previewHostMessages([wire(null, 1, T), wire(null, 2, T + 60000)]);
  assert.deepEqual(result.observedAccounts, []);
  assert.equal(result.monitorBot, null);
  assert.deepEqual(result.alertBot, []);
});

test("CLI accepts actual JSON-lines envelope format without a network sender", () => {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
  const input = [wire("MOCK_A", 1, T), wire("MOCK_A", 2, T + 60000),
    wire("MOCK_A", 2, T + 120000)].map(x => JSON.stringify(x)).join("\n") + "\n";
  const run = spawnSync(process.execPath, ["monitor/host-stream-preview.mjs"], {
    cwd: root, input, encoding: "utf8", windowsHide: true, timeout: 10000
  });
  assert.equal(run.status, 0, run.stderr);
  const result = JSON.parse(run.stdout);
  assert.equal(result.alertBot.length, 1);
  assert.equal(result.alertBot[0].target, "MOCK_ALERT_BOT");
  assert.equal(result.monitorBot.target, "MOCK_MONITOR_BOT");
  assert.doesNotMatch(run.stdout, /access_token|refresh_token|auth\.json/);
});
