import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import {
  normalizeSynthetic, emptyState, advanceState, renderTelegramMock,
  renderTaskbarMock, takeSingleWriterLock, readDurableState, persistDurableState
} from "../offline-core.mjs";

let checks = 0;
function test(label, fn) { fn(); checks++; process.stdout.write("PASS " + label + "\n"); }
const T = 1793500000000;
const mk = ({
  account = "MOCK_ACCOUNT_A", plan = "mock-plan", u5 = 80, u7 = 60,
  r5 = T + 5 * 3600000, r7 = T + 7 * 86400000,
  credits = 1, allowed = true, details = null
} = {}) => ({
  accountId: account, ordinaryUsageAllowed: allowed,
  rateLimits: {
    planType: plan,
    primary: { usedPercent: u5, resetsAt: r5 / 1000, windowDurationMins: 300 },
    secondary: { usedPercent: u7, resetsAt: r7 / 1000, windowDurationMins: 10080 }
  },
  rateLimitResetCredits: credits === undefined ? null : { availableCount: credits, credits: details }
});
const norm = (o, at = T) => normalizeSynthetic(mk(o), at);
const seq = (...observations) => {
  let state = emptyState(), events = [];
  for (const item of observations) {
    const next = advanceState(state, item);
    state = next.state; events.push(...next.events);
  }
  return { state, events };
};

test("initial baseline does not alert", () => assert.equal(seq(norm({})).events.length, 0));
test("remaining 5-hour and weekly percentages", () => { const x=norm({u5:15,u7:71});assert.equal(x.five.remainingPercent,85);assert.equal(x.week.remainingPercent,29); });
test("zero usage is legitimate known value", () => assert.equal(norm({u5:0}).five.remainingPercent,100));
test("unknown used is not mapped to 100", () => assert.equal(norm({u5:null}).five.remainingPercent,null));
test("out of range percent rejected", () => assert.equal(norm({u5:101}).five.usedPercent,null));
test("string percent rejected", () => assert.equal(norm({u5:"80"}).five.usedPercent,null));
test("negative percent rejected", () => assert.equal(norm({u7:-1}).week.usedPercent,null));
test("missing duration remains unknown", () => {const j=mk();delete j.rateLimits.primary.windowDurationMins; assert.equal(normalizeSynthetic(j,T).five.usedPercent,null);});
test("wrong duration never labeled five-hour", () => {const j=mk();j.rateLimits.primary.windowDurationMins=60; assert.equal(normalizeSynthetic(j,T).five.remainingPercent,null);});
test("negative credits remain unknown", () => assert.equal(norm({credits:-1}).credits,null));
test("missing credits stay unknown", () => {const j=mk();delete j.rateLimitResetCredits;assert.equal(normalizeSynthetic(j,T).credits,null);});
test("null credits are unknown", () => assert.equal(norm({credits:null}).credits,null));
test("unknown permission remains null", () => assert.equal(norm({allowed:null}).ordinaryUsageAllowed,null));
test("optional detail null distinguished", () => assert.equal(norm({details:null}).creditDetails,null));
test("optional empty detail known empty", () => assert.deepEqual(norm({details:[]}).creditDetails,[]));
test("synthetic credit expiry parsed", () => { const x=norm({details:[{status:"available",expiresAt:1800000000,id:"MOCK"}]});assert.equal(x.creditDetails[0].expiresAtMs,1800000000000); assert.equal("id" in x.creditDetails[0],false); });
test("reject invalid synthetic observation time", () => assert.throws(()=>normalizeSynthetic(mk(),-1)));
test("reject malformed response", () => assert.throws(()=>normalizeSynthetic("broken",T)));

test("credits increase requires two new observations", () => {
  const first=seq(norm({},T),norm({credits:2},T+60000));
  assert.equal(first.events.length,0);
  const more=advanceState(first.state,norm({credits:2},T+120000));
  assert.equal(more.events.length,1);
  assert.equal(more.events[0].type,"CREDIT_INCREASE");
  assert.equal(more.state.outbox.length,1);
});
test("credit increase once only after further polls", () => { const r=seq(norm({},T),norm({credits:2},T+60000),norm({credits:2},T+120000),norm({credits:2},T+180000));assert.equal(r.events.length,1); });
test("credit decrease no alert", () => { const r=seq(norm({credits:2},T),norm({credits:1},T+60000),norm({credits:1},T+120000));assert.equal(r.events.length,0); });
test("false one-off credit increase ignored", () => { const r=seq(norm({},T),norm({credits:2},T+60000),norm({credits:1},T+120000));assert.equal(r.events.length,0); });
test("early five-hour recovery requires confirmation", () => {
  const r=seq(norm({},T),norm({u5:0,r5:T+10*3600000},T+60000),norm({u5:0,r5:T+10*3600000},T+120000));
  assert.equal(r.events.length,1);assert.equal(r.events[0].type,"EARLY_RECOVERY_SUSPECTED");assert.equal(r.events[0].lane,"five");
});
test("early weekly recovery classified separately", () => {
  const r=seq(norm({},T),norm({u7:0,r7:T+14*86400000},T+60000),norm({u7:0,r7:T+14*86400000},T+120000));
  assert.equal(r.events.length,1);assert.equal(r.events[0].lane,"week");
});
test("regular scheduled reset classified", () => {
  const old=T+600000, newer=old+5*3600000;
  const r=seq(norm({r5:old},T),norm({u5:0,r5:newer},old-60000),norm({u5:0,r5:newer},old+60000));
  assert.equal(r.events.length,1);assert.equal(r.events[0].type,"SCHEDULED_RESET");
});
test("no early recovery when ordinary usage blocked", () => {
  const r=seq(norm({allowed:false},T),norm({u5:0,r5:T+10*3600000,allowed:false},T+60000),norm({u5:0,r5:T+10*3600000,allowed:false},T+120000));
  assert.equal(r.events.length,0);
});
test("no early recovery with unknown permission", () => {
  const r=seq(norm({allowed:null},T),norm({u5:0,r5:T+10*3600000,allowed:null},T+60000),norm({u5:0,r5:T+10*3600000,allowed:null},T+120000));
  assert.equal(r.events.length,0);
});
test("account change discards previous baseline", () => {const r=seq(norm({},T),norm({account:"MOCK_B",credits:5},T+60000),norm({account:"MOCK_B",credits:5},T+120000));assert.equal(r.events.length,0);});
test("plan change discards previous baseline", () => {const r=seq(norm({},T),norm({plan:"other",credits:5},T+60000),norm({plan:"other",credits:5},T+120000));assert.equal(r.events.length,0);});
test("missing identity suppresses alerts", () => {const r=seq(norm({},T),norm({account:null,credits:5},T+60000),norm({account:null,credits:5},T+120000));assert.equal(r.events.length,0);});
test("out-of-order reading ignored", () => {const r=seq(norm({},T),norm({credits:2},T-60000));assert.equal(r.events.length,0);});
test("stale interval rebaselines", () => {const r=seq(norm({},T),norm({credits:2},T+25*3600000),norm({credits:2},T+25*3600000+60000));assert.equal(r.events.length,0);});
test("two independent early lane events emitted", () => {const x={u5:0,u7:0,r5:T+10*3600000,r7:T+14*86400000};const r=seq(norm({},T),norm(x,T+60000),norm(x,T+120000));assert.equal(r.events.length,2);});
test("mock Telegram text omits synthetic identity", () => {const r=seq(norm({},T),norm({credits:2},T+60000),norm({credits:2},T+120000));const msg=renderTelegramMock(r.events[0]);assert.match(msg,/모의 알림/);assert.doesNotMatch(msg,/MOCK_ACCOUNT/);});
test("unknown taskbar labels visible", () => {const str=renderTaskbarMock(norm({u5:null,credits:null}));assert.match(str,/5H UNKNOWN/);assert.match(str,/리셋권 UNKNOWN/);});
test("durable ledger restores and suppresses repeats", () => {
  const dir=fs.mkdtempSync(path.join(os.tmpdir(),"codex-usage-mock-"));
  try {
    const unlock=takeSingleWriterLock(dir);
    let state=emptyState();
    for(const obs of [norm({},T),norm({credits:2},T+60000),norm({credits:2},T+120000)]) {
      state=advanceState(state,obs).state;persistDurableState(dir,state);
    }
    assert.equal(state.outbox.length,1);
    unlock();
    const unlock2=takeSingleWriterLock(dir);
    const restored=readDurableState(dir);
    const again=advanceState(restored,norm({credits:2},T+180000));
    assert.equal(again.events.length,0);assert.equal(again.state.outbox.length,1);
    unlock2();
  } finally {fs.rmSync(dir,{recursive:true,force:true});}
});
test("single writer lock rejects rival process opening same directory", () => {
  const dir=fs.mkdtempSync(path.join(os.tmpdir(),"codex-usage-lock-"));
  try { const unlock=takeSingleWriterLock(dir);assert.throws(()=>takeSingleWriterLock(dir));unlock(); }
  finally {fs.rmSync(dir,{recursive:true,force:true});}
});
test("invalid durable state rejected", () => {
  const dir=fs.mkdtempSync(path.join(os.tmpdir(),"codex-usage-corrupt-"));
  try {fs.writeFileSync(path.join(dir,"state.json"),'{"schemaVersion":2}');assert.throws(()=>readDurableState(dir));}
  finally {fs.rmSync(dir,{recursive:true,force:true});}
});
test("fresh unknown reading does not silently keep prior percent", () => {
  const a = norm({},T), b = norm({u5:null,credits:null},T+60000);
  const r = seq(a,b);
  assert.equal(r.state.trusted.five.usedPercent,null);
  assert.equal(r.state.trusted.credits,null);
});
test("synthetic consumed reset credit does not become early recovery alert", () => {
  const x={u5:0,r5:T+10*3600000,credits:0};
  const r=seq(norm({},T),norm(x,T+60000),norm(x,T+120000));
  assert.equal(r.events.length,0);
});
test("slightly changing post-reset usage can confirm same reset boundary", () => {
  const a={u5:0,r5:T+10*3600000}, b={u5:1,r5:T+10*3600000};
  const r=seq(norm({},T),norm(a,T+60000),norm(b,T+120000));
  assert.equal(r.events.length,1);
});
test("different second reset boundary invalidates pending candidate", () => {
  const a={u5:0,r5:T+10*3600000}, b={u5:0,r5:T+11*3600000};
  const r=seq(norm({},T),norm(a,T+60000),norm(b,T+120000));
  assert.equal(r.events.length,0);
});
test("repeat sample timestamp cannot confirm the same candidate", () => {
  const x={credits:2}, r=seq(norm({},T),norm(x,T+60000),norm(x,T+60000));
  assert.equal(r.events.length,0);
});
test("official multi-bucket codex lane accepted", () => {
  const j=mk();j.rateLimitsByLimitId={codex:j.rateLimits};delete j.rateLimits;
  assert.equal(normalizeSynthetic(j,T).five.remainingPercent,20);
});
test("never infer early reset when new reset time is already in past", () => {
  const a={u5:0,r5:T+10*3600000};
  const r=seq(norm({},T),norm(a,T+11*3600000),norm(a,T+11*3600000+60000));
  assert.equal(r.events.length,0);
});
process.stdout.write(`OFFLINE_TESTS_PASS checks=${checks} actualCodexQueries=0 telegramSends=0\n`);
