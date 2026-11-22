// Single local entrypoint: authority-owned Windows job -> real process exit
// -> native read-only provider verifier -> SQLite reconciliation.
// Deliberately NOT installed, scheduled, or exposed as an HTTP endpoint.
// Caller must supply a trusted quota deadline and locally provisioned
// credentials; the .NET host itself independently demands activated settings.
import {CodexLocalSettlementCoordinator}
  from './codex-chat-local-settlement-coordinator.mjs';
import {CodexWindowsPinnedHostRunner}
  from './codex-chat-windows-pinned-host-runner.mjs';
import {randomBytes} from 'node:crypto';
import {codexChatResetEventKey,signCodexChatSenderRequest}
  from './codex-chat-authority.mjs';
import {assertReviewedDotNetBundle}
  from './codex-reviewed-dotnet-bundle.mjs';
import {createCodexChatAuthorityHttpsServer}
  from './codex-chat-https.mjs';

export function createCodexWindowsLocalRuntime({
  createAuthority,resolveApprovedPrivateJob,
  reviewedHostExecutable,reviewedHostSha256,reviewedHostBundleSha256,
  reviewedWorkerExecutable=null,reviewedWorkerSha256=null,
  reviewedWorkerBundleSha256=null,
  ownerEnrollment=null,now=Date.now
}={}) {
  if(typeof createAuthority!=='function' ||
     typeof resolveApprovedPrivateJob!=='function' ||
     typeof now!=='function')
    throw new TypeError('Explicit local authority and private job resolver required');

  // This is local-server enrollment, NEVER the user-facing UI draft.
  // Without exact operator-controlled enrollment, no lease/reservation or
  // Windows sender is started, even when a valid due HMAC arrives.
  if(ownerEnrollment!==null &&
     (!ownerEnrollment.enabled ||
      !/^[A-Za-z0-9-]{1,48}$/.test(ownerEnrollment.deviceId??'') ||
      !/^[a-f0-9]{64}$/.test(ownerEnrollment.hmacKeyHex??'') ||
      !/^[a-f0-9]{64}$/.test(ownerEnrollment.accountSha256??'') ||
      !/^[a-f0-9]{8}-[a-f0-9-]{27,}$/.test(ownerEnrollment.profileId??'') ||
      !/^[a-f0-9]{8}-[a-f0-9-]{27,}$/.test(ownerEnrollment.threadId??'') ||
      typeof ownerEnrollment.fiveHour!=='boolean' ||
      typeof ownerEnrollment.sevenDay!=='boolean'))
    throw new Error('HOLD_OWNER_ENROLLMENT_INVALID');

  // Start with an exact reviewed executable; no binary discovery or fallback.
  const runner=new CodexWindowsPinnedHostRunner({
    executablePath:reviewedHostExecutable,
    expectedSha256:reviewedHostSha256,
    expectedBundleSha256:reviewedHostBundleSha256
  });
  let coordinator;
  const authority=createAuthority({
    verifySenderQuiescence:async proof=>{
      if(!coordinator)throw Error('Windows local supervisor uninitialized');
      return coordinator.verifySenderQuiescence(proof);
    }
  });
  coordinator=new CodexLocalSettlementCoordinator({
    authority,now,
    runReviewedWindowsJob:async (event,{signal}={})=>{
      const privateJob=await resolveApprovedPrivateJob(Object.freeze({...event}));
      if(!privateJob || typeof privateJob!=='object')
        throw Error('No locally approved Windows job');
      // A private input supplied by a job resolver cannot approve its own
      // managed DLLs. Bind the exact path and BOTH hashes to independent,
      // reviewed, authority-local anchors before any active send worker.
      if(privateJob.EnableLiveDispatch===true){
        if(privateJob.NativeWorkerExecutable!==reviewedWorkerExecutable ||
           privateJob.NativeWorkerSha256!==reviewedWorkerSha256 ||
           privateJob.NativeWorkerBundleSha256!==reviewedWorkerBundleSha256)
          throw Error('HOLD_REVIEWED_WORKER_ANCHOR_NOT_BOUND');
        assertReviewedDotNetBundle({
          executablePath:reviewedWorkerExecutable,
          expectedExecutableSha256:reviewedWorkerSha256,
          expectedBundleSha256:reviewedWorkerBundleSha256
        });
      }
      return runner.runOnce(event,privateJob,{signal});
    }
  });
  let closed=false;
  const localSigned=(method,path,body)=>{
    const timestampMs=now(),nonce=randomBytes(16).toString('hex');
    const bodyUtf8=JSON.stringify(body);
    const signature=signCodexChatSenderRequest({
      keyHex:ownerEnrollment.hmacKeyHex,
      deviceId:ownerEnrollment.deviceId,method,path,
      timestampMs,nonce,bodyUtf8
    });
    return authority.dispatch({method,path,bodyUtf8,headers:{
      'x-device-id':ownerEnrollment.deviceId,
      'x-timestamp-ms':String(timestampMs),
      'x-nonce':nonce,'x-signature':signature
    }});
  };
  // Called by a verified *local* due-pipe ingress, not HTTP.
  // Node acquires/reserves once; the pinned Windows host receives this epoch
  // and MUST use independent server readiness instead of reacquiring a lease.
  const runOwnerDue=async (due,{signal,onReserved}={})=>{
    if(signal?.aborted)
      return Object.freeze({code:'HOLD_OWNER_DISABLED_BEFORE_SEND',settled:false});
    if(closed || ownerEnrollment===null)
      return Object.freeze({code:'HOLD_OWNER_NOT_PROVISIONED',settled:false});
    if(!due || due.profileId!==ownerEnrollment.profileId ||
       due.accountSha256!==ownerEnrollment.accountSha256 ||
       due.threadId!==ownerEnrollment.threadId ||
       !['CODEX_FIVE_HOUR','CODEX_SEVEN_DAY'].includes(due.lane) ||
       !Number.isSafeInteger(due.resetAtUnix) ||
       !Number.isSafeInteger(due.observedAtUnix) ||
       (due.lane==='CODEX_FIVE_HOUR'&&!ownerEnrollment.fiveHour) ||
       (due.lane==='CODEX_SEVEN_DAY'&&!ownerEnrollment.sevenDay))
      return Object.freeze({code:'HOLD_OWNER_DUE_NOT_APPROVED',settled:false});
    const age=now()-due.resetAtUnix*1000;
    const observedAge=now()-due.observedAtUnix*1000;
    if(age<0 || age>600_000 || observedAge<0 ||
       observedAge>360_000)
      return Object.freeze({code:'HOLD_OWNER_DUE_EXPIRED',settled:false});
    const path='/v1/codex-chat';
    const key=codexChatResetEventKey({
      accountSha256:due.accountSha256,threadId:due.threadId,
      lane:due.lane,resetAtUnix:due.resetAtUnix
    });
    const lease=localSigned('POST',path+'/lease/acquire',{ttlMs:55_000});
    if(lease.code!=='LEASE_ACQUIRED')
      return Object.freeze({code:lease.code,settled:false});
    if(signal?.aborted){
      localSigned('POST',path+'/lease/release',{epoch:lease.epoch});
      return Object.freeze({code:'HOLD_OWNER_DISABLED_BEFORE_RESERVE',settled:false});
    }
    const reservation=localSigned('POST',path+'/events/reserve',{
      epoch:lease.epoch,eventKey:key,lane:due.lane,
      accountSha256:due.accountSha256,threadId:due.threadId,
      resetAtUnix:due.resetAtUnix
    });
    if(reservation.code!=='EVENT_RESERVED_NO_SEND'){
      localSigned('POST',path+'/lease/release',{epoch:lease.epoch});
      return Object.freeze({code:reservation.code,settled:false});
    }
    // Notification only after durable SQLite reservation. Pipe ingress can
    // ACK promptly while any later uncertainty remains locked in the ledger.
    try{onReserved?.(Object.freeze({eventKey:key,epoch:lease.epoch}));}
    catch{return Object.freeze({code:'HOLD_LOCAL_OWNER_RECEIPT_UNCERTAIN',settled:false});}
    if(signal?.aborted)
      return Object.freeze({code:'HOLD_OWNER_DISABLED_RESERVED_NO_RETRY',settled:false});
    // Preserve the exact verified local quota-observer identity and its
    // observation timestamp through the central reservation/Job boundary.
    // Omitting them made the Windows ScheduledJob.ProfileId default to
    // Guid.Empty and the worker reject EVERY otherwise-valid real dispatch.
    const dispatched=await coordinator.runOnce({
      eventKey:key,profileId:due.profileId,
      accountSha256:due.accountSha256,threadId:due.threadId,
      lane:due.lane,resetAtUnix:due.resetAtUnix,
      observedAtUnix:due.observedAtUnix,
      ownerDevice:ownerEnrollment.deviceId,
      epoch:lease.epoch,leaseExpiresAtMs:lease.expiresAtMs
    },{signal});
    if(dispatched.settled)
      localSigned('POST',path+'/lease/release',{epoch:lease.epoch});
    return dispatched;
  };
  return Object.freeze({
    createLoopbackHttpsServer({tlsKey,tlsCert}={}){
      if(closed)throw Error('HOLD_OWNER_RUNTIME_CLOSED');
      return createCodexChatAuthorityHttpsServer({
        authority,tlsKey,tlsCert,listenHost:'127.0.0.1',allowRemote:false
      });
    },
    runOwnerDue,
    async runDueOnce(event) {
      if(closed)return Object.freeze({
        code:'HOLD_LOCAL_RUNTIME_CLOSED',settled:false
      });
      return coordinator.runOnce(event);
    },
    close() {
      if(closed)return;
      closed=true;
      authority.close();
    }
  });
}
