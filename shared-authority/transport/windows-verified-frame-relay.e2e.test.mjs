import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {mkdtempSync,readFileSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {request} from 'node:https';
import {createQuotaMultiDeviceRelayHttpsServer} from './quota-multi-device-relay-https.mjs';

test('Windows .NET verified active frame reaches Node dual-device HTTPS relay',async()=>{
  const produced=spawnSync('dotnet',[
    'run','--project','windows/CodexAccountSwitcher.P3FrameInterop',
    '-c','Release','--no-restore','--nologo'
  ],{encoding:'utf8',timeout:60_000,maxBuffer:128_000});
  assert.equal(produced.status,0,'Windows .NET synthetic producer must succeed');
  const raw=produced.stdout.trim();
  const frame=JSON.parse(raw);
  assert.equal(frame.sourceDeviceId,'windows-b');
  assert.equal(frame.fiveHour.remainingPercent,85);
  assert.equal(frame.sevenDay.remainingPercent,62);
  assert.equal(frame.actualSendAuthorized,false);
  assert.equal(frame.senderTransitionAuthorized,false);
  assert.equal(raw.includes('synthetic-windows-account'),false);
  assert.equal(raw.includes('12345678-0000-4000-8000-000000000001'),false);

  const temp=mkdtempSync(join(tmpdir(),'p3-csharp-node-relay-'));
  let relay;
  try {
    const certPath=join(temp,'cert.pem'),keyPath=join(temp,'key.pem');
    const generated=spawnSync('openssl',[
      'req','-x509','-newkey','rsa:2048','-nodes','-days','1',
      '-subj','/CN=localhost',
      '-addext','subjectAltName=DNS:localhost,IP:127.0.0.1',
      '-keyout',keyPath,'-out',certPath
    ],{stdio:'ignore',timeout:15000});
    assert.equal(generated.status,0);
    const certificate=readFileSync(certPath,'utf8');
    const privateKey=readFileSync(keyPath,'utf8');
    const mac={sourceDeviceId:'macmini-a',
      publisherToken:'m'.repeat(48),viewerToken:'n'.repeat(48)};
    const windows={sourceDeviceId:'windows-b',
      publisherToken:'w'.repeat(48),viewerToken:'x'.repeat(48)};
    relay=createQuotaMultiDeviceRelayHttpsServer({
      certificate,privateKey,enrollments:[mac,windows],trustedNow:()=>Date.now()
    });
    await new Promise(resolve=>relay.listen(0,'127.0.0.1',resolve));
    const port=relay.address().port;

    async function exchange(source,method,token,body=null) {
      return new Promise((resolve,reject)=>{
        const headers={authorization:'Bearer '+token};
        if(body!==null){
          headers['content-type']='application/json';
          headers['content-length']=Buffer.byteLength(body);
        }
        const req=request({
          hostname:'127.0.0.1',servername:'localhost',port,
          path:'/v1/quota/'+source,method,headers,ca:certificate,
          rejectUnauthorized:true,timeout:5000
        },res=>{
          let response='';
          res.on('data',part=>response+=part);
          res.on('end',()=>resolve({http:res.statusCode,body:JSON.parse(response)}));
        });
        req.on('error',reject);
        req.end(body??undefined);
      });
    }

    assert.equal((await exchange('windows-b','GET',windows.viewerToken)).http,404);
    assert.equal((await exchange('windows-b','PUT',mac.publisherToken,raw)).http,401);
    assert.equal((await exchange('macmini-a','PUT',windows.publisherToken,raw)).http,401);

    const uploaded=await exchange('windows-b','PUT',windows.publisherToken,raw);
    assert.equal(uploaded.http,202);
    assert.equal(uploaded.body.status,'READ_ONLY_SNAPSHOT_ACCEPTED');

    const fetched=await exchange('windows-b','GET',windows.viewerToken);
    assert.equal(fetched.http,200);
    assert.equal(fetched.body.frame.sourceDeviceId,'windows-b');
    assert.equal(fetched.body.frame.fiveHour.remainingPercent,85);
    assert.equal(fetched.body.frame.sevenDay.remainingPercent,62);
    assert.equal(fetched.body.frame.accountIdSha256,frame.accountIdSha256);
    for(const flag of ['actualSendAuthorized','automaticRetryAllowed',
      'productionReservationAuthorized','senderTransitionAuthorized']) {
      assert.equal(fetched.body[flag],false);
    }

    assert.equal((await exchange('macmini-a','GET',mac.viewerToken)).http,404);
    assert.equal((await exchange('windows-b','GET',mac.viewerToken)).http,401);
    assert.equal((await exchange('windows-b','PUT',windows.publisherToken,raw)).http,422);
    assert.equal((await exchange('windows-b','GET',windows.viewerToken)).http,200);
    console.log('P3_CSHARP_WINDOWS_FRAME_TO_NODE_HTTPS_RELAY_PASS=1');
    console.log('P3_OTHER_DEVICE_REMAINS_ISOLATED_PASS=1');
    console.log('REAL_CODEX_READS=0 TELEGRAM_SENDS=0');
  } finally {
    if(relay?.listening)await new Promise(resolve=>relay.close(resolve));
    rmSync(temp,{recursive:true,force:true});
  }
});
