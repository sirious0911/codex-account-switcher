import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {reviewOfflineQuotaViewCandidateV2} from './quota-view-candidate-v2.mjs';
import {reviewOfflineQuotaViewCandidate} from './quota-view-candidate-v1.mjs';

// Real .NET producer to real Node V2 reader, all offline synthetic data.
// No relay, HTTP, provider calls, authentication or Telegram.
test('Windows C# active-profile V2 5H/7D/R wire → Node reviewer',()=>{
  const result=spawnSync('dotnet',[
    'run','--project','windows/CodexAccountSwitcher.P3V2FrameChecks',
    '-c','Release','--no-restore','--nologo'
  ],{encoding:'utf8',timeout:60000,maxBuffer:262144});
  assert.equal(result.status,0,result.stderr);
  assert.match(result.stderr,/P3_WINDOWS_V2_PRODUCER_CHECKS_PASS=23/);
  assert.match(result.stderr,/REAL_CODEX_READS=0 NETWORK_SENDS=0 TELEGRAM_SENDS=0/);
  const frames=Object.fromEntries(
    result.stdout.trim().split(/\r?\n/).map(line=>{
      const offset=line.indexOf(' ');
      assert.ok(offset>0,'Expected a labeled JSON wire frame');
      const label=line.slice(0,offset);
      const raw=line.slice(offset+1);
      assert.ok(Buffer.byteLength(raw,'utf8')<=2048);
      assert.doesNotMatch(raw,/synthetic-account|credit-private|Bearer/);
      return [label,raw];
    })
  );
  assert.deepEqual(Object.keys(frames),['WEEKLY','ZERO','R_ONLY']);
  const now=1800000000000;
  function read(input,previousRaw=null) {
    const r=reviewOfflineQuotaViewCandidateV2(input,{
      trustedNowUtcMs:now,expectedSourceDeviceId:'windows-b',previousRaw
    });
    assert.equal(r.status,'OFFLINE_V2_CANDIDATE_ONLY');
    assert.equal(r.actualSendAuthorized,false);
    assert.equal(r.senderTransitionAuthorized,false);
    assert.equal(r.realCrossHostSyncAuthorized,false);
    return r;
  }
  const weekly=read(frames.WEEKLY);
  assert.equal(weekly.presentation.fiveHourRemainingPercent,85);
  assert.equal(weekly.presentation.sevenDayRemainingPercent,62);
  assert.equal(weekly.presentation.availableCount,2);
  assert.deepEqual(
    weekly.presentation.expirySlots.map(x=>x.expiresAtUtcMs),
    [1800172800000,1800259200000]
  );
  assert.equal(weekly.presentation.expirySlots[0].position,1);
  assert.equal(weekly.presentation.expirySlots[1].position,2);

  const zero=read(frames.ZERO,frames.WEEKLY);
  assert.equal(zero.presentation.availableCount,0);
  assert.equal(zero.presentation.expirySlots,null);
  assert.equal(zero.presentation.expiryDetailsProvided,false);
  assert.equal(zero.view.sourceSessionId,weekly.view.sourceSessionId);
  assert.equal(zero.view.sequence,2);
  assert.equal(
    reviewOfflineQuotaViewCandidateV2(frames.ZERO,{
      trustedNowUtcMs:now,expectedSourceDeviceId:'windows-b',
      previousRaw:frames.ZERO
    }).status,'HOLD_REPLAY_OR_REORDERED_VIEW'
  );

  const free=read(frames.R_ONLY);
  assert.equal(free.presentation.availableCount,2);
  assert.equal(free.presentation.fiveHourRemainingPercent,null);
  assert.equal(free.presentation.sevenDayRemainingPercent,null);
  assert.equal(free.presentation.inferredNonWeeklyWindow,null);
  assert.equal(free.view.sequence,1);
  assert.equal(free.view.accountIdSha256,weekly.view.accountIdSha256);
  assert.equal(reviewOfflineQuotaViewCandidate(frames.WEEKLY,{
    trustedNowUtcMs:now
  }).view,null);
  console.log('WINDOWS_CSHARP_V2_TO_NODE_READ_ONLY_INTEROP_PASS=1');
  console.log('V2_ZERO_R_ONLY_REPLAY_AND_V1_ISOLATION_PASS=1');
});
