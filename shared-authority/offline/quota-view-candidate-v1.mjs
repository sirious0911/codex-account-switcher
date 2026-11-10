// Pure, NO-NETWORK, NON-AUTHORIZING 5H/7D observer candidate review.
// Node baseline for Windows/.NET and macOS/Swift golden fixtures.
// This does not read Codex accounts, receive HTTPS, switch devices, trust
// timestamps, issue a Sender lease, write an Outbox or send Telegram.
// trustedNowUtcMs is supplied by the caller/test harness, not an OS anchor.
import {parseUniqueJsonFrame} from './strict-json-frame.mjs';

const TOP=new Set([
  'schemaVersion','sourceDeviceId','sourceSessionId','sequence',
  'collectedAtUtcMs','accountIdSha256','fiveHour','sevenDay',
  'actualSendAuthorized','automaticRetryAllowed',
  'productionReservationAuthorized','senderTransitionAuthorized'
]);
const PERCENT=new Set(['remainingPercent','resetAtUtcMs']);
const DEVICE=/^[A-Za-z0-9-]{1,48}$/;
const SESSION=/^[a-f0-9]{32}$/;
const HASH=/^[a-f0-9]{64}$/;
const MIN_TIME=1577836800000;
const MAX_TIME=4133980800000;
const MAX_AGE=600000; // read-only display observation, not Sender lease
const MAX_FUTURE_SKEW=3000;
const SAFE=Object.freeze({
  actualSendAuthorized:false,
  automaticRetryAllowed:false,
  productionReservationAuthorized:false,
  senderTransitionAuthorized:false,
  realDeviceAuthenticated:false,
  realCrossHostSyncAuthorized:false
});
const hold=status=>Object.freeze({status,view:null,...SAFE});
const reviewed=x=>Object.freeze({status:'OFFLINE_VIEW_CANDIDATE_ONLY',view:{
  sourceDeviceId:x.sourceDeviceId,
  sourceSessionId:x.sourceSessionId,
  sequence:x.sequence,collectedAtUtcMs:x.collectedAtUtcMs,
  accountIdSha256:x.accountIdSha256,
  fiveHour:{...x.fiveHour},sevenDay:{...x.sevenDay}
},...SAFE});
function exact(o,fields){
  return !!o&&typeof o==='object'&&!Array.isArray(o) &&
    Object.keys(o).length===fields.size &&
    Object.keys(o).every(k=>fields.has(k));
}
function timestamp(x){
  return Number.isSafeInteger(x)&&x>=MIN_TIME&&x<=MAX_TIME;
}
function quota(obj,collected,days){
  if(!exact(obj,PERCENT))return false;
  const {remainingPercent,resetAtUtcMs}=obj;
  if(remainingPercent!==null &&
     (!Number.isSafeInteger(remainingPercent)||
      remainingPercent<0||remainingPercent>100))return false;
  if(resetAtUtcMs!==null && (!timestamp(resetAtUtcMs) ||
     resetAtUtcMs<collected-600000 ||
     resetAtUtcMs>collected+days*86400000))return false;
  return true;
}
function decode(raw){
  if(typeof raw!=='string' ||
     Buffer.byteLength(raw,'utf8')>2048)return null;
  try{
    const x=parseUniqueJsonFrame(raw,2048,8);
    if(!exact(x,TOP) ||x.schemaVersion!==1||
       !DEVICE.test(x.sourceDeviceId??'') ||
       !SESSION.test(x.sourceSessionId??'') ||
       !Number.isSafeInteger(x.sequence)||x.sequence<1||
       x.sequence>1000000000||
       !timestamp(x.collectedAtUtcMs) ||
       !HASH.test(x.accountIdSha256??'')||
       !quota(x.fiveHour,x.collectedAtUtcMs,1/4)||
       !quota(x.sevenDay,x.collectedAtUtcMs,8) ||
       ['actualSendAuthorized','automaticRetryAllowed',
         'productionReservationAuthorized','senderTransitionAuthorized']
         .some(k=>x[k]!==false))
      return null;
    if(x.fiveHour.remainingPercent===null &&
       x.sevenDay.remainingPercent===null)return null;
    return x;
  }catch{return null;}
}
export function reviewOfflineQuotaViewCandidate(
  raw,{trustedNowUtcMs,previousRaw=null}={}
){
  if(!timestamp(trustedNowUtcMs))return hold('HOLD_UNTRUSTED_VIEW_CLOCK');
  const x=decode(raw);
  if(!x)return hold('HOLD_INVALID_VIEW_FRAME');
  if(x.collectedAtUtcMs>trustedNowUtcMs+MAX_FUTURE_SKEW ||
     trustedNowUtcMs-x.collectedAtUtcMs>MAX_AGE)
    return hold('HOLD_STALE_OR_FUTURE_VIEW');
  if(previousRaw!==null){
    const old=decode(previousRaw);
    if(!old)return hold('HOLD_INVALID_PREVIOUS_VIEW');
    if(old.sourceDeviceId!==x.sourceDeviceId)
      return hold('HOLD_SOURCE_DEVICE_CONFLICT');
    if(old.accountIdSha256!==x.accountIdSha256)
      return hold('HOLD_ACCOUNT_IDENTITY_CHANGED');
    if(x.collectedAtUtcMs<=old.collectedAtUtcMs)
      return hold('HOLD_REPLAY_OR_REORDERED_VIEW');
    if(x.sourceSessionId===old.sourceSessionId &&
       x.sequence<=old.sequence)
      return hold('HOLD_REPLAY_OR_REORDERED_VIEW');
    if(x.sourceSessionId!==old.sourceSessionId &&
       x.sequence!==1)
      return hold('HOLD_NEW_SESSION_SEQUENCE_NOT_INITIAL');
  }
  return reviewed(x);
}
