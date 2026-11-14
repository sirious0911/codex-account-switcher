// Explicitly started HTTPS entrypoint for Mac-mini authority host.
// No default port, background worker, Telegram sender, credential
// discovery, device enrollment or DB initialization on import.
import {createServer} from 'node:https';
import {readFileSync,lstatSync} from 'node:fs';
import {resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {SenderAuthority} from './sender-authority.mjs';

const SAFE={schemaVersion:1,actualSendAuthorized:false,
  automaticRetryAllowed:false,remoteSenderActivated:false};
const reply=(code)=>({...SAFE,code});
const MAX_BODY_BYTES=1024;
function validHost(host){
  return typeof host==='string' && /^(?:127\.0\.0\.1|::1|localhost|(?:[0-9]{1,3}\.){3}[0-9]{1,3})$/.test(host);
}

export function createSenderAuthorityHttpsServer({
  authority, tlsKey, tlsCert, listenHost='127.0.0.1', allowRemote=false
}) {
  if(!(authority instanceof SenderAuthority))
    throw new TypeError('Explicit SenderAuthority required');
  if(!Buffer.isBuffer(tlsKey)||!Buffer.isBuffer(tlsCert)||
     tlsKey.length<100||tlsCert.length<100)
    throw new TypeError('Explicit TLS private key and certificate required');
  if(!validHost(listenHost) ||
     (!['127.0.0.1','::1','localhost'].includes(listenHost)&&allowRemote!==true))
    throw new Error('Non-loopback binding requires explicit operator approval');

  const server=createServer({key:tlsKey,cert:tlsCert,minVersion:'TLSv1.3',
    requestCert:false,handshakeTimeout:10000},async(req,res)=>{
    const send=(value,status=200)=>{
      if(res.writableEnded||res.destroyed)return;
      const bytes=Buffer.from(JSON.stringify(value),'utf8');
      res.writeHead(status,{'content-type':'application/json; charset=utf-8',
        'cache-control':'no-store','x-content-type-options':'nosniff',
        'content-length':String(bytes.length)});
      res.end(bytes);
    };
    try {
      if(!['GET','POST'].includes(req.method) ||
         !/^\/v1\/[A-Za-z0-9/_-]{1,154}$/.test(req.url??'')) {
        send(reply('HOLD_ROUTE'),404);return;
      }
      // rawHeaders must be checked before Node's normalized headers lose
      // duplicate signed identity values or conflicting framing values.
      const critical=new Set(['x-device-id','x-timestamp-ms',
        'x-nonce','x-signature','content-length','transfer-encoding']);
      const seen=new Set();
      for(let i=0;i<req.rawHeaders.length;i+=2){
        const name=req.rawHeaders[i].toLowerCase();
        if(critical.has(name)){
          if(seen.has(name)){send(reply('HOLD_AMBIGUOUS_HEADER'),400);return;}
          seen.add(name);
        }
      }
      if(seen.has('content-length')&&seen.has('transfer-encoding')){
        send(reply('HOLD_AMBIGUOUS_FRAMING'),400);return;
      }
      if(req.method==='POST' && req.headers['content-type']!=='application/json'){
        send(reply('HOLD_CONTENT_TYPE'),415);return;
      }
      if(req.method==='GET' && (seen.has('transfer-encoding') ||
          (seen.has('content-length')&&req.headers['content-length']!=='0'))){
        send(reply('HOLD_GET_BODY'),400);return;
      }
      let total=0;const chunks=[];
      for await(const chunk of req){
        total+=chunk.length;
        if(total>MAX_BODY_BYTES){
          send(reply('HOLD_BODY_TOO_LARGE'),413);return;
        }
        chunks.push(chunk);
      }
      const bodyUtf8=Buffer.concat(chunks).toString('utf8');
      if(!Buffer.from(bodyUtf8,'utf8').equals(Buffer.concat(chunks))){
        send(reply('HOLD_UTF8'),400);return;
      }
      const headers=Object.fromEntries(['x-device-id',
        'x-timestamp-ms','x-nonce','x-signature'].map(key=>
        [key,req.headers[key]]));
      send(authority.dispatch({method:req.method,path:req.url,
        headers,bodyUtf8}),200);
    }catch{
      send(reply('HOLD_INTERNAL_ERROR'),503);
    }
  });
  server.headersTimeout=15000;
  server.requestTimeout=15000;
  server.timeout=20000;
  return server; // Caller MUST explicitly invoke listen().
}

export function loadSenderAuthorityServiceConfig(configPath){
  if(typeof configPath!=='string'||configPath.length===0)
    throw new TypeError('Explicit private configuration file required');
  const configFile=resolve(configPath);
  const stat=lstatSync(configFile);
  if(!stat.isFile() || stat.isSymbolicLink() ||
     (process.platform!=='win32' && (stat.mode&0o077)!==0))
    throw new Error('Authority private configuration must be a private regular file');
  const input=JSON.parse(readFileSync(configFile,'utf8'));
  if(input===null||typeof input!=='object'||Array.isArray(input)||
     !Number.isInteger(input.port)||input.port<1||input.port>65535 ||
     typeof input.databasePath!=='string'||
     typeof input.tlsKeyPath!=='string'||
     typeof input.tlsCertPath!=='string'||
     !validHost(input.listenHost)||
     typeof input.allowRemote!=='boolean'||
     !Array.isArray(input.enrollments))
    throw new Error('Invalid explicit authority configuration');
  if(!input.allowRemote &&
     !['127.0.0.1','::1','localhost'].includes(input.listenHost))
    throw new Error('Remote listener not authorized by configuration');
  const keyFile=resolve(input.tlsKeyPath);
  const keyStat=lstatSync(keyFile);
  if(!keyStat.isFile() || keyStat.isSymbolicLink() ||
     (process.platform!=='win32' && (keyStat.mode&0o077)!==0))
    throw new Error('TLS key must be a private regular file');
  return {
    databasePath:input.databasePath,
    enrollments:input.enrollments,
    listenHost:input.listenHost,
    allowRemote:input.allowRemote,
    port:input.port,
    tlsKey:readFileSync(keyFile),
    tlsCert:readFileSync(resolve(input.tlsCertPath))
  };
}

async function runExplicitCli(){
  if(process.argv.length!==4||process.argv[2]!=='--serve'){
    throw new Error('Use --serve <private-config.json> explicitly; no automatic startup');
  }
  const options=loadSenderAuthorityServiceConfig(process.argv[3]);
  const authority=new SenderAuthority(options);
  let server;
  try{
    server=createSenderAuthorityHttpsServer({authority,...options});
    await new Promise((ok,fail)=>{
      server.once('error',fail);
      server.listen(options.port,options.listenHost,ok);
    });
    // Do not log enrollment secrets or config contents.
    console.log('Authority HTTPS listening, Telegram sender NOT included');
    const shutdown=()=>server.close(()=>{authority.close();});
    process.once('SIGTERM',shutdown);
    process.once('SIGINT',shutdown);
  }catch(error){
    server?.close();authority.close();throw error;
  }
}

// An imported module never opens a socket or modifies a database.
if(process.argv[1]&&resolve(process.argv[1])===fileURLToPath(import.meta.url)){
  runExplicitCli().catch(error=>{
    console.error('Authority startup HOLD: '+error.constructor.name);
    process.exitCode=1;
  });
}
