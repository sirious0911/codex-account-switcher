import assert from "node:assert/strict";
import { normalizeSynthetic, emptyState, advanceState, renderTelegramMock } from "../offline-core.mjs";

// Fixed vectors shared with dotnet-tests/Program.cs. The .NET checks have not
// been executed until a .NET 10 SDK is available.
const T = 1793527200000;
const SEC = v => Math.floor(v / 1000);
const MAKE = (used, credits, resetMs) => ({
  accountId: "MOCK_ACCOUNT",
  ordinaryUsageAllowed: true,
  rateLimits: {
    planType: "synthetic",
    primary: { usedPercent: used, windowDurationMins: 300, resetsAt: SEC(resetMs) },
    secondary: { usedPercent: 60, windowDurationMins: 10080, resetsAt: SEC(T + 7 * 86400000) }
  },
  rateLimitResetCredits: { availableCount: credits, credits: null }
});
const obs=(used,credits,resetMs,seconds=0) =>
  normalizeSynthetic(MAKE(used,credits,resetMs),T+seconds*1000);
function run(readings) {
  let state=emptyState(),out=[];
  for (const next of readings) {
    const result=advanceState(state,next);
    state=result.state;out.push(...result.events);
  }
  return out;
}
const credit=run([
  obs(15,1,T+5*3600000),
  obs(15,2,T+5*3600000,60),
  obs(15,2,T+5*3600000,120)
]);
assert.equal(credit.length,1);
assert.equal(credit[0].type,"CREDIT_INCREASE");
assert.equal(credit[0].key,"8da0eb6e6e9a7e59db7c9b2ee9b3ce2ee3b8f4607b429ae7221b9f90ddd870a6");
console.log("PASS credit hash and event type golden vector");

const reset=run([
  obs(80,1,T+5*3600000),
  obs(0,1,T+10*3600000,60),
  obs(0,1,T+10*3600000,120)
]);
assert.equal(reset.length,1);
assert.equal(reset[0].type,"EARLY_RECOVERY_SUSPECTED");
assert.equal(reset[0].key,"f572721c010511ffe3b07bf90ea8038b2bd59b5938c2392a907772bde4afbc60");
console.log("PASS reset hash and event type golden vector");
assert.equal(renderTelegramMock(credit[0]),"[모의 알림] Codex 리셋권 증가 관측: 1개 → 2개 (2회 확인)");
console.log("PASS mock credit message golden vector");
assert.equal(renderTelegramMock(reset[0]),"[모의 알림] Codex 5시간 비정기 복구 의심: 사용률 80% → 0% (2회 확인)");
console.log("PASS mock reset message golden vector");
console.log("PARITY_NODE_GOLDEN_PASS checks=4 DOTNET_NOT_RUN");
