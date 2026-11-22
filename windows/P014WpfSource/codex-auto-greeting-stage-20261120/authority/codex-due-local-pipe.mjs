// Windows user-local due observation ingress, NOT a send API.
// The in-memory HMAC key is provisioned out-of-band to the reviewed Windows
// UI and its single local owner. No user input, email, token or Codex turn
// reaches this transport. A verified event is NOT authority consent/lease.
import {createServer} from 'node:net';
import {createHmac,timingSafeEqual} from 'node:crypto';

const HASH=/^[a-f0-9]{64}$/;
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const NAME=/^[A-Za-z0-9_-]{8,65}$/;
const LANES=new Set(['CODEX_FIVE_HOUR','CODEX_SEVEN_DAY']);
const PREFIX='CODEX_DUE_LOCAL_PIPE_V1';
const FIELDS=['profileId','accountSha256','threadId','lane',
  'resetAtUnix','observedAtUnix'];
const exact=(value,keys)=>value && typeof value==='object' &&
  !Array.isArray(value) &&
  Object.keys(value).sort().join('|')===keys.slice().sort().join('|');

export function canonicalDueText(packet) {
  if(!exact(packet,['version','event','sentAtMs','nonce','mac']) ||
     packet.version!==1 || !exact(packet.event,FIELDS) ||
     !UUID.test(packet.event.profileId??'') ||
     !HASH.test(packet.event.accountSha256??'') ||
     !UUID.test(packet.event.threadId??'') ||
     !LANES.has(packet.event.lane) ||
     !Number.isSafeInteger(packet.event.resetAtUnix) ||
     !Number.isSafeInteger(packet.event.observedAtUnix) ||
     !Number.isSafeInteger(packet.sentAtMs) ||
     !/^[a-f0-9]{32}$/.test(packet.nonce??'') ||
     !HASH.test(packet.mac??''))
    throw Error('HOLD_INVALID_DUE_PIPE_FRAME');
  return [PREFIX,...FIELDS.map(x=>packet.event[x]),packet.sentAtMs,
    packet.nonce].join('\n');
}
const result=code=>JSON.stringify({code})+'\n';
const timeMs=()=>Date.now();

export function createCodexDueLocalPipe({
  pipeName,secretHex,profileId,accountSha256,threadId,
  masterEnabled=false,fiveHourEnabled=false,sevenDayEnabled=false,
  onVerifiedDue=null,authorizeDue=null,now=timeMs
}={}) {
  if(process.platform!=='win32' || !NAME.test(pipeName??'') ||
     !HASH.test(secretHex??'') || !UUID.test(profileId??'') ||
     !HASH.test(accountSha256??'') || !UUID.test(threadId??'') ||
     typeof now!=='function' ||
     (onVerifiedDue!==null && typeof onVerifiedDue!=='function') ||
     (authorizeDue!==null && typeof authorizeDue!=='function'))
    throw Error('HOLD_LOCAL_PIPE_NOT_PROVISIONED');
  const route='\\\\.\\pipe\\'+pipeName;
  const key=Buffer.from(secretHex,'hex');
  const seenNonce=new Map(),seenEvents=new Set();
  const accepted=(packet)=>{
    const e=packet.event;
    const nowMs=now();
    if(!Number.isSafeInteger(nowMs) ||
       Math.abs(nowMs-packet.sentAtMs)>30_000)
      return 'HOLD_DUE_PIPE_STALE';
    if(e.profileId!==profileId ||e.accountSha256!==accountSha256 ||
       e.threadId!==threadId)
      return 'HOLD_DUE_PIPE_WRONG_ACCOUNT_OR_THREAD';
    const observedMs=e.observedAtUnix*1000;
    const resetMs=e.resetAtUnix*1000;
    if(!Number.isSafeInteger(observedMs) ||
       resetMs>nowMs+30_000 || nowMs-resetMs>10*60_000 ||
       observedMs>nowMs+30_000 || nowMs-observedMs>6*60_000)
      return 'HOLD_DUE_PIPE_UNTRUSTED_TIME';
    if(!masterEnabled ||
       (e.lane==='CODEX_FIVE_HOUR'&&!fiveHourEnabled) ||
       (e.lane==='CODEX_SEVEN_DAY'&&!sevenDayEnabled) ||
       onVerifiedDue===null)
      return 'HOLD_LOCAL_SENDER_OFF';
    // Re-evaluate current user preference and operator activation for EVERY
    // authenticated due, not merely when the owner process was started.
    if(authorizeDue!==null){
      let allowed=false;
      try{allowed=authorizeDue(Object.freeze({...e}))===true;}
      catch{return 'HOLD_LOCAL_SENDER_OFF';}
      if(!allowed)return 'HOLD_LOCAL_SENDER_OFF';
    }
    if(seenNonce.has(packet.nonce))
      return 'HOLD_DUE_PIPE_REPLAY';
    // First full envelope, then event/owner identity are bound.
    const eventKey=[e.accountSha256,e.threadId,e.lane,e.resetAtUnix].join('|');
    if(seenEvents.has(eventKey))
      return 'HOLD_DUE_EVENT_ALREADY_ACCEPTED';
    // Reserve before any awaited callback. A crash or lost ACK must not
    // silently dispatch the same due cycle twice in this process.
    seenNonce.set(packet.nonce,nowMs);
    seenEvents.add(eventKey);
    // Avoid unbounded nonce growth. A 30-second freshness gate remains.
    if(seenNonce.size>2048){
      for(const [n,t] of seenNonce)
        if(t<nowMs-60_000)seenNonce.delete(n);
      if(seenNonce.size>2048)seenNonce.clear();
    }
    return 'ACCEPTED';
  };
  const server=createServer(socket=>{
    socket.setTimeout(4000);
    let raw='',completed=false;
    const reply=code=>{
      if(completed)return;
      completed=true;
      socket.end(result(code));
    };
    socket.on('timeout',()=>reply('HOLD_DUE_PIPE_TIMEOUT'));
    socket.on('error',()=>{});
    socket.on('data',async chunk=>{
      if(completed)return;
      raw+=chunk.toString('utf8');
      if(Buffer.byteLength(raw,'utf8')>1024){
        reply('HOLD_DUE_PIPE_FRAME_OVERSIZE');return;
      }
      const first=raw.indexOf('\n');
      if(first<0)return;
      if(first!==raw.length-1 || first===0){
        reply('HOLD_DUE_PIPE_AMBIGUOUS_FRAME');return;
      }
      // .NET StreamWriter.WriteLine emits CRLF on Windows. Permit CR only
      // immediately before the single terminal LF, never within JSON.
      let frame=raw.slice(0,first);
      if(frame.endsWith('\r'))frame=frame.slice(0,-1);
      if(frame.length===0 || frame.includes('\r')){
        reply('HOLD_DUE_PIPE_AMBIGUOUS_FRAME');return;
      }
      let packet;
      try{packet=JSON.parse(frame);}
      catch{reply('HOLD_DUE_PIPE_JSON');return;}
      let body;
      try{body=canonicalDueText(packet);}
      catch{reply('HOLD_INVALID_DUE_PIPE_FRAME');return;}
      const expected=createHmac('sha256',key).update(body).digest();
      const supplied=Buffer.from(packet.mac,'hex');
      if(!timingSafeEqual(expected,supplied)){
        reply('HOLD_DUE_PIPE_HMAC');return;
      }
      const gate=accepted(packet);
      if(gate!=='ACCEPTED'){reply(gate);return;}
      let status='HOLD_DUE_OWNER_UNCERTAIN_NO_RETRY';
      try{
        const answer=await onVerifiedDue(Object.freeze({...packet.event}));
        if(answer==='DUE_RECEIVED_OWNER_PENDING')
          status=answer;
      }catch{}
      reply(status);
    });
  });
  server.maxConnections=2;
  return Object.freeze({
    path:route,
    listen:()=>new Promise((resolve,reject)=>{
      server.once('error',reject);
      server.listen(route,()=>{server.removeListener('error',reject);resolve();});
    }),
    close:()=>new Promise((resolve,reject)=>{
      server.close(e=>e?reject(e):resolve());
    })
  });
}
