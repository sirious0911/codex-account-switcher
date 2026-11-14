// Start with explicit TLS materials and separate per-role bearer tokens.
// Default bind is LOCAL LOOPBACK ONLY. No Codex credentials or Telegram logic.
import {readFileSync,lstatSync} from 'node:fs';
import {createQuotaRelayHttpsServer} from './quota-relay-https.mjs';

const env=process.env;
function certificateFile(path){
  if(!path||!path.startsWith('/')&&!/^[A-Za-z]:[\\/]/.test(path))
    throw new Error('Provide an absolute TLS file path');
  const stat=lstatSync(path);
  if(!stat.isFile()||stat.isSymbolicLink()||stat.size>65536)
    throw new Error('TLS file must be a regular bounded file, not a link');
  return readFileSync(path,'utf8');
}
const host=env.QUOTA_RELAY_BIND_HOST??'127.0.0.1';
if(host!=='127.0.0.1'&&host!=='::1' &&
   env.QUOTA_RELAY_EXTERNAL_BIND_ACK!=='EXPLICIT_TLS_REMOTE_BIND')
  throw new Error('External TLS binding requires explicit acknowledgement');
const port=Number(env.QUOTA_RELAY_PORT??'9443');
if(!Number.isInteger(port)||port<1024||port>65535)
  throw new Error('Invalid relay TCP port');
const server=createQuotaRelayHttpsServer({
  certificate:certificateFile(env.QUOTA_RELAY_CERT_FILE),
  privateKey:certificateFile(env.QUOTA_RELAY_KEY_FILE),
  publisherToken:env.QUOTA_RELAY_PUBLISH_TOKEN,
  viewerToken:env.QUOTA_RELAY_VIEW_TOKEN,
  sourceDeviceId:env.QUOTA_RELAY_SOURCE_DEVICE_ID
});
server.requestTimeout=10000;
server.headersTimeout=10000;
server.keepAliveTimeout=2000;
server.listen(port,host,()=>{
  // Only bind information, never token/cert contents.
  process.stdout.write('QUOTA_RELAY_READ_ONLY_TLS_LISTENING '+host+':'+port+'\n');
});
process.once('SIGTERM',()=>server.close());
process.once('SIGINT',()=>server.close());
