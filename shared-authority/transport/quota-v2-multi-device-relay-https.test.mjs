import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {request} from 'node:https';
import {createQuotaV2MultiDeviceRelayHttpsServer} from './quota-v2-multi-device-relay-https.mjs';

// Real TLS on 127.0.0.1:0, with ALL cert/key/credentials created in RAM.
// No files, Codex provider, operating services, external host or Telegram.
test('P3 V2 Windows/Mac R relay: authenticated two-device loopback HTTPS',async()=>{
  const issued=spawnSync('openssl',[
    'req','-x509','-newkey','rsa:2048','-nodes','-days','1',
    '-subj','/CN=localhost','-addext',
    'subjectAltName=DNS:localhost,IP:127.0.0.1',
    '-keyout','-','-out','-'
  ],{encoding:'utf8',timeout:15000,maxBuffer:262144});
  assert.equal(issued.status,0,'Synthetic in-memory TLS identity unavailable');
  const privateKey=issued.stdout.match(
    /-----BEGIN PRIVATE KEY-----[\s\S]*?-----END PRIVATE KEY-----/)?.[0];
  const certificate=issued.stdout.match(
    /-----BEGIN CERTIFICATE-----[\s\S]*?-----END CERTIFICATE-----/)?.[0];
  assert.ok(privateKey && certificate,'Expected in-memory TLS identity');
  const clock={now:1800000000000};
  const mac={sourceDeviceId:'macmini-a',
    publisherToken:'m'.repeat(48),viewerToken:'n'.repeat(48)};
  const windows={sourceDeviceId:'windows-b',
    publisherToken:'w'.repeat(48),viewerToken:'x'.repeat(48)};
  const config={certificate,privateKey,enrollments:[mac,windows],
    trustedNow:()=>clock.now};

  assert.throws(()=>createQuotaV2MultiDeviceRelayHttpsServer({
    ...config,enrollments:[mac,{...windows,viewerToken:mac.viewerToken}]
  }),/shared credential/);
  assert.throws(()=>createQuotaV2MultiDeviceRelayHttpsServer({
    ...config,enrollments:[mac,{...mac}]
  }),/Duplicate V2 source/);
  assert.throws(()=>createQuotaV2MultiDeviceRelayHttpsServer({
    ...config,enrollments:[mac,{...windows,publisherToken:windows.viewerToken}]
  }),/Invalid V2 source enrollment/);

  const relay=createQuotaV2MultiDeviceRelayHttpsServer(config);
  assert.throws(()=>relay.listen(4444,'127.0.0.1',()=>{}),/ephemeral/);
  assert.throws(()=>relay.listen(0,'0.0.0.0',()=>{}),/ephemeral/);
  assert.throws(()=>relay.listen(0,'::',()=>{}),/ephemeral/);
  await new Promise(resolve=>relay.listen(0,'127.0.0.1',resolve));
  try{
    const port=relay.address().port;
    const send=async(source,method,credential,frame=null,route=null,type='application/json')=>{
      const raw=frame===null?null:
        typeof frame==='string'?frame:JSON.stringify(frame);
      const headers={authorization:'Bearer '+credential};
      if(raw!==null){
        headers['content-type']=type;
        headers['content-length']=Buffer.byteLength(raw,'utf8');
      }
      return new Promise((resolve,reject)=>{
        const req=request({
          hostname:'127.0.0.1',servername:'localhost',port,
          path:route??'/v2/quota/'+source,method,headers,ca:certificate,
          rejectUnauthorized:true,timeout:5000
        },res=>{
          let body='';
          res.on('data',chunk=>body+=chunk);
          res.on('end',()=>{
            try{
              resolve({code:res.statusCode,body:JSON.parse(body),
                headers:res.headers,raw:body});
            }catch(e){reject(e);}
          });
        });
        req.on('error',reject);
        req.end(raw??undefined);
      });
    };
    const frame=(source,account,number,sequence=1,at=clock.now-1500,options={})=>({
      schemaVersion:2,sourceDeviceId:source,
      sourceSessionId:'a'.repeat(32),sequence,collectedAtUtcMs:at,
      accountIdSha256:account.repeat(64),
      fiveHour:options.rOnly?
        {remainingPercent:null,resetAtUtcMs:null}:
        {remainingPercent:85,resetAtUtcMs:at+3600000},
      sevenDay:options.rOnly?
        {remainingPercent:null,resetAtUtcMs:null}:
        {remainingPercent:62,resetAtUtcMs:at+172800000},
      resetCredits:{
        availableCount:number,
        expiryDetailsUtcMs:options.expiry===undefined?
          [at+200000000,at+300000000]:options.expiry
      },
      actualSendAuthorized:false,automaticRetryAllowed:false,
      productionReservationAuthorized:false,senderTransitionAuthorized:false
    });

    // Strict route, device and role separation before any state is accepted.
    assert.equal((await send(mac.sourceDeviceId,'GET',mac.viewerToken)).code,404);
    assert.equal((await send(windows.sourceDeviceId,'GET',windows.viewerToken)).code,404);
    assert.equal((await send('macmini-x','GET',mac.viewerToken)).code,404);
    assert.equal((await send(mac.sourceDeviceId,'GET',mac.viewerToken,null,
      '/v1/quota/macmini-a')).code,404);
    assert.equal((await send(mac.sourceDeviceId,'GET',mac.viewerToken,null,
      '/v2/quota/macmini-a?view=1')).code,404);
    assert.equal((await send(mac.sourceDeviceId,'GET',mac.publisherToken)).code,401);
    assert.equal((await send(mac.sourceDeviceId,'PUT',mac.viewerToken,
      frame(mac.sourceDeviceId,'b',2))).code,401);
    assert.equal((await send(mac.sourceDeviceId,'PUT',windows.publisherToken,
      frame(mac.sourceDeviceId,'b',2))).code,401);
    assert.equal((await send(windows.sourceDeviceId,'GET',mac.viewerToken)).code,401);

    const macR2=frame('macmini-a','b',2);
    const winR2=frame('windows-b','c',2,1,clock.now-1500,
      {rOnly:true,expiry:[null]});
    assert.equal((await send('macmini-a','PUT',mac.publisherToken,macR2)).code,202);
    assert.equal((await send('windows-b','PUT',windows.publisherToken,winR2)).code,202);

    const macView=await send('macmini-a','GET',mac.viewerToken);
    const winView=await send('windows-b','GET',windows.viewerToken);
    assert.equal(macView.code,200);
    assert.equal(winView.code,200);
    assert.equal(macView.body.schemaVersion,2);
    assert.equal(macView.body.status,'FRESH_READ_ONLY');
    assert.equal(macView.body.frame.resetCredits.availableCount,2);
    assert.deepEqual(macView.body.frame.resetCredits.expiryDetailsUtcMs,
      [macR2.resetCredits.expiryDetailsUtcMs[0],
       macR2.resetCredits.expiryDetailsUtcMs[1]]);
    assert.equal(macView.body.frame.fiveHour.remainingPercent,85);
    assert.equal(winView.body.frame.fiveHour.remainingPercent,null);
    assert.equal(winView.body.frame.sevenDay.remainingPercent,null);
    assert.equal(winView.body.frame.resetCredits.availableCount,2);
    assert.deepEqual(winView.body.frame.resetCredits.expiryDetailsUtcMs,[null]);
    assert.equal(macView.body.frame.sourceDeviceId,'macmini-a');
    assert.equal(winView.body.frame.sourceDeviceId,'windows-b');
    assert.match(macView.headers['cache-control'],/no-store/);
    assert.match(winView.headers['x-content-type-options'],/nosniff/);
    for(const view of [macView,winView])
      for(const name of ['actualSendAuthorized','automaticRetryAllowed',
        'productionReservationAuthorized','senderTransitionAuthorized'])
        assert.equal(view.body[name],false);
    assert.doesNotMatch(macView.raw,/publisherToken|viewerToken|creditId|Bearer/);

    // Existing snapshot cannot be consumed repeatedly.
    const repeated=await send('macmini-a','PUT',mac.publisherToken,macR2);
    assert.equal(repeated.code,422);
    assert.equal(repeated.body.status,'HOLD_REPLAY_OR_REORDERED_VIEW');
    const malformed={...macR2,sequence:2,collectedAtUtcMs:clock.now-500,
      extraField:'credit-private'};
    assert.equal((await send('macmini-a','PUT',mac.publisherToken,malformed)).code,422);
    const permissions={...macR2,sequence:2,collectedAtUtcMs:clock.now-500,
      senderTransitionAuthorized:true};
    assert.equal((await send('macmini-a','PUT',mac.publisherToken,permissions)).code,422);
    const badSource={...macR2,sourceDeviceId:'windows-b',sequence:2,
      collectedAtUtcMs:clock.now-500};
    assert.equal((await send('macmini-a','PUT',mac.publisherToken,badSource)).code,422);
    const tooLong=JSON.stringify(macR2)+' '.repeat(2050);
    assert.equal((await send('macmini-a','PUT',mac.publisherToken,tooLong)).code,400);
    const wrongType=await send('macmini-a','PUT',mac.publisherToken,macR2,
      null,'application/octet-stream');
    assert.equal(wrongType.code,400);

    // A verified new same-account frame clears previous expiry slots on R0.
    const macR0=frame('macmini-a','b',0,2,clock.now-500,{expiry:null});
    assert.equal((await send('macmini-a','PUT',mac.publisherToken,macR0)).code,202);
    const zero=await send('macmini-a','GET',mac.viewerToken);
    assert.equal(zero.code,200);
    assert.equal(zero.body.frame.resetCredits.availableCount,0);
    assert.equal(zero.body.frame.resetCredits.expiryDetailsUtcMs,null);
    assert.equal(zero.body.frame.sequence,2);

    // The source's provider-account change quarantines its slot only.
    const changed=frame('macmini-a','d',2,1,clock.now-200,
      {rOnly:true,expiry:null});
    changed.sourceSessionId='f'.repeat(32);
    const conflict=await send('macmini-a','PUT',mac.publisherToken,changed);
    assert.equal(conflict.code,409);
    assert.equal(conflict.body.status,'HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET');
    const oldHidden=await send('macmini-a','GET',mac.viewerToken);
    assert.equal(oldHidden.code,409);
    assert.equal(oldHidden.body.frame,null);
    assert.equal((await send('windows-b','GET',windows.viewerToken)).code,200);

    // Time passes without extending the viewer's six-minute freshness.
    clock.now+=120000;
    const aged=await send('windows-b','GET',windows.viewerToken);
    assert.equal(aged.code,200);
    assert.equal(aged.body.status,'AGED_READ_ONLY');
    clock.now+=240000;
    const stale=await send('windows-b','GET',windows.viewerToken);
    assert.equal(stale.code,410);
    assert.equal(stale.body.frame,null);
    assert.equal(stale.body.status,'HOLD_STALE_OR_FUTURE_V2_FRAME');

    console.log('P3_V2_LOOPBACK_TLS_TWO_DEVICE_RELAY_PASS=1');
    console.log('P3_V2_R2_TO_R0_AND_R_ONLY_PASS=1');
    console.log('P3_V2_ROLE_REPLAY_IDENTITY_AND_STALE_DENIAL_PASS=1');
    console.log('REAL_CODEX_READS=0 EXTERNAL_HTTPS=0 TELEGRAM_SENDS=0');
  }finally{
    await new Promise(resolve=>relay.close(resolve));
  }
});
