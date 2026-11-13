import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,rmSync,readFileSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {execFileSync} from 'node:child_process';
import {randomBytes} from 'node:crypto';
import {request} from 'node:https';
import {SenderAuthority,provisionSenderAuthorityDatabase,
  signSenderAuthorityRequest} from './sender-authority.mjs';
import {createSenderAuthorityHttpsServer} from './authority-https.mjs';

test('authenticated TLS 1.3 loopback API serves real SQLite across two signed clients',async()=>{
  const folder=mkdtempSync(join(tmpdir(),'codex-authority-loopback-'));
  const dbFile=join(folder,'authority.sqlite');
  const certFile=join(folder,'localhost-cert.pem');
  const keyFile=join(folder,'localhost-key.pem');
  let server,authority;
  try{
    // Ephemeral self-signed CERT for a strictly localhost TEST server; it is
    // never committed, trusted system-wide or used for production deployment.
    execFileSync('openssl',['req','-x509','-newkey','rsa:2048','-nodes',
      '-keyout',keyFile,'-out',certFile,'-days','1',
      '-subj','/CN=localhost'],{stdio:'ignore',timeout:10000});
    provisionSenderAuthorityDatabase(dbFile);
    const windows={deviceId:'win-test',role:'sender',
      hmacKeyHex:randomBytes(32).toString('hex')};
    const mac={deviceId:'mac-test',role:'sender',
      hmacKeyHex:randomBytes(32).toString('hex')};
    authority=new SenderAuthority({
      databasePath:dbFile,enrollments:[windows,mac]
    });
    const tlsKey=readFileSync(keyFile),tlsCert=readFileSync(certFile);
    assert.throws(()=>createSenderAuthorityHttpsServer({
      authority,tlsKey,tlsCert,listenHost:'0.0.0.0',allowRemote:false}),
      /explicit operator approval/);
    server=createSenderAuthorityHttpsServer({authority,tlsKey,tlsCert});
    await new Promise((resolve,reject)=>{
      server.once('error',reject);
      server.listen(0,'127.0.0.1',resolve);
    });
    const port=server.address().port;
    async function invoke(device,method,path,body=null,alter={}) {
      const bodyUtf8=alter.bodyUtf8??(body===null?'':JSON.stringify(body));
      const timestampMs=Date.now(),nonce=randomBytes(16).toString('hex');
      const signature=signSenderAuthorityRequest({
        keyHex:device.hmacKeyHex,deviceId:device.deviceId,
        method,path,timestampMs,nonce,bodyUtf8
      });
      const headers={
        'x-device-id':device.deviceId,
        'x-timestamp-ms':String(timestampMs),
        'x-nonce':nonce,'x-signature':signature
      };
      if(method==='POST'){
        headers['content-type']='application/json';
        headers['content-length']=String(Buffer.byteLength(bodyUtf8));
      }
      Object.assign(headers,alter.headers??{});
      return new Promise((resolve,reject)=>{
        const req=request({host:'127.0.0.1',port,path,method,
          rejectUnauthorized:false,
          minVersion:'TLSv1.3',headers},res=>{
          const parts=[];
          res.on('data',chunk=>parts.push(chunk));
          res.on('end',()=>{
            try{resolve({status:res.statusCode,body:JSON.parse(
              Buffer.concat(parts).toString('utf8'))});}
            catch(error){reject(error);}
          });
        });
        req.on('error',reject);
        req.end(bodyUtf8);
      });
    }
    const first=await invoke(windows,'POST','/v1/lease/acquire',{ttlMs:20000});
    assert.equal(first.status,200);
    assert.equal(first.body.code,'LEASE_ACQUIRED');
    assert.equal(first.body.epoch,1);
    assert.equal(first.body.actualSendAuthorized,false);
    assert.equal((await invoke(mac,'POST','/v1/lease/acquire',
      {ttlMs:20000})).body.code,'HOLD_OWNER_ACTIVE');
    const key='d'.repeat(64);
    assert.equal((await invoke(windows,'POST','/v1/events/reserve',{
      epoch:1,eventKey:key,lane:'FIVE_HOUR_100'
    })).body.code,'EVENT_RESERVED_NO_SEND');
    assert.equal((await invoke(windows,'POST','/v1/events/reserve',{
      epoch:1,eventKey:key,lane:'FIVE_HOUR_100'
    })).body.code,'HOLD_EVENT_ALREADY_RESERVED');
    assert.equal((await invoke(windows,'GET','/v1/events/'+key))
      .body.event.state,'RESERVED_BEFORE_SEND');
    const invalid=await invoke(mac,'POST','/v1/lease/release',
      {epoch:1},{headers:{'x-signature':'0'.repeat(64)}});
    assert.equal(invalid.body.code,'HOLD_UNAUTHENTICATED');
    assert.equal((await invoke(windows,'POST','/v1/lease/release',
      {epoch:1})).body.code,'LEASE_RELEASED');
    assert.equal((await invoke(mac,'POST','/v1/lease/acquire',
      {ttlMs:20000})).body.epoch,2);
    assert.equal((await invoke(mac,'POST','/v1/events/reserve',{
      epoch:2,eventKey:key,lane:'WEEKLY_100'
    })).body.code,'HOLD_EVENT_ALREADY_RESERVED');
    console.log('AUTHORITY_HTTPS_LOCALHOST_TWO_SIGNED_DEVICES_PASS');
  } finally {
    if(server)await new Promise(resolve=>server.close(resolve));
    authority?.close();
    rmSync(folder,{recursive:true,force:true});
  }
});
