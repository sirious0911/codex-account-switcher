// P3 read-only quota telemetry relay over authenticated TLS.
// The publisher token authorizes ONLY a V1 5H/7D view upload. It never
// authorizes Telegram, an account switch, a Sender lease, or credit consumption.
import {createServer} from 'node:https';
import {createHash,timingSafeEqual} from 'node:crypto';
import {reviewOfflineQuotaViewCandidate} from '../offline/quota-view-candidate-v1.mjs';

const DEVICE=/^[A-Za-z0-9-]{1,48}$/;
const TOKEN=/^[A-Za-z0-9_-]{32,256}$/;
const MAX_BODY=2048;
const HEADERS=Object.freeze({
  'content-type':'application/json; charset=utf-8',
  'cache-control':'no-store, max-age=0',
  'x-content-type-options':'nosniff',
  'referrer-policy':'no-referrer'
});

function digest(token) {
  return createHash('sha256').update(token,'utf8').digest();
}

function authenticated(header,expected) {
  if(typeof header!=='string'||!header.startsWith('Bearer '))return false;
  const token=header.slice(7);
  if(!TOKEN.test(token))return false;
  return timingSafeEqual(digest(token),expected);
}

function duplicateSecurityHeaders(rawHeaders) {
  const counts=new Map();
  for(let i=0;i<rawHeaders.length;i+=2){
    const name=rawHeaders[i].toLowerCase();
    if(name==='authorization'||name==='content-type'||name==='host'){
      counts.set(name,(counts.get(name)??0)+1);
      if(counts.get(name)>1)return true;
    }
  }
  return false;
}

function write(res,code,content) {
  const data=JSON.stringify(content);
  res.writeHead(code,{...HEADERS,'content-length':Buffer.byteLength(data)});
  res.end(data);
}

function safeResponse(status,frame=null) {
  return {
    schemaVersion:1,status,frame,
    actualSendAuthorized:false,automaticRetryAllowed:false,
    productionReservationAuthorized:false,senderTransitionAuthorized:false
  };
}

function receive(req){
  return new Promise((resolve,reject)=>{
    const parts=[];
    let length=0;
    req.on('data',chunk=>{
      length+=chunk.length;
      if(length>MAX_BODY){
        reject(new Error('OVERSIZED'));
        req.destroy();
        return;
      }
      parts.push(chunk);
    });
    req.on('end',()=>resolve(Buffer.concat(parts).toString('utf8')));
    req.on('error',reject);
  });
}

/**
 * The source is fixed at enrollment. This service must run with a server
 * certificate trusted by its clients and with distinct high-entropy tokens.
 * It is not a shared Telegram sender authority; all privileged flags are false.
 */
export function createQuotaRelayHttpsServer({
  certificate,privateKey,publisherToken,viewerToken,sourceDeviceId,
  trustedNow=()=>Date.now()
}={}) {
  if(typeof certificate!=='string'||!certificate.includes('BEGIN CERTIFICATE')||
     typeof privateKey!=='string'||!privateKey.includes('PRIVATE KEY')||
     !TOKEN.test(publisherToken??'')||!TOKEN.test(viewerToken??'')||
     publisherToken===viewerToken||!DEVICE.test(sourceDeviceId??'')||
     typeof trustedNow!=='function')
    throw new TypeError('Invalid TLS certificate, tokens or source device enrollment');

  const publisherHash=digest(publisherToken);
  const viewerHash=digest(viewerToken);
  let slot=null; // {raw,view,accountIdSha256,quarantined}
  const path='/v1/quota/'+sourceDeviceId;
  return createServer({
    key:privateKey,cert:certificate,minVersion:'TLSv1.2',
    requestCert:false
  },async(req,res)=>{
    try{
      if(duplicateSecurityHeaders(req.rawHeaders) ||
         req.url!==path || !['GET','PUT'].includes(req.method)) {
        write(res,404,safeResponse('HOLD_UNKNOWN_ROUTE'));return;
      }
      if(req.method==='GET'){
        if(!authenticated(req.headers.authorization,viewerHash)){
          write(res,401,safeResponse('HOLD_UNAUTHENTICATED'));return;
        }
        if(req.headers['content-length']||req.headers['transfer-encoding']){
          write(res,400,safeResponse('HOLD_INVALID_REQUEST'));return;
        }
        if(slot?.quarantined){
          write(res,409,safeResponse('HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET'));return;
        }
        if(!slot){
          write(res,404,safeResponse('HOLD_NO_SOURCE_OBSERVATION'));return;
        }
        const reviewed=reviewOfflineQuotaViewCandidate(slot.raw,{
          trustedNowUtcMs:trustedNow()
        });
        if(reviewed.status!=='OFFLINE_VIEW_CANDIDATE_ONLY'){
          write(res,410,safeResponse(reviewed.status));return;
        }
        const age=trustedNow()-slot.view.collectedAtUtcMs;
        const status=age<120_000?'FRESH_READ_ONLY':'AGED_READ_ONLY';
        write(res,200,safeResponse(status,slot.view));return;
      }
      if(!authenticated(req.headers.authorization,publisherHash)){
        write(res,401,safeResponse('HOLD_UNAUTHENTICATED'));return;
      }
      if(req.headers['content-type']!=='application/json'||
         req.headers['content-length']===undefined||
         !/^\d+$/.test(req.headers['content-length']) ||
         Number(req.headers['content-length'])<1 ||
         Number(req.headers['content-length'])>MAX_BODY ||
         req.headers['transfer-encoding']){
        write(res,400,safeResponse('HOLD_INVALID_REQUEST'));return;
      }
      if(slot?.quarantined){
        write(res,409,safeResponse('HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET'));return;
      }
      const body=await receive(req);
      const candidate=reviewOfflineQuotaViewCandidate(body,{
        trustedNowUtcMs:trustedNow(),previousRaw:slot?.raw??null
      });
      if(candidate.status==='HOLD_ACCOUNT_IDENTITY_CHANGED'){
        // Never show the prior account as current after source account switches.
        slot={quarantined:true,accountIdSha256:slot.accountIdSha256};
        write(res,409,safeResponse('HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET'));
        return;
      }
      if(candidate.status!=='OFFLINE_VIEW_CANDIDATE_ONLY') {
        write(res,422,safeResponse(candidate.status));return;
      }
      if(candidate.view.sourceDeviceId!==sourceDeviceId) {
        write(res,422,safeResponse('HOLD_UNENROLLED_SOURCE'));return;
      }
      slot={raw:body,view:candidate.view,
        accountIdSha256:candidate.view.accountIdSha256,quarantined:false};
      write(res,202,safeResponse('READ_ONLY_SNAPSHOT_ACCEPTED'));
    }catch{
      if(!res.headersSent && !res.destroyed){
        write(res,500,safeResponse('HOLD_RELAY_ERROR'));
      }
    }
  });
}
