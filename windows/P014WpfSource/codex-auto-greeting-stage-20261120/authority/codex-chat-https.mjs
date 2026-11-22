// Codex-chat-only HTTPS transport, completely isolated from Telegram.
// Importing this module never opens a socket or touches a database.
// Caller must instantiate an explicit preprovisioned authority and invoke listen.
import {createServer} from 'node:https';
import {CodexChatSenderAuthority} from './codex-chat-authority.mjs';

const SAFE=Object.freeze({schemaVersion:1,actualSendAuthorized:false,
  automaticRetryAllowed:false,remoteSenderActivated:false});
const failure=(code)=>({...SAFE,code});
const BODY_LIMIT=1024;
const ID_HEADERS=['x-device-id','x-timestamp-ms','x-nonce','x-signature'];
const CRITICAL=new Set([...ID_HEADERS,'content-length','transfer-encoding',
  'content-type','host']);
const LOOPBACK=new Set(['127.0.0.1','::1','localhost']);

export function createCodexChatAuthorityHttpsServer({
  authority,tlsKey,tlsCert,listenHost='127.0.0.1',allowRemote=false
}={}) {
  if(!(authority instanceof CodexChatSenderAuthority))
    throw new TypeError('Explicit isolated Codex-chat authority required');
  if(!Buffer.isBuffer(tlsKey)||!Buffer.isBuffer(tlsCert) ||
     tlsKey.length<100||tlsCert.length<100)
    throw new TypeError('Explicit TLS key and certificate required');
  if(typeof listenHost!=='string' ||
     !/^(?:127\.0\.0\.1|::1|localhost|(?:[0-9]{1,3}\.){3}[0-9]{1,3})$/.test(listenHost) ||
     (!LOOPBACK.has(listenHost) && allowRemote!==true))
    throw new Error('External binding requires explicit remote approval');

  const server=createServer({
    key:tlsKey,cert:tlsCert,minVersion:'TLSv1.3',
    requestCert:false,handshakeTimeout:10000
  },async(req,res)=>{
    const send=(result,status=200)=>{
      if(res.destroyed||res.writableEnded)return;
      const bytes=Buffer.from(JSON.stringify(result),'utf8');
      res.writeHead(status,{
        'content-type':'application/json; charset=utf-8',
        'cache-control':'no-store','x-content-type-options':'nosniff',
        'content-length':String(bytes.length)
      });
      res.end(bytes);
    };
    try{
      if(!['GET','POST'].includes(req.method) ||
         !/^\/v1\/codex-chat\/[A-Za-z0-9/_-]{1,146}$/.test(req.url??'')){
        send(failure('HOLD_ROUTE'),404);return;
      }
      const counts=new Map();
      for(let i=0;i<req.rawHeaders.length;i+=2){
        const key=req.rawHeaders[i].toLowerCase();
        if(!CRITICAL.has(key))continue;
        counts.set(key,(counts.get(key)??0)+1);
        if(counts.get(key)>1){send(failure('HOLD_AMBIGUOUS_HEADER'),400);return;}
      }
      if(counts.has('content-length')&&counts.has('transfer-encoding')){
        send(failure('HOLD_AMBIGUOUS_FRAMING'),400);return;
      }
      if(req.method==='POST' && req.headers['content-type']!=='application/json'){
        send(failure('HOLD_CONTENT_TYPE'),415);return;
      }
      if(req.method==='GET' &&
         (counts.has('transfer-encoding') ||
           (counts.has('content-length') && req.headers['content-length']!=='0'))){
        send(failure('HOLD_GET_BODY'),400);return;
      }
      for(const k of ID_HEADERS)
        if(counts.get(k)!==1 || typeof req.headers[k]!=='string'){
          send(failure('HOLD_IDENTITY_HEADER'),401);return;
        }
      const parts=[];let total=0;
      for await(const part of req){
        total+=part.length;
        if(total>BODY_LIMIT){send(failure('HOLD_BODY_TOO_LARGE'),413);return;}
        parts.push(part);
      }
      const raw=Buffer.concat(parts);
      const bodyUtf8=raw.toString('utf8');
      if(!Buffer.from(bodyUtf8,'utf8').equals(raw)){
        send(failure('HOLD_UTF8'),400);return;
      }
      const headers=Object.fromEntries(ID_HEADERS.map(k=>[k,req.headers[k]]));
      const result=authority.dispatch({
        method:req.method,path:req.url,bodyUtf8,headers
      });
      send(result);
    }catch{
      send(failure('HOLD_INTERNAL_ERROR'),503);
    }
  });
  server.headersTimeout=15_000;
  server.requestTimeout=15_000;
  server.timeout=20_000;
  return server;
}
