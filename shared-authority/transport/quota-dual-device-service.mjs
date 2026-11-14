// P3 opt-in Windows/Mac quota relay service entrypoint.
// Process environment must supply TWO independent enrollment sets and TLS.
// This does not start automatically, read Codex profiles or send Telegram.
import {readFileSync,lstatSync} from 'node:fs';
import {isAbsolute} from 'node:path';
import {createQuotaMultiDeviceRelayHttpsServer} from './quota-multi-device-relay-https.mjs';

const settings=process.env;
function readCertificateFile(filePath) {
  if(!filePath||!isAbsolute(filePath))throw new Error('A TLS file path must be absolute');
  const info=lstatSync(filePath);
  if(!info.isFile()||info.isSymbolicLink()||info.size>65536)
    throw new Error('TLS materials must be bounded regular files');
  return readFileSync(filePath,'utf8');
}

const host=settings.QUOTA_RELAY_BIND_HOST??'127.0.0.1';
if(!['127.0.0.1','::1'].includes(host) &&
   settings.QUOTA_RELAY_EXTERNAL_BIND_ACK!=='EXPLICIT_TLS_REMOTE_BIND')
  throw new Error('Remote network binding requires explicit acknowledgement');
const port=Number(settings.QUOTA_RELAY_PORT??'9443');
if(!Number.isInteger(port)||port<1024||port>65535)
  throw new Error('Invalid relay listening port');

const server=createQuotaMultiDeviceRelayHttpsServer({
  certificate:readCertificateFile(settings.QUOTA_RELAY_CERT_FILE),
  privateKey:readCertificateFile(settings.QUOTA_RELAY_KEY_FILE),
  enrollments:[
    {
      sourceDeviceId:settings.QUOTA_RELAY_DEVICE_A_ID,
      publisherToken:settings.QUOTA_RELAY_DEVICE_A_PUBLISH_TOKEN,
      viewerToken:settings.QUOTA_RELAY_DEVICE_A_VIEW_TOKEN
    },
    {
      sourceDeviceId:settings.QUOTA_RELAY_DEVICE_B_ID,
      publisherToken:settings.QUOTA_RELAY_DEVICE_B_PUBLISH_TOKEN,
      viewerToken:settings.QUOTA_RELAY_DEVICE_B_VIEW_TOKEN
    }
  ]
});
server.once('error',()=>{
  // Avoid disclosing listener TLS materials or enrollment credentials.
  process.exitCode=1;
});
server.listen(port,host,()=>{
  process.stdout.write('P3_DUAL_DEVICE_HTTPS_RELAY_LISTENING\n');
});
process.once('SIGINT',()=>server.close());
process.once('SIGTERM',()=>server.close());
