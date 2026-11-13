import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,rmSync,existsSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {randomBytes} from 'node:crypto';
import {provisionSenderAuthorityDatabase,SenderAuthority,
  signSenderAuthorityRequest} from './sender-authority.mjs';

const mkKey=()=>randomBytes(32).toString('hex');
const mkNonce=()=>randomBytes(16).toString('hex');
const eventA='a'.repeat(64),eventB='b'.repeat(64),eventC='c'.repeat(64);
const viewers=()=>[
  {deviceId:'windows-1',role:'sender',hmacKeyHex:mkKey()},
  {deviceId:'mac-1',role:'sender',hmacKeyHex:mkKey()},
  {deviceId:'mobile-viewer',role:'viewer',hmacKeyHex:mkKey()}
];

test('real SQLite single-owner fencing, cross-device event dedup and restart durability',()=>{
  const folder=mkdtempSync(join(tmpdir(),'codex-shared-authority-product-'));
  const databasePath=join(folder,'authority.sqlite');
  const enrollments=viewers();
  let now=Date.UTC(2026,10,13,14,0);
  let db;
  try {
    assert.equal(existsSync(databasePath),false);
    provisionSenderAuthorityDatabase(databasePath);
    assert.equal(existsSync(databasePath),true);
    assert.throws(()=>provisionSenderAuthorityDatabase(databasePath),
      /Refusing to overwrite/);
    db=new SenderAuthority({databasePath,enrollments,now:()=>now});
    const request=(deviceId,method,path,body=null,extra={})=>{
      const keyHex=enrollments.find(i=>i.deviceId===deviceId).hmacKeyHex;
      const timestampMs=extra.timestampMs??now;
      const nonce=extra.nonce??mkNonce();
      const bodyUtf8=extra.bodyUtf8??(body===null?'':JSON.stringify(body));
      return {method,path,bodyUtf8,headers:{
        'x-device-id':deviceId,
        'x-timestamp-ms':String(timestampMs),
        'x-nonce':nonce,
        'x-signature':signSenderAuthorityRequest({
          keyHex,deviceId,method,path,timestampMs,nonce,bodyUtf8})
      }};
    };
    const call=(id,method,path,body=null,extra={})=>
      db.dispatch(request(id,method,path,body,extra));

    const read=call('mobile-viewer','GET','/v1/status');
    assert.equal(read.code,'STATUS');
    assert.equal(read.actualSendAuthorized,false);
    assert.equal(read.lease.owner,null);

    assert.equal(call('mobile-viewer','POST','/v1/lease/acquire',
      {ttlMs:20000}).code,'HOLD_FORBIDDEN_ROLE');
    const first=call('windows-1','POST','/v1/lease/acquire',{ttlMs:20000});
    assert.equal(first.code,'LEASE_ACQUIRED');
    assert.equal(first.epoch,1);
    assert.equal(call('mac-1','POST','/v1/lease/acquire',
      {ttlMs:20000}).code,'HOLD_OWNER_ACTIVE');

    const signed=request('windows-1','POST','/v1/events/reserve',
      {epoch:1,eventKey:eventA,lane:'FIVE_HOUR_100'});
    assert.equal(db.dispatch(signed).code,'EVENT_RESERVED_NO_SEND');
    assert.equal(db.dispatch(signed).code,'HOLD_REPLAY');
    assert.equal(call('windows-1','POST','/v1/events/reserve',
      {epoch:1,eventKey:eventA,lane:'WEEKLY_100'}).code,
      'HOLD_EVENT_ALREADY_RESERVED');
    assert.equal(call('mobile-viewer','GET','/v1/events/'+eventA).event.state,
      'RESERVED_BEFORE_SEND');

    now+=5000;
    assert.equal(call('windows-1','POST','/v1/lease/renew',
      {epoch:1,ttlMs:20000}).code,'LEASE_RENEWED');
    assert.equal(call('windows-1','POST','/v1/lease/release',
      {epoch:1}).code,'LEASE_RELEASED');
    const mac=call('mac-1','POST','/v1/lease/acquire',{ttlMs:20000});
    assert.equal(mac.code,'LEASE_ACQUIRED');
    assert.equal(mac.epoch,2);
    assert.equal(call('windows-1','POST','/v1/lease/renew',
      {epoch:1,ttlMs:20000}).code,'HOLD_STALE_OWNER');
    assert.equal(call('mac-1','POST','/v1/events/reserve',
      {epoch:2,eventKey:eventA,lane:'FIVE_HOUR_100'}).code,
      'HOLD_EVENT_ALREADY_RESERVED');
    assert.equal(call('mac-1','POST','/v1/events/reserve',
      {epoch:2,eventKey:eventB,lane:'WEEKLY_100'}).code,
      'EVENT_RESERVED_NO_SEND');
    assert.equal(call('mac-1','POST','/v1/events/uncertain',
      {epoch:2,eventKey:eventB}).code,'EVENT_MARKED_UNCERTAIN');
    assert.equal(call('mac-1','POST','/v1/events/uncertain',
      {epoch:2,eventKey:eventB}).code,'HOLD_EVENT_UNCHANGED');

    // Persistent history must not reset when the process restarts.
    db.close();
    db=new SenderAuthority({databasePath,enrollments,now:()=>now});
    assert.equal(call('mobile-viewer','GET','/v1/events/'+eventB).event.state,
      'SEND_OUTCOME_UNCERTAIN');
    assert.equal(call('mac-1','POST','/v1/events/reserve',
      {epoch:2,eventKey:eventA,lane:'FIVE_HOUR_100'}).code,
      'HOLD_EVENT_ALREADY_RESERVED');
    now+=20001;
    assert.equal(call('mac-1','POST','/v1/lease/renew',
      {epoch:2,ttlMs:20000}).code,'HOLD_STALE_OWNER');
    const third=call('windows-1','POST','/v1/lease/acquire',{ttlMs:20000});
    assert.equal(third.code,'LEASE_ACQUIRED');
    assert.equal(third.epoch,3);
    assert.equal(call('windows-1','POST','/v1/events/reserve',
      {epoch:3,eventKey:eventB,lane:'WEEKLY_100'}).code,
      'HOLD_EVENT_ALREADY_RESERVED');
    assert.equal(call('windows-1','POST','/v1/events/reserve',
      {epoch:3,eventKey:eventC,lane:'FIVE_HOUR_100'}).code,
      'EVENT_RESERVED_NO_SEND');
    console.log('AUTHORITY_REAL_SQLITE_CROSS_HOST_FENCING_AND_RESTART_PASS');
  }finally{db?.close();rmSync(folder,{recursive:true,force:true});}
});

test('HMAC identity, body integrity, nonce replay, timestamp and shape guards',()=>{
  const folder=mkdtempSync(join(tmpdir(),'codex-shared-authority-auth-'));
  const databasePath=join(folder,'authority.sqlite'),enrollments=viewers();
  let now=Date.UTC(2026,10,13,14,20);
  provisionSenderAuthorityDatabase(databasePath);
  const db=new SenderAuthority({databasePath,enrollments,now:()=>now});
  try{
    const deviceId='windows-1',keyHex=enrollments[0].hmacKeyHex;
    const mk=(path,body, options={})=>{
      const method='POST',timestampMs=options.timestampMs??now,
        nonce=options.nonce??mkNonce(),
        bodyUtf8=options.bodyUtf8??JSON.stringify(body);
      const signature=signSenderAuthorityRequest({
        keyHex,deviceId,method,path,timestampMs,nonce,bodyUtf8});
      return {method,path,bodyUtf8,headers:{
        'x-device-id':deviceId,'x-timestamp-ms':String(timestampMs),
        'x-nonce':nonce,'x-signature':signature
      }};
    };
    const good=mk('/v1/lease/acquire',{ttlMs:20000});
    const malicious={...good,bodyUtf8:'{"ttlMs":60000}'};
    assert.equal(db.dispatch(malicious).code,'HOLD_UNAUTHENTICATED');
    const forged={...good,headers:{...good.headers,'x-device-id':'mac-1'}};
    assert.equal(db.dispatch(forged).code,'HOLD_UNAUTHENTICATED');
    const stale=mk('/v1/lease/acquire',{ttlMs:20000},
      {timestampMs:now-30001});
    assert.equal(db.dispatch(stale).code,'HOLD_UNAUTHENTICATED');
    const future=mk('/v1/lease/acquire',{ttlMs:20000},
      {timestampMs:now+30001});
    assert.equal(db.dispatch(future).code,'HOLD_UNAUTHENTICATED');
    const badShape=mk('/v1/lease/acquire',{ttlMs:20000},
      {bodyUtf8:'{"ttlMs":20000,"ttlMs":30000}'});
    assert.equal(db.dispatch(badShape).code,'HOLD_INVALID_BODY');
    assert.equal(db.dispatch({...good,headers:{}}).code,'HOLD_UNAUTHENTICATED');
    assert.equal(db.dispatch({...good,path:'/v1/lease/release'}).code,
      'HOLD_UNAUTHENTICATED');
    assert.equal(db.dispatch({...good,method:'PUT'}).code,'HOLD_INVALID_REQUEST');
    assert.equal(db.dispatch({...good,bodyUtf8:'X'.repeat(1025)}).code,
      'HOLD_INVALID_REQUEST');
    assert.equal(db.dispatch(good).code,'LEASE_ACQUIRED');
    assert.equal(db.dispatch(good).code,'HOLD_REPLAY');
    assert.equal(db.dispatch(mk('/v1/events/reserve',
      {epoch:1,eventKey:'z'.repeat(64),lane:'FIVE_HOUR_100'})).code,
      'HOLD_INVALID_BODY');
    const invalidTimestamp={...good,headers:{...good.headers,
      'x-timestamp-ms':'invalid','x-nonce':mkNonce()}};
    assert.equal(db.dispatch(invalidTimestamp).code,'HOLD_UNAUTHENTICATED');
    assert.equal(db.dispatch({...good,path:'/evil'}).code,'HOLD_ROUTE');
    assert.equal(db.dispatch({
      method:'GET',path:'/v1/status',bodyUtf8:'ignored',headers:good.headers
    }).code,'HOLD_INVALID_REQUEST');
    console.log('AUTHORITY_HMAC_TAMPER_REPLAY_AND_FAIL_CLOSED_PASS');
  }finally{db.close();rmSync(folder,{recursive:true,force:true});}
});
