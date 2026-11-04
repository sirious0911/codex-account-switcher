import test from "node:test";
import assert from "node:assert/strict";
import { normalizeSynthetic, renderTaskbarMock } from "../../monitor/offline-core.mjs";
import { previewSyntheticReadings } from "../../monitor/synthetic-pipeline.mjs";

const T = 1793500000000;
const fiveReset = T + 5 * 3_600_000;
const weekReset = T + 7 * 86_400_000;

/** Matches Swift CodexMonitorPayloadEncoder's raw JSON schema. */
const raw = ({
  accountId = "MOCK_PROVIDER_001",
  planType = "plus",
  ordinaryUsageAllowed = true,
  usedFive = 75,
  usedWeekly = 60,
  resetsFive = fiveReset,
  resetsWeekly = weekReset,
  credits = 1
} = {}) => ({
  accountId,
  ordinaryUsageAllowed,
  rateLimitsByLimitId: {
    codex: {
      planType,
      primary: usedFive === null ? null :
        { usedPercent: usedFive, windowDurationMins: 300, resetsAt: resetsFive / 1000 },
      secondary: usedWeekly === null ? null :
        { usedPercent: usedWeekly, windowDurationMins: 10080, resetsAt: resetsWeekly / 1000 }
    }
  },
  rateLimitResetCredits: { availableCount: credits }
});
const sample = (props, at) => ({ raw: raw(props), observedAtMs: at });

test("Swift-compatible monitor JSON normalizes quota and credits", () => {
  const current = normalizeSynthetic(raw(), T);
  assert.equal(current.accountId, "MOCK_PROVIDER_001");
  assert.equal(current.planType, "plus");
  assert.equal(current.five.remainingPercent, 25);
  assert.equal(current.week.remainingPercent, 40);
  assert.equal(current.credits, 1);
  assert.match(renderTaskbarMock(current), /5H 25%/);
});

test("confirmed credit increase produces exactly one mock outbox delivery", () => {
  const result = previewSyntheticReadings([
    sample({}, T),
    sample({ credits: 2 }, T + 60000),
    sample({ credits: 2 }, T + 120000),
    sample({ credits: 2 }, T + 180000)
  ]);
  assert.equal(result.events.length, 1);
  assert.equal(result.events[0].type, "CREDIT_INCREASE");
  assert.equal(result.mockMessages.length, 1);
  assert.match(result.mockMessages[0], /모의 알림/);
  assert.match(result.mockMessages[0], /1개 → 2개/);
  assert.doesNotMatch(result.mockMessages[0], /MOCK_PROVIDER_001/);
  assert.equal(result.outbox[0].status, "MOCK_COMPLETE");
});

test("early five-hour recovery produces one confirmed mock event", () => {
  const recovered = { usedFive: 0, resetsFive: fiveReset + 5 * 3_600_000 };
  const result = previewSyntheticReadings([
    sample({}, T),
    sample(recovered, T + 60000),
    sample(recovered, T + 120000)
  ], "MOCK_MAC");
  assert.equal(result.events.length, 1);
  assert.equal(result.events[0].type, "EARLY_RECOVERY_SUSPECTED");
  assert.equal(result.events[0].lane, "five");
  assert.equal(result.mockMessages.length, 1);
  assert.equal(result.outbox[0].mockOwner, "MOCK_MAC");
});

test("unknown permission suppresses early recovery rather than inventing confirmation", () => {
  const recovered = { usedFive: 0, resetsFive: fiveReset + 5 * 3_600_000,
    ordinaryUsageAllowed: null };
  const result = previewSyntheticReadings([
    sample({ ordinaryUsageAllowed: null }, T),
    sample(recovered, T + 60000),
    sample(recovered, T + 120000)
  ]);
  assert.equal(result.events.length, 0);
  assert.equal(result.mockMessages.length, 0);
});

test("missing plan and provider identity cannot emit synthetic alerts", () => {
  for (const unknown of [{ planType: null }, { accountId: null }]) {
    const result = previewSyntheticReadings([
      sample(unknown, T),
      sample({ ...unknown, credits: 2 }, T + 60000),
      sample({ ...unknown, credits: 2 }, T + 120000)
    ]);
    assert.deepEqual(result.events, []);
  }
});

test("a one-off reset-credit change is not a notification", () => {
  const result = previewSyntheticReadings([
    sample({}, T),
    sample({ credits: 2 }, T + 60000),
    sample({ credits: 1 }, T + 120000)
  ]);
  assert.equal(result.mockMessages.length, 0);
});

test("first observation with an active account never alerts", () => {
  const result = previewSyntheticReadings([sample({ credits: 3 }, T)]);
  assert.equal(result.events.length, 0);
  assert.equal(result.outbox.length, 0);
});

test("mock owner enumeration rejects real or unknown sender names", () => {
  assert.throws(() => previewSyntheticReadings([
    sample({}, T),
    sample({ credits: 2 }, T + 60000),
    sample({ credits: 2 }, T + 120000)
  ], "TELEGRAM_REAL"), /synthetic host/);
});

test("Monitor Bot and Alert Bot previews have distinct mock-only destinations", () => {
  const result = previewSyntheticReadings([
    sample({}, T),
    sample({ credits: 2 }, T + 60000),
    sample({ credits: 2 }, T + 120000)
  ]);
  assert.equal(result.monitorPreview.target, "MOCK_MONITOR_BOT");
  assert.match(result.monitorPreview.text, /Codex \| 5H 25%/);
  assert.equal(result.mockDeliveries.length, 1);
  assert.equal(result.mockDeliveries[0].target, "MOCK_ALERT_BOT");
  assert.equal(result.mockDeliveries[0].text, result.mockMessages[0]);
  assert.doesNotMatch(result.mockDeliveries[0].text, /MOCK_PROVIDER_001/);
});

test("zero observations produce no monitor or notification preview", () => {
  const result = previewSyntheticReadings([]);
  assert.equal(result.monitorPreview, null);
  assert.deepEqual(result.mockDeliveries, []);
  assert.deepEqual(result.events, []);
});
