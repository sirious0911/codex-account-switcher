// Single per-user Windows due receiver lifecycle.
// No configured operational runtime means no send. A valid pipe frame is ACKed
// only AFTER its event has been durably reserved in the central SQLite owner.
// Checks user/operational ON/OFF on every due and during an active Windows Job;
// OFF, shutdown and account changes abort the supervised host and hold outcome.
import {createCodexDueLocalPipe} from './codex-due-local-pipe.mjs';

const OFF=()=>({master:false,fiveHour:false,sevenDay:false});
export function createCodexWindowsOwnerSession({
  pipeName,secretHex,profileId,accountSha256,threadId,
  readSwitches=OFF,runOwnerDue=null,now=Date.now,pollMs=200
}={}) {
  if(typeof readSwitches!=='function' || typeof runOwnerDue!=='function' ||
     typeof now!=='function' || !Number.isSafeInteger(pollMs) ||
     pollMs<50 || pollMs>1000)
    throw TypeError('Explicit owner session callbacks required');
  let running=false,closing=false,lastCode='NOT_STARTED';
  const active=new Set();
  const allowed=due=>{
    if(closing || !running)return false;
    try{
      const sw=readSwitches();
      return !!sw && sw.master===true &&
        (due.lane==='CODEX_FIVE_HOUR'
          ?sw.fiveHour===true:sw.sevenDay===true);
    }catch{return false;}
  };
  const pipe=createCodexDueLocalPipe({
    pipeName,secretHex,profileId,accountSha256,threadId,
    masterEnabled:true,fiveHourEnabled:true,sevenDayEnabled:true,
    authorizeDue:allowed,now,
    onVerifiedDue:async due=>{
      if(!allowed(due))return 'HOLD_LOCAL_SENDER_OFF';
      const controller=new AbortController();
      active.add(controller);
      let acked=false;
      let releaseAck=()=>{};
      const ack=new Promise(resolve=>{releaseAck=resolve;});
      const emitAck=code=>{
        if(acked)return;
        acked=true;
        releaseAck(code);
      };
      // Polling only while a job is in flight, not a perpetual background
      // process. This also handles a user switching the master OFF during send.
      const watcher=setInterval(()=>{
        if(!allowed(due))controller.abort();
      },pollMs);
      watcher.unref?.();
      const operation=Promise.resolve().then(async()=>{
        if(!allowed(due)){
          controller.abort();return {code:'HOLD_OWNER_DISABLED_BEFORE_SEND'};
        }
        return runOwnerDue(Object.freeze({...due}),{
          signal:controller.signal,
          onReserved:()=>emitAck('DUE_RECEIVED_OWNER_PENDING')
        });
      });
      operation.then(result=>{
        lastCode=result?.code??'HOLD_OWNER_RESULT_UNVERIFIED';
        emitAck('HOLD_OWNER_NOT_RESERVED');
      },()=>{
        lastCode='HOLD_OWNER_JOB_UNCERTAIN_NO_RETRY';
        emitAck('HOLD_OWNER_JOB_UNCERTAIN_NO_RETRY');
      }).finally(()=>{
        clearInterval(watcher);
        active.delete(controller);
      });
      // The sender's TCP/pipe client times out after 4s. Give no positive
      // receipt unless SQLite reservation callback happened first.
      const timer=setTimeout(()=>emitAck('HOLD_OWNER_RESERVATION_TIMEOUT'),3000);
      timer.unref?.();
      const outcome=await ack;
      clearTimeout(timer);
      return outcome;
    }
  });
  return Object.freeze({
    async start(){
      if(running || closing)throw Error('HOLD_OWNER_SESSION_ALREADY_USED');
      await pipe.listen();running=true;lastCode='OWNER_READY';
      return Object.freeze({code:'OWNER_RUNNING',path:pipe.path});
    },
    status(){
      return Object.freeze({running,closing,inFlight:active.size,lastCode});
    },
    async stop(){
      if(closing)return;
      closing=true;
      for(const c of active)c.abort();
      if(running){await pipe.close();running=false;}
      lastCode='OWNER_STOPPED';
    }
  });
}
