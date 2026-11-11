// P3 V2 R/5H/7D telemetry relay: separately versioned, in-memory,
// read-only, and restricted to an ephemeral 127.0.0.1 test listener.
// Never modifies the V1 relay, starts a production service, consumes reset
// credits, reads Codex credentials, or authorizes Telegram or account changes.
import {createServer} from 'node:https';
import {createHash,timingSafeEqual} from 'node:crypto';
import {reviewOfflineQuotaViewCandidateV2} from '../offline/quota-view-candidate-v2.mjs';

const DEVICE=/^[A-Za-z0-9-]{1,48}$/;
const ACCESS=/^[A-Za-z0-9_-]{32,256}$/;
const MAX_BODY=2048;
const SAFE_HEADERS=Object.freeze({
  'content-type':'application/json; charset=utf-8',
  'cache-control':'no-store, max-age=0',
  'pragma':'no-cache',
  'x-content-type-options':'nosniff',
  'referrer-policy':'no-referrer'
});
const PRIVILEGES=Object.freeze({
  actualSendAuthorized:false,automaticRetryAllowed:false,
  productionReservationAuthorized:false,senderTransitionAuthorized:false
});
const response=(status,frame=null)=>({
  schemaVersion:2,status,frame,...PRIVILEGES
});
const digest=value=>createHash('sha256').update(value,'utf8').digest();
const auth=(raw,expected)=>{
  if(typeof raw!=='string'||!raw.startsWith('Bearer '))return false;
  const value=raw.slice(7);
  return ACCESS.test(value)&&timingSafeEqual(digest(value),expected);
};
const dupHeader=raw=>{
  const seen=new Set();
  for(let i=0;i<raw.length;i+=2){
    const name=raw[i].toLowerCase();
    if(['authorization','host','content-type','content-length',
        'transfer-encoding'].includes(name)){
      if(seen.has(name))return true;
      seen.add(name);
    }
  }
  return false;
};
const send=(res,code,status,frame=null)=>{
  const json=JSON.stringify(response(status,frame));
  res.writeHead(code,{...SAFE_HEADERS,
    'content-length':Buffer.byteLength(json,'utf8')});
  res.end(json);
};
const receive=req=>new Promise((resolve,reject)=>{
  const chunks=[];
  let count=0;
  req.on('data',chunk=>{
    count+=chunk.length;
    if(count>MAX_BODY){
      reject(new Error('OVERSIZED'));
      req.resume();
    }else chunks.push(chunk);
  });
  req.on('end',()=>{
    try {
      // Invalid UTF8 is not silently transformed into JSON.
      resolve(new TextDecoder('utf-8',{fatal:true})
        .decode(Buffer.concat(chunks)));
    }catch(error){reject(error);}
  });
  req.on('error',reject);
});

/**
 * Creates a completely isolated, opt-in V2 test relay.
 * Enrollment uses separate per-device publisher/viewer credentials.
 * All frames and source identity state live only in memory.
 *
 * The returned server may ONLY listen on 127.0.0.1, port 0 (ephemeral),
 * with an explicit callback. It cannot be used as a public endpoint.
 * Real device operation requires a separate formal release/hosting gate.
 */
export function createQuotaV2MultiDeviceRelayHttpsServer({
  certificate,privateKey,enrollments,trustedNow=()=>Date.now()
}={}) {
  if(typeof certificate!=='string'||!certificate.includes('BEGIN CERTIFICATE')||
     typeof privateKey!=='string'||!privateKey.includes('PRIVATE KEY')||
     typeof trustedNow!=='function'||
     !Array.isArray(enrollments)||enrollments.length<2||
     enrollments.length>8)
    throw new TypeError('Invalid offline TLS or source enrollment configuration');

  const byRoute=new Map();
  const used=new Set();
  for(const row of enrollments){
    if(!row||typeof row!=='object'||
       !DEVICE.test(row.sourceDeviceId??'')||
       !ACCESS.test(row.publisherToken??'')||
       !ACCESS.test(row.viewerToken??'')||
       row.publisherToken===row.viewerToken)
      throw new TypeError('Invalid V2 source enrollment');
    const pubHash=digest(row.publisherToken);
    const viewHash=digest(row.viewerToken);
    const path='/v2/quota/'+row.sourceDeviceId;
    const pubKey=pubHash.toString('hex'),viewKey=viewHash.toString('hex');
    if(byRoute.has(path)||used.has(pubKey)||used.has(viewKey))
      throw new TypeError('Duplicate V2 source or shared credential');
    used.add(pubKey);used.add(viewKey);
    byRoute.set(path,{
      sourceDeviceId:row.sourceDeviceId,
      publisherHash:pubHash,viewerHash:viewHash,
      last:null,quarantined:false
    });
  }

  const server=createServer({key:privateKey,cert:certificate,
    minVersion:'TLSv1.2',requestCert:false},async(req,res)=>{
    try {
      if(dupHeader(req.rawHeaders)||!['GET','PUT'].includes(req.method)){
        send(res,404,'HOLD_UNKNOWN_ROUTE');return;
      }
      const slot=byRoute.get(req.url);
      if(!slot){send(res,404,'HOLD_UNKNOWN_ROUTE');return;}

      if(req.method==='GET'){
        if(!auth(req.headers.authorization,slot.viewerHash)){
          send(res,401,'HOLD_UNAUTHENTICATED');return;
        }
        if(req.headers['content-length']!==undefined||
           req.headers['transfer-encoding']!==undefined){
          send(res,400,'HOLD_INVALID_REQUEST');return;
        }
        if(slot.quarantined){
          send(res,409,'HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET');return;
        }
        if(!slot.last){send(res,404,'HOLD_NO_SOURCE_OBSERVATION');return;}
        const result=reviewOfflineQuotaViewCandidateV2(slot.last.raw,{
          trustedNowUtcMs:trustedNow(),
          expectedSourceDeviceId:slot.sourceDeviceId
        });
        if(result.status!=='OFFLINE_V2_CANDIDATE_ONLY'){
          send(res,410,result.status);return;
        }
        const age=trustedNow()-result.view.collectedAtUtcMs;
        send(res,200,age<120_000?'FRESH_READ_ONLY':'AGED_READ_ONLY',
          result.view);
        return;
      }

      if(!auth(req.headers.authorization,slot.publisherHash)){
        send(res,401,'HOLD_UNAUTHENTICATED');return;
      }
      if(slot.quarantined){
        send(res,409,'HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET');return;
      }
      const contentLength=req.headers['content-length'];
      if(req.headers['content-type']!=='application/json'||
         typeof contentLength!=='string'||
         !/^\d+$/.test(contentLength)||
         Number(contentLength)<1||Number(contentLength)>MAX_BODY||
         req.headers['transfer-encoding']!==undefined){
        send(res,400,'HOLD_INVALID_REQUEST');return;
      }
      const raw=await receive(req);
      if(Buffer.byteLength(raw,'utf8')!==Number(contentLength)){
        send(res,400,'HOLD_INVALID_REQUEST');return;
      }
      const result=reviewOfflineQuotaViewCandidateV2(raw,{
        trustedNowUtcMs:trustedNow(),
        expectedSourceDeviceId:slot.sourceDeviceId,
        previousRaw:slot.last?.raw??null
      });
      if(result.status==='HOLD_ACCOUNT_IDENTITY_CHANGED'){
        // The old account's R count/dates can never be shown as the new one.
        slot.last=null;
        slot.quarantined=true;
        send(res,409,'HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET');return;
      }
      if(result.status!=='OFFLINE_V2_CANDIDATE_ONLY'){
        send(res,422,result.status);return;
      }
      slot.last={raw};
      send(res,202,'READ_ONLY_V2_SNAPSHOT_ACCEPTED');
    }catch{
      if(!res.headersSent&&!res.destroyed)
        send(res,400,'HOLD_INVALID_REQUEST');
    }
  });
  server.requestTimeout=10_000;
  server.headersTimeout=10_000;
  server.keepAliveTimeout=2_000;

  const listen=server.listen.bind(server);
  server.listen=(port,host,callback)=>{
    if(port!==0||host!=='127.0.0.1'||typeof callback!=='function')
      throw new TypeError('V2 test listener must use ephemeral 127.0.0.1 only');
    return listen(port,host,callback);
  };
  return server;
}
