// An independent P3 HTTPS server for multiple enrolled telemetry sources.
// Each route delegates to its own existing, fail-closed single-source relay.
// This module does not change the single-source server or authorize Telegram.
import {createServer} from 'node:https';
import {createHash} from 'node:crypto';
import {createQuotaRelayHttpsServer} from './quota-relay-https.mjs';

const SOURCE_ID=/^[A-Za-z0-9-]{1,48}$/;
const ACCESS=/^[A-Za-z0-9_-]{32,256}$/;
const DENIED=JSON.stringify({
  schemaVersion:1,status:'HOLD_UNKNOWN_ROUTE',frame:null,
  actualSendAuthorized:false,automaticRetryAllowed:false,
  productionReservationAuthorized:false,senderTransitionAuthorized:false
});

/**
 * A single TLS endpoint can serve up to eight independent Windows/Mac
 * quota sources. Tokens are never shared between publishers, viewers,
 * or devices. There is deliberately no remote account-switch/reset API.
 *
 * Enrollment: {sourceDeviceId, publisherToken, viewerToken}.
 */
export function createQuotaMultiDeviceRelayHttpsServer({
  certificate,privateKey,enrollments,trustedNow=()=>Date.now()
}={}) {
  if(typeof certificate!=='string'||!certificate.includes('BEGIN CERTIFICATE')||
     typeof privateKey!=='string'||!privateKey.includes('PRIVATE KEY')||
     typeof trustedNow!=='function'||
     !Array.isArray(enrollments)||enrollments.length<2||enrollments.length>8)
    throw new TypeError('Invalid TLS configuration or source enrollment count');

  const byRoute=new Map();
  const credentials=new Set();
  for(const enrollment of enrollments) {
    if(!enrollment||typeof enrollment!=='object'||
       !SOURCE_ID.test(enrollment.sourceDeviceId??'')||
       !ACCESS.test(enrollment.publisherToken??'')||
       !ACCESS.test(enrollment.viewerToken??'')||
       enrollment.publisherToken===enrollment.viewerToken)
      throw new TypeError('Invalid source enrollment');
    const route='/v1/quota/'+enrollment.sourceDeviceId;
    const a=createHash('sha256').update(enrollment.publisherToken).digest('hex');
    const b=createHash('sha256').update(enrollment.viewerToken).digest('hex');
    if(byRoute.has(route)||credentials.has(a)||credentials.has(b))
      throw new TypeError('Duplicate source or shared credential');
    credentials.add(a);credentials.add(b);
    // The original relay holds its own mutable slot, quarantine, TTL and
    // account-identity gate. It is never opened as a listening socket.
    const isolated=createQuotaRelayHttpsServer({
      certificate,privateKey,sourceDeviceId:enrollment.sourceDeviceId,
      publisherToken:enrollment.publisherToken,viewerToken:enrollment.viewerToken,
      trustedNow
    });
    byRoute.set(route,isolated);
  }

  const server=createServer({
    key:privateKey,cert:certificate,minVersion:'TLSv1.2',
    requestCert:false
  },(req,res)=>{
    // Exact URL lookup also rejects suffixes, unknown paths and queries.
    const isolated=byRoute.get(req.url);
    if(!isolated) {
      res.writeHead(404,{
        'content-type':'application/json; charset=utf-8',
        'cache-control':'no-store, max-age=0',
        'x-content-type-options':'nosniff',
        'content-length':Buffer.byteLength(DENIED)
      });
      res.end(DENIED);
      return;
    }
    isolated.emit('request',req,res);
  });
  server.requestTimeout=10_000;
  server.headersTimeout=10_000;
  server.keepAliveTimeout=2_000;
  return server;
}
