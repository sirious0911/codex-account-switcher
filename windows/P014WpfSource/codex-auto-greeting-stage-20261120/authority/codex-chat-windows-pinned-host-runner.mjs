// Authority-process-owned Windows orchestration host launcher.
// A client HTTP ACK can never call this; only a server-owned scheduler can.
// The EXE must be reviewed, SHA-pinned, and must itself use a Windows Job Object
// and return its own zero-active-descendant proof. This launcher NEVER calls
// Codex directly; it cannot override a disabled on-disk activation switch.
import {spawn} from 'node:child_process';
import {readFileSync,lstatSync} from 'node:fs';
import {createHash} from 'node:crypto';
import {basename,isAbsolute} from 'node:path';
import {assertReviewedDotNetBundle}
  from './codex-reviewed-dotnet-bundle.mjs';

const HASH=/^[a-f0-9]{64}$/;
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const HOST='Codex-Windows-Job-Orchestrator.exe';
const exact=(o,keys)=>o && typeof o==='object' && !Array.isArray(o) &&
  Object.keys(o).sort().join('|')===keys.slice().sort().join('|');

export function parsePinnedWindowsJobReceipt(receipt,event) {
  if(!exact(receipt,['Status','WorkerStarted','ActualTurnProven',
       'TurnId','LocalJobExit']) ||
     receipt.Status!=='TURN_START_ACCEPTED_AWAIT_PROVIDER_VERIFICATION' ||
     receipt.WorkerStarted!==true || receipt.ActualTurnProven!==false ||
     !UUID.test(receipt.TurnId??'') ||
     !exact(receipt.LocalJobExit,[
       'EventKey','AccountSha256','ExistingThreadId','Lane',
       'ResetAtUnix','OwnerDevice','OwnerEpoch','TurnId',
       'AllJobProcessesTerminated'
     ]))
    throw new Error('HOLD_UNTRUSTED_WINDOWS_JOB_RECEIPT');
  const e=receipt.LocalJobExit;
  if(e.EventKey!==event.eventKey ||
     e.AccountSha256!==event.accountSha256 ||
     e.ExistingThreadId!==event.threadId ||
     e.Lane!==event.lane || e.ResetAtUnix!==event.resetAtUnix ||
     e.OwnerDevice!==event.ownerDevice ||
     e.OwnerEpoch!==event.epoch || e.TurnId!==receipt.TurnId ||
     e.AllJobProcessesTerminated!==true)
    throw new Error('HOLD_JOB_EVENT_OR_PROCESS_PROOF_MISMATCH');
  return Object.freeze({...event,turnId:receipt.TurnId,
    status:receipt.Status,exitCode:0,
    allJobProcessesTerminated:true,outcomeUncertain:false});
}

function exactExecutable(executablePath,expectedSha256,expectedBundleSha256) {
  if(process.platform!=='win32' ||
     typeof executablePath!=='string' ||
     !isAbsolute(executablePath) || basename(executablePath)!==HOST ||
     !HASH.test(expectedSha256??''))
    throw new TypeError('Reviewed pinned Windows host path required');
  const info=lstatSync(executablePath);
  if(!info.isFile() || info.isSymbolicLink() ||
     createHash('sha256').update(readFileSync(executablePath))
       .digest('hex')!==expectedSha256)
    throw new Error('Windows host executable SHA256 mismatch');
  assertReviewedDotNetBundle({
    executablePath,expectedExecutableSha256:expectedSha256,
    expectedBundleSha256
  });
}

export class CodexWindowsPinnedHostRunner {
  #exe;#hash;#bundleSha;#timeout;#attempted=new Set();
  constructor({executablePath,expectedSha256,
    expectedBundleSha256,timeoutMs=55_000}={}) {
    exactExecutable(executablePath,expectedSha256,expectedBundleSha256);
    if(!Number.isSafeInteger(timeoutMs) ||
       timeoutMs<1000 || timeoutMs>60_000)
      throw new TypeError('Invalid host timeout');
    this.#exe=executablePath;this.#hash=expectedSha256;
    this.#bundleSha=expectedBundleSha256;
    this.#timeout=timeoutMs;
  }
  async runOnce(event,privateJob,{signal}={}) {
    if(signal?.aborted)
      throw new Error('HOLD_OWNER_DISABLED_BEFORE_START');
    if(!event || !HASH.test(event.eventKey??'') ||
       !HASH.test(event.accountSha256??'') ||
       !UUID.test(event.threadId??'') ||
       !['CODEX_FIVE_HOUR','CODEX_SEVEN_DAY'].includes(event.lane) ||
       !Number.isSafeInteger(event.epoch) || event.epoch<=0 ||
       !Number.isSafeInteger(event.resetAtUnix) ||
       typeof event.ownerDevice!=='string')
      throw new TypeError('Invalid pinned event');
    if(this.#attempted.has(event.eventKey))
      throw new Error('HOLD_LOCAL_JOB_ALREADY_ATTEMPTED_NO_RETRY');
    this.#attempted.add(event.eventKey);
    if(!privateJob || privateJob.AccountSha256!==event.accountSha256 ||
       privateJob.ThreadId!==event.threadId ||
       privateJob.Lane!==event.lane ||
       privateJob.ResetAtUnix!==event.resetAtUnix ||
       privateJob.DeviceId!==event.ownerDevice ||
       (event.profileId!==undefined &&
         (!UUID.test(event.profileId) ||
          privateJob.ProfileId!==event.profileId)) ||
       (event.observedAtUnix!==undefined &&
         (!Number.isSafeInteger(event.observedAtUnix) ||
          privateJob.ObservedAtUnix!==event.observedAtUnix)))
      throw new Error('HOLD_PRIVATE_INPUT_EVENT_MISMATCH');
    // The OS can change a binary between operations. Pin immediately before
    // spawn as well. Never print or persist the stdin payload or credentials.
    exactExecutable(this.#exe,this.#hash,this.#bundleSha);
    // The authority-local owner must not make the Windows host acquire a
    // second lease for an event already reserved by this same owner.
    // Only locally derived current epoch and expiry are handed over.
    const preOwned=Number.isSafeInteger(event.leaseExpiresAtMs) &&
      event.leaseExpiresAtMs>Date.now()+5000;
    if(event.leaseExpiresAtMs!==undefined && !preOwned)
      throw new Error('HOLD_PRE_RESERVED_LEASE_EXPIRED_NO_FALLBACK');
    const dispatchJob=preOwned?{
      ...privateJob,PreReservedEpoch:event.epoch,
      PreReservedEventKey:event.eventKey,
      PreReservedLeaseExpiresAtMs:event.leaseExpiresAtMs
    }:privateJob;
    const privateJson=JSON.stringify(dispatchJob);
    if(Buffer.byteLength(privateJson,'utf8')>12_000 ||
       Buffer.byteLength(privateJson,'utf8')<100)
      throw new Error('HOLD_PRIVATE_INPUT_SIZE');
    return await new Promise((resolve,reject)=>{
      let settled=false,child,stdout='',overflow=false;
      let cancelHost=()=>{};
      const stop=(error,value)=>{
        if(settled)return;
        settled=true;clearTimeout(timer);
        signal?.removeEventListener('abort',cancelHost);
        if(error)reject(error);else resolve(value);
      };
      try {
        child=spawn(this.#exe,['--approved-server-owned-stdin'],{
          shell:false,windowsHide:true,detached:false,
          stdio:['pipe','pipe','ignore']
        });
      }catch {
        reject(new Error('HOLD_WINDOWS_HOST_SPAWN'));return;
      }
      const timer=setTimeout(()=>{
        try{child.kill();}catch{}
        stop(new Error('HOLD_WINDOWS_HOST_TIMEOUT_NO_RETRY'));
      },this.#timeout);
      cancelHost=()=>{
        try{child.kill();}catch{}
        stop(new Error('HOLD_OWNER_SWITCHED_OFF_NO_RETRY'));
      };
      if(signal?.aborted)cancelHost();
      else signal?.addEventListener('abort',cancelHost,{once:true});
      child.stdout.setEncoding('utf8');
      child.stdout.on('data',chunk=>{
        if(stdout.length+chunk.length>4096){overflow=true;return;}
        stdout+=chunk;
      });
      child.once('error',()=>{
        stop(new Error('HOLD_WINDOWS_HOST_PROCESS_ERROR_NO_RETRY'));
      });
      child.once('close',(code,signal)=>{
        try{exactExecutable(this.#exe,this.#hash,this.#bundleSha);}
        catch{stop(new Error('HOLD_WINDOWS_HOST_BUNDLE_CHANGED_NO_RETRY'));return;}
        if(code!==0 || signal || overflow ||
           stdout.trim().split(/\r?\n/).length!==1){
          stop(new Error('HOLD_WINDOWS_HOST_EXIT_OR_OUTPUT_UNCERTAIN'));
          return;
        }
        let receipt;
        try{receipt=JSON.parse(stdout.trim());}
        catch{
          stop(new Error('HOLD_WINDOWS_JOB_INVALID_OUTPUT'));return;
        }
        try{stop(null,parsePinnedWindowsJobReceipt(receipt,event));}
        catch{stop(new Error('HOLD_WINDOWS_JOB_RECEIPT_UNVERIFIED'));}
      });
      child.stdin.on('error',()=>{}); // close handler determines uncertainty
      child.stdin.end(privateJson);
    });
  }
}
