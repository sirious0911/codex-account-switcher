import {createHash,createPrivateKey,timingSafeEqual,X509Certificate} from 'node:crypto';
import {connect} from 'node:tls';

const hex=/^[a-f0-9]{64}$/;
const uuid=/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/;
const reserved=['AuthorityUrl','AuthorityCertificatePem','AuthorityCertSha256','AuthorityHmacKeyHex','EnableLiveDispatch'];
const fail=()=>{throw Error('HOLD_OWNER_AUTH_BINDING');};
export function bindOwnerAuthority({tls,owner,enrollments,recurringConsent,privateJob}={}) {
  if(!owner || owner.enabled!==true || !hex.test(owner.hmacKeyHex??'') ||
     !hex.test(owner.accountSha256??'') || !uuid.test(owner.profileId??'') ||
     !uuid.test(owner.threadId??'') || !/^[A-Za-z0-9-]{1,48}$/.test(owner.deviceId??'') ||
     !Array.isArray(enrollments) || enrollments.length<1 || enrollments.length>16 ||
     !privateJob || typeof privateJob!=='object' || Array.isArray(privateJob) ||
     Object.keys(privateJob).some(key=>reserved.some(field=>field.toLowerCase()===key.toLowerCase())))fail();
  const ids=new Set(),keys=new Set();
  let match, senderCount=0;
  for(const row of enrollments){
    if(!row || typeof row.deviceId!=='string' || !/^[A-Za-z0-9-]{1,48}$/.test(row.deviceId) ||
       !['sender','viewer'].includes(row.role) || (Object.hasOwn(row,'enabled') && row.enabled!==true) ||
       (Object.hasOwn(row,'revoked') && row.revoked!==false) || !hex.test(row.hmacKeyHex??'') ||
       ids.has(row.deviceId) || keys.has(row.hmacKeyHex))fail();
    ids.add(row.deviceId);keys.add(row.hmacKeyHex);
    if(row.role==='sender')senderCount++;
    if(row.deviceId===owner.deviceId)match=row;
  }
  if(senderCount!==1 || match?.role!=='sender' || !timingSafeEqual(Buffer.from(match.hmacKeyHex,'hex'),Buffer.from(owner.hmacKeyHex,'hex')) ||
     recurringConsent?.approvedByUser!==true ||
     ['profileId','accountSha256','threadId'].some(key=>recurringConsent[key]!==owner[key]))fail();
  let cert;
  try {
    cert=new X509Certificate(tls.certPem);
    const now=Date.now(), start=Date.parse(cert.validFrom), end=Date.parse(cert.validTo);
    if(!Number.isFinite(start) || !Number.isFinite(end) || now<start || now>=end ||
       !cert.checkIP('127.0.0.1') || !cert.checkPrivateKey(createPrivateKey(tls.keyPem)))fail();
  } catch {fail();}
  return Object.freeze({
    AuthorityCertificatePem:tls.certPem,
    AuthorityCertSha256:createHash('sha256').update(cert.raw).digest('hex'),
    AuthorityHmacKeyHex:owner.hmacKeyHex
  });
}
export function requireBoundDue(owner,event,switches){
  if(switches?.master!==true || (event?.lane==='CODEX_FIVE_HOUR'?switches.fiveHour!==true:
     event?.lane==='CODEX_SEVEN_DAY'?switches.sevenDay!==true:true) ||
     ['profileId','accountSha256','threadId'].some(key=>event?.[key]!==owner[key]) ||
     event?.ownerDevice!==owner.deviceId)fail();
}
export async function verifyListeningCertificate(port,binding){
  await new Promise((resolve,reject)=>{
    const socket=connect({host:'127.0.0.1',port,minVersion:'TLSv1.3',ca:binding.AuthorityCertificatePem,rejectUnauthorized:true});
    const done=(error)=>{socket.destroy();error?reject(Error('HOLD_OWNER_TLS_BOUNDARY')):resolve();};
    socket.setTimeout(3000,()=>done(true));
    socket.once('error',()=>done(true));
    socket.once('secureConnect',()=>{
      const raw=socket.getPeerCertificate().raw;
      done(!raw || createHash('sha256').update(raw).digest('hex')!==binding.AuthorityCertSha256);
    });
  });
}


export function bindPreparedOwner(config) {
  if(config?.schemaVersion!==1 || config.operatorPrepared!==true ||
     typeof config.appDataHome!=='string' || typeof config.databasePath!=='string' ||
     !/^[A-Za-z0-9_-]{8,65}$/.test(config.pipeName??'') ||
     !hex.test(config.pipeSecretHex??'') ||
     !['host','worker','verifier'].every(name=>{
       const entry=config.bundle?.[name];
       return typeof entry?.executablePath==='string' && entry.executablePath.length>0 &&
         hex.test(entry.exeSha256??'') && hex.test(entry.bundleSha256??'');
     }))fail();
  return bindOwnerAuthority(config);
}
