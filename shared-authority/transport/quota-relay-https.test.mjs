import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {mkdtempSync,readFileSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {request} from 'node:https';
import {createQuotaRelayHttpsServer} from './quota-relay-https.mjs';

test('actual HTTPS quota relay: publish, authenticated view, replay/identity fencing',async()=>{
  const dir=mkdtempSync(join(tmpdir(),'codex-quota-p3-'));
  let server;
  try{
    const certPath=join(dir,'test-cert.pem'),keyPath=join(dir,'test-key.pem');
    const created=spawnSync('openssl',[
      'req','-x509','-newkey','rsa:2048','-nodes','-days','1',
      '-subj','/CN=localhost',
      '-addext','subjectAltName=DNS:localhost,IP:127.0.0.1',
      '-keyout',keyPath,'-out',certPath
    ],{stdio:'ignore'});
    assert.equal(created.status,0,'Ephemeral TLS certificate generation failed');
    const cert=readFileSync(certPath,'utf8');
    const key=readFileSync(keyPath,'utf8');
    const time={now:Date.now()};
    const publish='p'.repeat(48),view='v'.repeat(48);
    const source='macmini-a';
    server=createQuotaRelayHttpsServer({
      certificate:cert,privateKey:key,
      publisherToken:publish,viewerToken:view,sourceDeviceId:source,
      trustedNow:()=>time.now
    });
    await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
    const port=server.address().port;
    async function call(method,token,body=null,path='/v1/quota/'+source){
      const content=body===null?null:typeof body==='string'?body:JSON.stringify(body);
      return new Promise((resolve,reject)=>{
        const headers={authorization:'Bearer '+token};
        if(content!==null){
          headers['content-type']='application/json';
          headers['content-length']=Buffer.byteLength(content);
        }
        const req=request({
          hostname:'127.0.0.1',port,path,method,headers,
          ca:cert,servername:'localhost',rejectUnauthorized:true,timeout:5000
        },res=>{
          let data='';
          res.on('data',chunk=>data+=chunk);
          res.on('end',()=>resolve({statusCode:res.statusCode,body:JSON.parse(data),
            headers:res.headers}));
        });
        req.on('error',reject);
        req.end(content??undefined);
      });
    }
    const packet={
      schemaVersion:1,sourceDeviceId:source,sourceSessionId:'a'.repeat(32),
      sequence:1,collectedAtUtcMs:time.now-5000,
      accountIdSha256:'b'.repeat(64),
      fiveHour:{remainingPercent:85,resetAtUtcMs:time.now+3_600_000},
      sevenDay:{remainingPercent:62,resetAtUtcMs:time.now+172_800_000},
      actualSendAuthorized:false,automaticRetryAllowed:false,
      productionReservationAuthorized:false,senderTransitionAuthorized:false
    };

    const empty=await call('GET',view);
    assert.equal(empty.statusCode,404);
    const deniedWrite=await call('PUT',view,packet);
    assert.equal(deniedWrite.statusCode,401);
    const deniedRead=await call('GET',publish);
    assert.equal(deniedRead.statusCode,401);
    const badRoute=await call('GET',view,null,'/v1/quota/macmini-b');
    assert.equal(badRoute.statusCode,404);

    const uploaded=await call('PUT',publish,packet);
    assert.equal(uploaded.statusCode,202);
    assert.equal(uploaded.body.status,'READ_ONLY_SNAPSHOT_ACCEPTED');
    const received=await call('GET',view);
    assert.equal(received.statusCode,200);
    assert.equal(received.body.status,'FRESH_READ_ONLY');
    assert.equal(received.body.frame.sourceDeviceId,source);
    assert.equal(received.body.frame.fiveHour.remainingPercent,85);
    assert.equal(received.body.frame.sevenDay.remainingPercent,62);
    for(const flag of ['actualSendAuthorized','automaticRetryAllowed',
        'productionReservationAuthorized','senderTransitionAuthorized']){
      assert.equal(received.body[flag],false);
      assert.equal(received.body.frame[flag],undefined);
    }
    assert.match(received.headers['cache-control'],/no-store/);

    const replay=await call('PUT',publish,packet);
    assert.equal(replay.statusCode,422);
    assert.equal(replay.body.status,'HOLD_REPLAY_OR_REORDERED_VIEW');
    const forbidden=await call('PUT',publish,{...packet,sequence:2,
      collectedAtUtcMs:time.now-1000,token:'not-allowed'});
    assert.equal(forbidden.statusCode,422);
    assert.equal(forbidden.body.status,'HOLD_INVALID_VIEW_FRAME');
    const future=await call('PUT',publish,{...packet,sequence:2,
      collectedAtUtcMs:time.now+10_000});
    assert.equal(future.statusCode,422);
    assert.equal(future.body.status,'HOLD_STALE_OR_FUTURE_VIEW');

    time.now+=130_000;
    const aged=await call('GET',view);
    assert.equal(aged.statusCode,200);
    assert.equal(aged.body.status,'AGED_READ_ONLY');

    time.now+=471_000;
    const expired=await call('GET',view);
    assert.equal(expired.statusCode,410);
    assert.equal(expired.body.frame,null);
    const second={...packet,sourceSessionId:'c'.repeat(32),sequence:1,
      collectedAtUtcMs:time.now-1000,accountIdSha256:'d'.repeat(64),
      fiveHour:{remainingPercent:90,resetAtUtcMs:time.now+3_600_000},
      sevenDay:{remainingPercent:72,resetAtUtcMs:time.now+86_400_000}};
    const conflict=await call('PUT',publish,second);
    assert.equal(conflict.statusCode,409);
    assert.equal(conflict.body.status,'HOLD_ACCOUNT_SWITCH_REQUIRES_LOCAL_RESET');
    const quarantined=await call('GET',view);
    assert.equal(quarantined.statusCode,409);
    assert.equal(quarantined.body.frame,null);
    const stillHeld=await call('PUT',publish,second);
    assert.equal(stillHeld.statusCode,409);

    console.log('P3_ACTUAL_TLS_RELAY_READ_ONLY_PASS=1');
    console.log('P3_REPLAY_IDENTITY_QUARANTINE_PASS=1');
    console.log('P3_TELEGRAM_SEND=0 REAL_CODEX_READ=0');
  }finally{
    if(server?.listening)await new Promise(resolve=>server.close(resolve));
    rmSync(dir,{recursive:true,force:true});
  }
});
