import test from 'node:test';
import assert from 'node:assert/strict';
import {reviewOfflineQuotaViewCandidateV2 as review} from './quota-view-candidate-v2.mjs';
import {reviewOfflineQuotaViewCandidate as reviewV1} from './quota-view-candidate-v1.mjs';

// Pure synthetic, deterministic: no clock, network, account, file or Telegram.
const NOW = 1800000000000;
const E1 = 1800200000000;
const E2 = 1800300000000;
const S = 'a'.repeat(32);
const H = 'b'.repeat(64);
const Q0 = {remainingPercent:null,resetAtUtcMs:null};
const base = () => ({
  schemaVersion:2,sourceDeviceId:'windows-b',sourceSessionId:S,
  sequence:1,collectedAtUtcMs:NOW-1000,accountIdSha256:H,
  fiveHour:{remainingPercent:85,resetAtUtcMs:NOW+3600000},
  sevenDay:{remainingPercent:62,resetAtUtcMs:NOW+172800000},
  resetCredits:{availableCount:2,expiryDetailsUtcMs:[E1,E2]},
  actualSendAuthorized:false,automaticRetryAllowed:false,
  productionReservationAuthorized:false,senderTransitionAuthorized:false
});
const raw = x => JSON.stringify(x);
const check = (input, now=NOW, previousRaw=null, expectedSourceDeviceId='windows-b') =>
  review(input,{trustedNowUtcMs:now,previousRaw,expectedSourceDeviceId});
const pass = (x,now=NOW,previousRaw=null,source='windows-b') => {
  const result=check(raw(x),now,previousRaw,source);
  assert.equal(result.status,'OFFLINE_V2_CANDIDATE_ONLY',JSON.stringify(result));
  assert.notEqual(result.view,null);
  assert.equal(result.actualSendAuthorized,false);
  assert.equal(result.automaticRetryAllowed,false);
  assert.equal(result.productionReservationAuthorized,false);
  assert.equal(result.senderTransitionAuthorized,false);
  assert.equal(result.realDeviceAuthenticated,false);
  assert.equal(result.realCrossHostSyncAuthorized,false);
  return result;
};
const fail = (x, now=NOW, previous=null, source='windows-b') => {
  const result=check(typeof x==='string'?x:raw(x),now,previous,source);
  assert.equal(result.view,null);
  assert.equal(result.presentation,null);
  assert.match(result.status,/^HOLD_/);
  return result;
};

// The 20 named cases mirror the V2-S1 independent design review matrix.
test('R01: reverse expiry input is presented ascending, with no credit IDs',()=>{
  const f=base();f.resetCredits.expiryDetailsUtcMs=[E2,E1];
  const r=pass(f);
  assert.deepEqual(r.presentation.expirySlots.map(x=>x.expiresAtUtcMs),[E1,E2]);
  assert.equal(r.presentation.availableCount,2);
  assert.equal(raw(r).includes('creditID'),false);
});
test('R02: provider count stays independent from filtered eligible details',()=>{
  const f=base();f.resetCredits.expiryDetailsUtcMs=[E1];
  const r=pass(f);
  assert.equal(r.presentation.availableCount,2);
  assert.equal(r.presentation.expirySlots.length,1);
});
test('R03: count 2 with missing source detail remains 2 + unknown date',()=>{
  const f=base();f.resetCredits.expiryDetailsUtcMs=null;
  const r=pass(f);
  assert.equal(r.presentation.availableCount,2);
  assert.equal(r.presentation.expirySlots,null);
  assert.equal(r.presentation.expiryDetailsProvided,false);
});
test('R04: count 2 with one known and one unknown expiry is preserved',()=>{
  const f=base();f.resetCredits.expiryDetailsUtcMs=[null,E1];
  const r=pass(f);
  assert.deepEqual(r.presentation.expirySlots.map(x=>x.expiresAtUtcMs),[E1,null]);
  assert.equal(r.presentation.expirySlots[1].expiryStatus,'UNKNOWN');
});
test('R05: count becomes zero and previous expiry slots never persist',()=>{
  const before=base();
  const after=base();after.sequence=2;after.collectedAtUtcMs=NOW;
  after.resetCredits={availableCount:0,expiryDetailsUtcMs:[]};
  const r=pass(after,NOW,raw(before));
  assert.equal(r.presentation.availableCount,0);
  assert.deepEqual(r.presentation.expirySlots,[]);
  assert.equal(raw(r).includes(String(E1)),false);
  assert.equal(raw(r).includes(String(E2)),false);
});
test('R06: unknown count is not coerced to zero',()=>{
  const f=base();f.resetCredits={availableCount:null,expiryDetailsUtcMs:null};
  const r=pass(f);
  assert.equal(r.presentation.availableCount,null);
  assert.equal(r.presentation.expirySlots,null);
});
test('R07: count 4 and only two transmitted expiry slots',()=>{
  const f=base();f.resetCredits={availableCount:4,expiryDetailsUtcMs:[E2,E1]};
  const r=pass(f);assert.equal(r.presentation.availableCount,4);
  assert.equal(r.presentation.expirySlots.length,2);
  assert.equal(r.presentation.expirySlots[0].expiresAtUtcMs,E1);
});
test('R08: count 1 plus explicit empty detail is not count zero',()=>{
  const f=base();f.resetCredits={availableCount:1,expiryDetailsUtcMs:[]};
  const r=pass(f);
  assert.equal(r.presentation.availableCount,1);
  assert.deepEqual(r.presentation.expirySlots,[]);
  assert.equal(r.presentation.expiryDetailsProvided,true);
});
test('R09: duplicate expiry timestamps preserve two positional slots',()=>{
  const f=base();f.resetCredits.expiryDetailsUtcMs=[E1,E1];
  const r=pass(f);
  assert.deepEqual(r.presentation.expirySlots.map(x=>x.position),[1,2]);
  assert.deepEqual(r.presentation.expirySlots.map(x=>x.expiresAtUtcMs),[E1,E1]);
});
test('R10: out-of-bound, fractional, negative or malformed timestamps fail',()=>{
  for(const t of [-1,1.2,4133980800001,1800200000]){
    const f=base();f.resetCredits.expiryDetailsUtcMs=[t];
    fail(f);
  }
  // JSON.stringify converts NaN to null; use genuinely invalid JSON text.
  fail(raw(base()).replace(String(E1),'NaN'));
});
test('R11: V2 fields cannot silently downgrade into strict V1',()=>{
  const f=base();f.schemaVersion=1;
  fail(f);
  const v1=reviewV1(raw(f),{trustedNowUtcMs:NOW});
  assert.equal(v1.view,null);
});
test('R12: all 5H/7D/R become UNKNOWN at exactly 6 minutes',()=>{
  const f=base();f.collectedAtUtcMs=NOW-360000;
  const r=fail(f);
  assert.equal(r.status,'HOLD_STALE_OR_FUTURE_V2_FRAME');
});
test('R13: provider account hash conflict blocks entire next frame',()=>{
  const f=base();f.accountIdSha256='c'.repeat(64);
  f.sequence=2;f.collectedAtUtcMs=NOW;
  assert.equal(fail(f,NOW,raw(base())).status,'HOLD_ACCOUNT_IDENTITY_CHANGED');
});
test('R14: source, sequence, replay and unknown keys fail strictly',()=>{
  const source=base();source.sourceDeviceId='macmini-a';
  assert.equal(fail(source).status,'HOLD_SOURCE_DEVICE_CONFLICT');
  const sequence=base();sequence.sequence=0;fail(sequence);
  const extra=base();extra.unexpectedKey=1;fail(extra);
  const reorder=base();reorder.sequence=2;
  assert.equal(fail(reorder,NOW,raw(base())).status,'HOLD_REPLAY_OR_REORDERED_VIEW');
});
test('R15: R-only frame allowed without inventing a Free 30D/7D window',()=>{
  const f=base();f.fiveHour={...Q0};f.sevenDay={...Q0};
  const r=pass(f);
  assert.equal(r.presentation.fiveHourRemainingPercent,null);
  assert.equal(r.presentation.sevenDayRemainingPercent,null);
  assert.equal(r.presentation.inferredNonWeeklyWindow,null);
  assert.equal(r.presentation.availableCount,2);
});
test('R16: expired expiry requires refresh, not inferred count decrement',()=>{
  const f=base();f.resetCredits.expiryDetailsUtcMs=[NOW-4000,NOW-2000];
  const r=pass(f);
  assert.equal(r.presentation.availableCount,2);
  assert.deepEqual(r.presentation.expirySlots.map(x=>x.expiryStatus),
    ['EXPIRED_REFRESH_REQUIRED','EXPIRED_REFRESH_REQUIRED']);
});
test('R17: two devices are isolated and never sum reset credits',()=>{
  const windows=base();
  const mac=base();mac.sourceDeviceId='macmini-a';
  mac.accountIdSha256='c'.repeat(64);
  mac.resetCredits.availableCount=1;mac.resetCredits.expiryDetailsUtcMs=[E1];
  const a=pass(windows);
  const b=pass(mac,NOW,null,'macmini-a');
  assert.equal(a.presentation.availableCount,2);
  assert.equal(b.presentation.availableCount,1);
  assert.equal(fail(mac).status,'HOLD_SOURCE_DEVICE_CONFLICT');
});
test('R18: any privileged flag set to true rejects the entire frame',()=>{
  for(const key of ['actualSendAuthorized','automaticRetryAllowed',
                    'productionReservationAuthorized','senderTransitionAuthorized']){
    const f=base();f[key]=true;fail(f);
  }
});
test('R19: source epoch seconds mistakenly sent as milliseconds rejected',()=>{
  const f=base();f.resetCredits.expiryDetailsUtcMs=[1800200000];
  fail(f);
});
test('R20: injected provider credit ID or extra nested field is rejected',()=>{
  const f=base();f.resetCredits.id='credit-private';
  fail(f);
  const g=base();g.resetCredits.expiryDetailsUtcMs=[{id:'private',time:E1}];
  fail(g);
});

// Extra boundary cases for B1–B4, all synthetic and in the same test file.
test('B1: R-only known zero is evidence, all quotas+R unknown is not',()=>{
  const known=base();known.fiveHour={...Q0};known.sevenDay={...Q0};
  known.resetCredits={availableCount:0,expiryDetailsUtcMs:[]};
  assert.equal(pass(known).presentation.availableCount,0);
  known.resetCredits={availableCount:null,expiryDetailsUtcMs:null};
  fail(known);
});
test('B2: null count cannot carry expiry list and zero cannot carry date',()=>{
  const f=base();f.resetCredits={availableCount:null,expiryDetailsUtcMs:[E1]};
  fail(f);
  f.resetCredits={availableCount:0,expiryDetailsUtcMs:[E1]};fail(f);
  f.resetCredits={availableCount:1,expiryDetailsUtcMs:[E1,E2]};fail(f);
  f.resetCredits={availableCount:-1,expiryDetailsUtcMs:null};fail(f);
  f.resetCredits={availableCount:2147483648,expiryDetailsUtcMs:null};fail(f);
});
test('B2: zero count with unprovided expiry is valid and keeps no dates',()=>{
  const f=base();f.resetCredits={availableCount:0,expiryDetailsUtcMs:null};
  const r=pass(f);
  assert.equal(r.presentation.availableCount,0);
  assert.equal(r.presentation.expirySlots,null);
});
test('B3: freshness changes at 2min boundary, expires at 6min',()=>{
  const f=base();
  assert.equal(pass(f,NOW).presentation.freshness,'FRESH_READ_ONLY');
  f.collectedAtUtcMs=NOW-120000;
  assert.equal(pass(f,NOW).presentation.freshness,'AGED_READ_ONLY');
  f.collectedAtUtcMs=NOW-359999;
  assert.equal(pass(f,NOW).presentation.freshness,'AGED_READ_ONLY');
  f.collectedAtUtcMs=NOW-360000;fail(f);
});
test('B3: clock skew tolerance max 3 seconds and rejection beyond',()=>{
  const f=base();
  f.collectedAtUtcMs=NOW+3000;pass(f,NOW);
  f.collectedAtUtcMs=NOW+3001;fail(f,NOW);
  assert.equal(check(raw(base()),'not-trusted').status,'HOLD_UNTRUSTED_VIEW_CLOCK');
});
test('B3: V1 time bounds inclusive at 2020 and 2101',()=>{
  const min=1577836800000,max=4133980800000;
  for(const timestamp of [min,max]){
    const f=base();
    f.collectedAtUtcMs=timestamp;
    f.fiveHour={...Q0};f.sevenDay={...Q0};
    f.resetCredits={availableCount:1,expiryDetailsUtcMs:[timestamp]};
    pass(f,timestamp);
  }
  const f=base();f.resetCredits.expiryDetailsUtcMs=[max+1];fail(f);
});
test('B3: session renewal must start sequence at 1, timestamps increase',()=>{
  const old=base();
  const next=base();next.sourceSessionId='d'.repeat(32);
  next.collectedAtUtcMs=NOW;next.sequence=2;
  fail(next,NOW,raw(old));
  next.sequence=1;pass(next,NOW,raw(old));
  const repeated=base();repeated.collectedAtUtcMs=NOW;
  fail(repeated,NOW,raw(old));
});
test('B4: duplicate JSON keys incl escaped names and noncanonical extras fail',()=>{
  const original=raw(base());
  const duplicated=original.replace('"schemaVersion":2',
    '"schemaVersion":2,"schemaVersion":2');
  fail(duplicated);
  const escaped=original.replace('"schemaVersion":2',
    '"schemaVersion":2,"schema\\u0056ersion":2');
  fail(escaped);
  const nested=original.replace('"availableCount":2',
    '"availableCount":2,"availableCount":2');
  fail(nested);
  const extra=base();extra.nonWeeklyWindow='30D';fail(extra);
});
test('B4: no stale previous frame, unknown source, or oversized frame',()=>{
  assert.equal(review(raw(base()),{trustedNowUtcMs:NOW}).status,
    'HOLD_UNENROLLED_SOURCE');
  const f=base();
  assert.equal(fail(f,NOW,'{').status,'HOLD_INVALID_PREVIOUS_V2_FRAME');
  const oversized=raw(base())+' '.repeat(2048);
  fail(oversized);
});
