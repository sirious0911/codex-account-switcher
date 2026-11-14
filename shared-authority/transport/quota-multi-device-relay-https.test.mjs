import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {mkdtempSync,readFileSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {request} from 'node:https';
import {createQuotaMultiDeviceRelayHttpsServer} from './quota-multi-device-relay-https.mjs';

test('real HTTPS: two enrolled devices remain isolated across auth, replay and quarantine',async()=>{
  const root=mkdtempSync(join(tmpdir(),'p3-dual-sources-'));
  let server;
  try{
    const certFile=join(root,'cert.pem'),keyFile=join(root,'key.pem');
    const generated=spawnSync('openssl',[
      'req','-x509','-newkey','rsa:2048','-nodes','-days','1',
      '-subj','/CN=localhost',
      '-addext','subjectAltName=DNS:localhost,IP:127.0.0.1',
      '-keyout',keyFile,'-out',certFile
    ],{stdio:'ignore'});
    assert.equal(generated.status,0,'ephemeral TLS certificate unavailable');
    const certificate=readFileSync(certFile,'utf8');
    const privateKey=readFileSync(keyFile,'utf8');
    const mac={sourceDeviceId:'macmini-a',
      publisherToken:'m'.repeat(48),viewerToken:'n'.repeat(48)};
    const windows={sourceDeviceId:'windows-b',
      publisherToken:'w'.repeat(48),viewerToken:'x'.repeat(48)};
    const clock={now:Date.now()};
    const setup={certificate,privateKey,enrollments:[mac,windows],
      trustedNow:()=>clock.now};

    assert.throws(()=>createQuotaMultiDeviceRelayHttpsServer({
      ...setup,enrollments:[mac,{...windows,viewerToken:mac.viewerToken}]
    }),/shared credential/);
    assert.throws(()=>createQuotaMultiDeviceRelayHttpsServer({
      ...setup,enrollments:[mac,{...mac}]
    }),/Duplicate source/);
    server=createQuotaMultiDeviceRelayHttpsServer(setup);
    await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
    const port=server.address().port;

    async function exchange(device,method,credential,payload=null) {
      const raw=payload===null?null:JSON.stringify(payload);
      const headers={authorization:'Bearer '+credential};
      if(raw!==null){
        headers['content-type']='application/json';
        headers['content-length']=Buffer.byteLength(raw);
      }
      return new Promise((resolve,reject)=>{
        const req=request({
          hostname:'127.0.0.1',servername:'localhost',
          port,path:'/v1/quota/'+device,method,headers,
          ca:certificate,rejectUnauthorized:true,timeout:5000
        },res=>{
          let response='';
          res.on('data',chunk=>response+=chunk);
          res.on('end',()=>resolve({
            code:res.statusCode,body:JSON.parse(response),headers:res.headers
          }));
        });
        req.on('error',reject);
        req.end(raw??undefined);
      });
    }
    const frame=(device,account,remaining)=>({
      schemaVersion:1,sourceDeviceId:device,
      sourceSessionId:'a'.repeat(32),sequence:1,
      collectedAtUtcMs:clock.now-1500,accountIdSha256:account.repeat(64),
      fiveHour:{remainingPercent:remaining,resetAtUtcMs:clock.now+3_600_000},
      sevenDay:{remainingPercent:remaining-4,resetAtUtcMs:clock.now+172_800_000},
      actualSendAuthorized:false,automaticRetryAllowed:false,
      productionReservationAuthorized:false,senderTransitionAuthorized:false
    });

    assert.equal((await exchange('macmini-a','GET',mac.viewerToken)).code,404);
    assert.equal((await exchange('windows-b','GET',windows.viewerToken)).code,404);
    assert.equal((await exchange('macmini-a','PUT',windows.publisherToken,
      frame('macmini-a','b',76))).code,401);
    assert.equal((await exchange('windows-b','GET',mac.viewerToken)).code,401);
    assert.equal((await exchange('macmini-b','GET',mac.viewerToken)).code,404);

    const macFrame=frame('macmini-a','b',76);
    const winFrame=frame('windows-b','c',39);
    assert.equal((await exchange('macmini-a','PUT',mac.publisherToken,macFrame)).code,202);
    assert.equal((await exchange('windows-b','PUT',windows.publisherToken,winFrame)).code,202);

    const macView=await exchange('macmini-a','GET',mac.viewerToken);
    const winView=await exchange('windows-b','GET',windows.viewerToken);
    assert.equal(macView.code,200);
    assert.equal(winView.code,200);
    assert.equal(macView.body.frame.fiveHour.remainingPercent,76);
    assert.equal(winView.body.frame.fiveHour.remainingPercent,39);
    assert.equal(macView.body.frame.sourceDeviceId,'macmini-a');
    assert.equal(winView.body.frame.sourceDeviceId,'windows-b');
    assert.match(macView.headers['cache-control'],/no-store/);
    assert.equal(macView.body.actualSendAuthorized,false);
    assert.equal(winView.body.senderTransitionAuthorized,false);

    assert.equal((await exchange('macmini-a','PUT',mac.publisherToken,macFrame)).code,422);
    assert.equal((await exchange('windows-b','GET',windows.viewerToken)).code,200);
    const changed={...macFrame,sourceSessionId:'d'.repeat(32),sequence:1,
      collectedAtUtcMs:clock.now-1000,accountIdSha256:'e'.repeat(64)};
    assert.equal((await exchange('macmini-a','PUT',mac.publisherToken,changed)).code,409);
    assert.equal((await exchange('macmini-a','GET',mac.viewerToken)).code,409);
    assert.equal((await exchange('windows-b','GET',windows.viewerToken)).code,200);
    assert.equal((await exchange('windows-b','GET',mac.viewerToken)).code,401);

    clock.now+=610_000;
    const stale=await exchange('windows-b','GET',windows.viewerToken);
    assert.equal(stale.code,410);
    assert.equal(stale.body.frame,null);

    console.log('P3_2_DEVICE_REAL_HTTPS_TLS_ISOLATION_PASS=1');
    console.log('P3_CROSS_SOURCE_AUTH_REPLAY_QUARANTINE_PASS=1');
    console.log('REAL_CODEX_READS=0 TELEGRAM_SENDS=0');
  }finally{
    if(server?.listening)await new Promise(resolve=>server.close(resolve));
    rmSync(root,{recursive:true,force:true});
  }
});
