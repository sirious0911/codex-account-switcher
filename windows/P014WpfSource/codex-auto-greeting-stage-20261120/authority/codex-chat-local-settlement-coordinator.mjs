// Local-only authority-owned bridge between one reviewed Windows Job runner
// and independently verified provider completion in CodexChatSenderAuthority.
// No HTTP route, user-provided ACK, Codex send, process launcher or credentials.
// Production MUST inject a reviewed, server-owned Windows Job Object runner.
// A new coordinator after crash has no trusted job proof: fail closed.
const HASH=/^[a-f0-9]{64}$/;
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const DEVICE=/^[A-Za-z0-9-]{1,48}$/;
const LANES=new Set(['CODEX_FIVE_HOUR','CODEX_SEVEN_DAY']);

function validEvent(q) {
  return q && !Array.isArray(q) &&
    HASH.test(q.eventKey??'') && HASH.test(q.accountSha256??'') &&
    UUID.test(q.threadId??'') && DEVICE.test(q.ownerDevice??'') &&
    Number.isSafeInteger(q.epoch) && q.epoch>0 &&
    Number.isSafeInteger(q.resetAtUnix) && q.resetAtUnix>0 &&
    LANES.has(q.lane);
}
const hold=code=>Object.freeze({code,settled:false});

export class CodexLocalSettlementCoordinator {
  #runner;#authority;#now;#records=new Map();
  constructor({runReviewedWindowsJob,authority,now=Date.now}={}) {
    if(typeof runReviewedWindowsJob!=='function' ||
       !authority || typeof authority.reconcileCompletedTurn!=='function' ||
       typeof now!=='function')
      throw new TypeError('Local server-owned runner and authority required');
    this.#runner=runReviewedWindowsJob;
    this.#authority=authority;
    this.#now=now;
  }
  // Called ONLY by the local authority scheduler after durable reservation.
  // This is not an RPC method and cannot be called through a signed client.
  async runOnce(event,{signal}={}) {
    if(!validEvent(event))return hold('HOLD_INVALID_LOCAL_EVENT');
    if(signal?.aborted)return hold('HOLD_OWNER_DISABLED_BEFORE_SEND');
    if(this.#records.has(event.eventKey))
      return hold('HOLD_DUPLICATE_OR_UNCERTAIN_NO_RETRY');
    const record={...event,acceptedTurnId:null,jobStopped:false,
      startedAtMs:this.#now(),state:'RUNNING'};
    // Register BEFORE the first external process operation.
    this.#records.set(event.eventKey,record);
    let result;
    try {
      result=await this.#runner(Object.freeze({...event}),{signal});
    }catch{
      record.state='UNCERTAIN';
      return hold('HOLD_SUPERVISED_JOB_UNCERTAIN_NO_RETRY');
    }
    if(!result || result.eventKey!==record.eventKey ||
       result.ownerDevice!==record.ownerDevice ||
       result.epoch!==record.epoch ||
       result.accountSha256!==record.accountSha256 ||
       result.threadId!==record.threadId ||
       result.exitCode!==0 || result.allJobProcessesTerminated!==true ||
       result.outcomeUncertain!==false ||
       result.status!=='TURN_START_ACCEPTED_AWAIT_PROVIDER_VERIFICATION' ||
       !UUID.test(result.turnId??'')) {
      record.state='UNCERTAIN';
      return hold('HOLD_JOB_RESULT_OR_IDENTITY_UNPROVEN_NO_RETRY');
    }
    // Local runner must be genuinely reviewed and server-owned. Its
    // allJobProcessesTerminated proof MUST derive from Windows Job Object,
    // not from a remote client or solely from a process exit-code.
    record.jobStopped=true;
    record.acceptedTurnId=result.turnId;
    record.state='STOPPED_AWAIT_PROVIDER';
    let settlement;
    try {
      settlement=await this.#authority.reconcileCompletedTurn({
        eventKey:record.eventKey,turnId:record.acceptedTurnId
      });
    }catch{
      return hold('HOLD_SERVER_RECONCILIATION_UNKNOWN_NO_RETRY');
    }
    if(settlement?.code!=='PROVIDER_TURN_COMPLETION_VERIFIED' &&
       settlement?.code!=='ALREADY_INDEPENDENTLY_VERIFIED')
      return hold(settlement?.code??'HOLD_COMPLETION_NOT_PROVED');
    record.state='PROVIDER_COMPLETED';
    return Object.freeze({code:settlement.code,settled:true});
  }
  // Authority callback: never infer quiescence from an arbitrary process
  // table scan or a client ACK. A different coordinator instance denies it.
  async verifySenderQuiescence(q) {
    if(!q || !HASH.test(q.eventKey??''))throw Error('Untrusted event');
    const record=this.#records.get(q.eventKey);
    if(!record || record.ownerDevice!==q.ownerDevice ||
       record.epoch!==q.epoch ||
       record.accountSha256!==q.accountSha256 ||
       record.threadId!==q.threadId ||
       record.acceptedTurnId!==q.turnId ||
       !record.jobStopped)
      throw Error('No server-owned Windows Job completion evidence');
    const verifiedAtMs=this.#now();
    if(!Number.isSafeInteger(verifiedAtMs) ||
       verifiedAtMs<record.startedAtMs)
      throw Error('Invalid server time');
    return Object.freeze({
      eventKey:record.eventKey,ownerDevice:record.ownerDevice,
      epoch:record.epoch,noActiveSendWorker:true,verifiedAtMs
    });
  }
}
