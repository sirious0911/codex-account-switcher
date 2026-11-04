import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { normalizeSynthetic, emptyState, advanceState, takeSingleWriterLock, readDurableState, persistDurableState } from "../offline-core.mjs";
import { reserveMockDelivery, acknowledgeMockDelivery, flagMockUncertain, inspectMockOutbox } from "../mock-outbox.mjs";

let checks = 0;
function test(name, callback) { callback(); checks++; console.log("PASS " + name); }
const T = 1793500000000;
function mock(available, observedAt) {
  return normalizeSynthetic({
    accountId: "MOCK_ACCOUNT", ordinaryUsageAllowed: true,
    rateLimits: { planType: "mock",
      primary: { usedPercent: 15, resetsAt: 1793520000, windowDurationMins: 300 },
      secondary: { usedPercent: 38, resetsAt: 1794100000, windowDurationMins: 10080 } },
    rateLimitResetCredits: { availableCount: available, credits: null }
  }, observedAt);
}
function confirmed() {
  let state = emptyState();
  for (const [count, at] of [[1,T],[2,T+60000],[2,T+120000]]) {
    state = advanceState(state,mock(count,at)).state;
  }
  assert.equal(state.outbox.length,1);
  return state;
}
test("outbox starts with one mock-pending event",() => {
  const s=confirmed();
  assert.equal(s.outbox[0].status,"MOCK_PENDING");
});
test("reserve emits formatting-only message",()=>{
  const s=confirmed();
  const r=reserveMockDelivery(s,s.outbox[0].key,"MOCK_MAC");
  assert.equal(r.reserved,true);
  assert.match(r.mockMessage,/모의 알림/);
  assert.equal(r.state.outbox[0].status,"MOCK_RESERVED");
  assert.equal(s.outbox[0].status,"MOCK_PENDING");
});
test("reserve is not repeated even on same host",()=>{
  const s=confirmed();
  const r=reserveMockDelivery(s,s.outbox[0].key,"MOCK_MAC");
  const twice=reserveMockDelivery(r.state,s.outbox[0].key,"MOCK_MAC");
  assert.equal(twice.reserved,false);
  assert.equal(twice.mockMessage,null);
});
test("one owner blocks second OS",()=>{
  const s=confirmed();
  const r=reserveMockDelivery(s,s.outbox[0].key,"MOCK_MAC");
  assert.throws(()=>reserveMockDelivery(r.state,s.outbox[0].key,"MOCK_WINDOWS"),/Single/);
});
test("disallow arbitrary owner IDs",()=>{
  const s=confirmed();assert.throws(()=>reserveMockDelivery(s,s.outbox[0].key,"REAL_HOST"),/synthetic/);
});
test("reject events not in ledger",()=>{
  assert.throws(()=>reserveMockDelivery(confirmed(),"NOT_PRESENT","MOCK_MAC"),/Unknown/);
});
test("mock acknowledgement requires existing reservation",()=>{
  const s=confirmed();
  assert.throws(()=>acknowledgeMockDelivery(s,s.outbox[0].key,"MOCK_MAC"),/reservation/);
});
test("acknowledge changes only mock state",()=>{
  const s=confirmed();
  const r=reserveMockDelivery(s,s.outbox[0].key,"MOCK_MAC");
  const a=acknowledgeMockDelivery(r.state,s.outbox[0].key,"MOCK_MAC");
  assert.equal(a.outbox[0].status,"MOCK_COMPLETE");
  assert.equal(reserveMockDelivery(a,s.outbox[0].key,"MOCK_MAC").reserved,false);
});
test("mock uncertain delivery must never auto retry",()=>{
  const s=confirmed();
  const r=reserveMockDelivery(s,s.outbox[0].key,"MOCK_MAC");
  const u=flagMockUncertain(r.state,s.outbox[0].key,"MOCK_MAC");
  assert.equal(u.outbox[0].status,"MOCK_UNCERTAIN");
  assert.equal(reserveMockDelivery(u,s.outbox[0].key,"MOCK_MAC").reserved,false);
});
test("cannot acknowledge another owner's event",()=>{
  const s=confirmed();
  const r=reserveMockDelivery(s,s.outbox[0].key,"MOCK_MAC");
  assert.throws(()=>acknowledgeMockDelivery(r.state,s.outbox[0].key,"MOCK_WINDOWS"));
});
test("mock states persist across simulated restart",()=>{
  const dir=fs.mkdtempSync(path.join(os.tmpdir(),"codex-mock-outbox-"));
  try {
    let s=confirmed();
    let unlock=takeSingleWriterLock(dir);
    s=reserveMockDelivery(s,s.outbox[0].key,"MOCK_MAC").state;
    persistDurableState(dir,s);
    unlock();
    unlock=takeSingleWriterLock(dir);
    const restored=readDurableState(dir);
    assert.equal(inspectMockOutbox(restored)[0].status,"MOCK_RESERVED");
    assert.equal(reserveMockDelivery(restored,s.outbox[0].key,"MOCK_MAC").reserved,false);
    assert.throws(()=>reserveMockDelivery(restored,s.outbox[0].key,"MOCK_WINDOWS"));
    unlock();
  } finally { fs.rmSync(dir,{recursive:true,force:true}); }
});
test("competing separate Node process cannot acquire mock lock",()=>{
  const dir=fs.mkdtempSync(path.join(os.tmpdir(),"codex-two-processes-"));
  try {
    const unlock=takeSingleWriterLock(dir);
    const runner=spawnSync(process.execPath,[
      "--input-type=module","-e",
      'import fs from "node:fs";import path from "node:path";try{fs.openSync(path.join(process.argv[1],".writer.lock"),"wx");process.exit(99)}catch(e){process.exit(e.code==="EEXIST"?0:1)}',
      dir
    ],{encoding:"utf8",timeout:5000,windowsHide:true});
    assert.equal(runner.status,0, runner.stderr || "separate process not denied");
    unlock();
  } finally {fs.rmSync(dir,{recursive:true,force:true});}
});
test("unknown outbox status fails closed",()=>{
  const s=confirmed();s.outbox[0].status="UNKNOWN";
  assert.throws(()=>inspectMockOutbox(s),/Unrecognized/);
});
console.log(`MOCK_OUTBOX_PASS checks=${checks} realTelegramSend=0 realAccountRead=0`);
