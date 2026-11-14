// Standalone, portable Node 24 authority core for a future Mac mini service.
// It never sends Telegram, reads Codex credentials, starts a listener or
// grants production send permission. Explicitly provisioned SQLite only.
import {createHash, createHmac, timingSafeEqual} from 'node:crypto';
import {DatabaseSync} from 'node:sqlite';
import {existsSync, openSync, closeSync, unlinkSync} from 'node:fs';
import {resolve} from 'node:path';
import {parseUniqueJsonFrame} from '../offline/strict-json-frame.mjs';

const DEVICE=/^[A-Za-z0-9-]{1,48}$/;
const KEY=/^[0-9a-f]{64}$/;
const NONCE=/^[0-9a-f]{32}$/;
const EVENT=/^[0-9a-f]{64}$/;
const ROLES=new Set(['sender','viewer']);
const LANES=new Set(['FIVE_HOUR_100','WEEKLY_100']);
const MAX_SKEW_MS=30_000;
const MAX_BODY=1024;
const MODES=new Set(['GET','POST']);
const SAFE=Object.freeze({
  actualSendAuthorized:false,
  automaticRetryAllowed:false,
  remoteSenderActivated:false
});
const response=(code, details={})=>Object.freeze({schemaVersion:1,code,...details,...SAFE});
const sha=(data)=>createHash('sha256').update(data,'utf8').digest('hex');

export function senderAuthorityCanonical({deviceId,method,path,timestampMs,nonce,bodyUtf8=''}) {
  return ['CODEX_SHARED_SENDER_AUTH_V1',deviceId,method,path,
    String(timestampMs),nonce,sha(bodyUtf8),''].join('\n');
}

export function signSenderAuthorityRequest({keyHex, deviceId, method, path,
  timestampMs, nonce, bodyUtf8=''}) {
  if(!KEY.test(keyHex??'') || !DEVICE.test(deviceId??'') ||
     !MODES.has(method) || typeof path!=='string' ||
     !Number.isSafeInteger(timestampMs) || !NONCE.test(nonce??'') ||
     typeof bodyUtf8!=='string')
    throw new TypeError('Invalid signed request arguments');
  return createHmac('sha256',Buffer.from(keyHex,'hex')).update(
    senderAuthorityCanonical({deviceId,method,path,timestampMs,nonce,bodyUtf8}),
    'utf8').digest('hex');
}

const routes=new Map([
  ['GET /v1/status','status'],
  ['POST /v1/lease/acquire','acquire'],
  ['POST /v1/lease/renew','renew'],
  ['POST /v1/lease/release','release'],
  ['POST /v1/events/reserve','reserve'],
  ['POST /v1/events/uncertain','uncertain']
]);
function routeFor(method,path) {
  if(method==='GET' && /^\/v1\/events\/[0-9a-f]{64}$/.test(path))
    return 'event';
  return routes.get(method+' '+path)??null;
}
const isObject=x=>x!==null&&typeof x==='object'&&!Array.isArray(x)&&
  Object.getPrototypeOf(x)===Object.prototype;
function fields(body, expected) {
  return isObject(body) && Object.keys(body).length===expected.length &&
    Object.keys(body).every(k=>expected.includes(k)) &&
    expected.every(k=>Object.hasOwn(body,k));
}
const validEpoch=e=>Number.isSafeInteger(e)&&e>0;
const validTtl=t=>Number.isSafeInteger(t)&&t>=1000&&t<=60_000;

export function provisionSenderAuthorityDatabase(dbFile) {
  if(typeof dbFile!=='string'||dbFile.length===0)
    throw new TypeError('Explicit database path required');
  const file=resolve(dbFile);
  if(existsSync(file))throw new Error('Refusing to overwrite an existing authority');
  // Exclusive creation only. No automatic migration of existing owner/event
  // history, and never a default operating directory.
  const fd=openSync(file,'wx',0o600);closeSync(fd);
  let db;
  try {
    db=new DatabaseSync(file,{timeout:10000});
    db.exec('PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;');
    db.exec('BEGIN IMMEDIATE;');
    db.exec(
      'CREATE TABLE authority_meta(id INTEGER PRIMARY KEY CHECK(id=1),schema_version INTEGER NOT NULL CHECK(schema_version=1),last_epoch INTEGER NOT NULL CHECK(last_epoch>=0));'+
      'INSERT INTO authority_meta VALUES(1,1,0);'+
      'CREATE TABLE active_lease(id INTEGER PRIMARY KEY CHECK(id=1),owner_device TEXT,fencing_epoch INTEGER NOT NULL CHECK(fencing_epoch>=0),expires_at_ms INTEGER NOT NULL);'+
      'INSERT INTO active_lease VALUES(1,NULL,0,0);'+
      'CREATE TABLE request_nonces(device_id TEXT NOT NULL,nonce TEXT NOT NULL,at_ms INTEGER NOT NULL,PRIMARY KEY(device_id,nonce));'+
      'CREATE TABLE event_ledger(event_key TEXT PRIMARY KEY,lane TEXT NOT NULL,owner_device TEXT NOT NULL,epoch INTEGER NOT NULL CHECK(epoch>0),state TEXT NOT NULL CHECK(state IN (\'RESERVED_BEFORE_SEND\',\'SEND_OUTCOME_UNCERTAIN\')),created_at_ms INTEGER NOT NULL);'
    );
    db.exec('COMMIT;');
  } catch(error) {
    try{db?.exec('ROLLBACK;');}catch{}
    // Keep incomplete DB for inspection rather than deleting silently.
    throw error;
  } finally {db?.close();}
  return file;
}

export class SenderAuthority {
  #db;
  #enrolled;
  #now;
  #closed=false;
  constructor({databasePath,enrollments,now=Date.now}) {
    if(typeof databasePath!=='string'||!existsSync(databasePath))
      throw new Error('Explicit pre-provisioned authority database required');
    if(!Array.isArray(enrollments)||enrollments.length<1||enrollments.length>16)
      throw new Error('Explicit 1-16 device credentials required');
    if(typeof now!=='function')throw new TypeError('Invalid server clock');
    const ids=new Set(), keys=new Set();
    this.#enrolled=new Map();
    for(const item of enrollments) {
      if(!isObject(item)||!DEVICE.test(item.deviceId??'') ||
         !ROLES.has(item.role)||!KEY.test(item.hmacKeyHex??'') ||
         ids.has(item.deviceId)||keys.has(item.hmacKeyHex))
        throw new Error('Invalid or duplicate device enrollment');
      ids.add(item.deviceId); keys.add(item.hmacKeyHex);
      this.#enrolled.set(item.deviceId,{
        role:item.role,key:Buffer.from(item.hmacKeyHex,'hex')
      });
    }
    this.#now=now;
    this.#db=new DatabaseSync(resolve(databasePath),{timeout:10000});
    try {
      this.#db.exec('PRAGMA busy_timeout=10000; PRAGMA synchronous=FULL;');
      const meta=this.#db.prepare('SELECT schema_version,last_epoch FROM authority_meta WHERE id=1').get();
      const lease=this.#db.prepare('SELECT id FROM active_lease WHERE id=1').get();
      if(meta?.schema_version!==1 || !Number.isSafeInteger(meta.last_epoch) ||
         !lease || this.#db.prepare('PRAGMA integrity_check').get().integrity_check!=='ok')
        throw new Error('Authority schema or integrity not verified');
      for(const table of ['request_nonces','event_ledger'])
        if(!this.#db.prepare('SELECT 1 FROM sqlite_master WHERE name=? AND type=\'table\'').get(table))
          throw new Error('Incomplete authority schema');
    } catch(err){this.#db.close();throw err;}
  }
  close() {
    if(this.#closed)return;
    this.#closed=true;
    this.#db.close();
  }
  #time(){
    const now=this.#now();
    if(!Number.isSafeInteger(now)||now<Date.UTC(2020,0,1))
      throw new Error('Untrusted server time');
    return now;
  }
  #authenticate({method,path,headers,bodyUtf8},now) {
    if(!isObject(headers))return null;
    const ids=['x-device-id','x-timestamp-ms','x-nonce','x-signature'];
    if(Object.keys(headers).length!==ids.length||
       !ids.every(k=>typeof headers[k]==='string'&&Object.hasOwn(headers,k)))
      return null;
    const deviceId=headers['x-device-id'];
    const timestampText=headers['x-timestamp-ms'];
    const nonce=headers['x-nonce'];
    const signature=headers['x-signature'];
    const enrolled=this.#enrolled.get(deviceId);
    if(!enrolled || !/^[0-9]{13}$/.test(timestampText) ||
       !NONCE.test(nonce)||!KEY.test(signature))
      return null;
    const timestampMs=Number(timestampText);
    if(!Number.isSafeInteger(timestampMs) ||
       Math.abs(now-timestampMs)>MAX_SKEW_MS)return null;
    const canonical=senderAuthorityCanonical({
      deviceId,method,path,timestampMs,nonce,bodyUtf8
    });
    const provided=Buffer.from(signature,'hex');
    const expected=createHmac('sha256',enrolled.key)
      .update(canonical,'utf8').digest();
    if(provided.length!==expected.length||
       !timingSafeEqual(provided,expected))return null;
    return {deviceId,role:enrolled.role,nonce};
  }
  #mutate(op,body,identity,now,path) {
    const db=this.#db;
    const lease=()=>db.prepare('SELECT owner_device AS owner,fencing_epoch AS epoch,expires_at_ms AS expiresAtMs FROM active_lease WHERE id=1').get();
    const current=lease();
    const owned=()=>current.owner===identity.deviceId&&
      current.epoch===body.epoch&&current.expiresAtMs>now;
    switch(op) {
      case 'status':
        return response('STATUS',{authorityTimeMs:now,lease:current});
      case 'event':{
        const key=path.slice('/v1/events/'.length);
        const event=db.prepare('SELECT lane,state,owner_device AS owner,epoch FROM event_ledger WHERE event_key=?').get(key);
        return response(event?'EVENT_FOUND':'EVENT_ABSENT',{event:event??null});
      }
      case 'acquire':{
        if(!validTtl(body.ttlMs))return response('HOLD_INVALID_BODY');
        if(current.owner && current.expiresAtMs>now)
          return response('HOLD_OWNER_ACTIVE');
        const last=db.prepare('SELECT last_epoch AS value FROM authority_meta WHERE id=1').get().value;
        if(!Number.isSafeInteger(last)||last>=Number.MAX_SAFE_INTEGER-1)
          return response('HOLD_EPOCH_EXHAUSTED');
        const epoch=last+1;
        db.prepare('UPDATE authority_meta SET last_epoch=? WHERE id=1').run(epoch);
        db.prepare('UPDATE active_lease SET owner_device=?,fencing_epoch=?,expires_at_ms=? WHERE id=1')
          .run(identity.deviceId,epoch,now+body.ttlMs);
        return response('LEASE_ACQUIRED',{epoch,expiresAtMs:now+body.ttlMs});
      }
      case 'renew':{
        if(!validEpoch(body.epoch)||!validTtl(body.ttlMs))
          return response('HOLD_INVALID_BODY');
        if(!owned())return response('HOLD_STALE_OWNER');
        db.prepare('UPDATE active_lease SET expires_at_ms=? WHERE id=1')
          .run(now+body.ttlMs);
        return response('LEASE_RENEWED',{epoch:body.epoch,expiresAtMs:now+body.ttlMs});
      }
      case 'release':{
        if(!validEpoch(body.epoch))return response('HOLD_INVALID_BODY');
        if(!owned())return response('HOLD_STALE_OWNER');
        db.prepare('UPDATE active_lease SET owner_device=NULL,expires_at_ms=0 WHERE id=1').run();
        return response('LEASE_RELEASED',{epoch:body.epoch});
      }
      case 'reserve':{
        if(!validEpoch(body.epoch)||!EVENT.test(body.eventKey??'') ||
           !LANES.has(body.lane))return response('HOLD_INVALID_BODY');
        if(!owned())return response('HOLD_STALE_OWNER');
        const found=db.prepare('SELECT event_key FROM event_ledger WHERE event_key=?')
          .get(body.eventKey);
        if(found)return response('HOLD_EVENT_ALREADY_RESERVED');
        db.prepare('INSERT INTO event_ledger(event_key,lane,owner_device,epoch,state,created_at_ms) VALUES(?,?,?,?,?,?)')
          .run(body.eventKey,body.lane,identity.deviceId,body.epoch,'RESERVED_BEFORE_SEND',now);
        return response('EVENT_RESERVED_NO_SEND',{eventKey:body.eventKey});
      }
      case 'uncertain':{
        if(!validEpoch(body.epoch)||!EVENT.test(body.eventKey??''))
          return response('HOLD_INVALID_BODY');
        if(!owned())return response('HOLD_STALE_OWNER');
        const update=db.prepare('UPDATE event_ledger SET state=\'SEND_OUTCOME_UNCERTAIN\' WHERE event_key=? AND owner_device=? AND epoch=? AND state=\'RESERVED_BEFORE_SEND\'')
          .run(body.eventKey,identity.deviceId,body.epoch);
        return response(update.changes===1?'EVENT_MARKED_UNCERTAIN':'HOLD_EVENT_UNCHANGED');
      }
    }
    return response('HOLD_ROUTE');
  }
  dispatch({method,path,headers,bodyUtf8=''}) {
    if(this.#closed)return response('HOLD_CLOSED');
    if(!MODES.has(method)||typeof path!=='string'||path.length>160 ||
       path.includes('?')||path.includes('#')||
       typeof bodyUtf8!=='string'||Buffer.byteLength(bodyUtf8,'utf8')>MAX_BODY)
      return response('HOLD_INVALID_REQUEST');
    const op=routeFor(method,path);
    if(!op)return response('HOLD_ROUTE');
    if(method==='GET' && bodyUtf8!=='')return response('HOLD_INVALID_REQUEST');
    // Authenticate the exact signed raw bytes *before* interpreting fields,
    // so a modified path/body cannot obtain a different action by accident.
    let now;
    try{now=this.#time();}catch{return response('HOLD_CLOCK');}
    const identity=this.#authenticate({method,path,headers,bodyUtf8},now);
    if(!identity)return response('HOLD_UNAUTHENTICATED');
    if(method==='POST' && identity.role!=='sender')
      return response('HOLD_FORBIDDEN_ROLE');
    let body={};
    try{
      if(method==='POST')body=parseUniqueJsonFrame(bodyUtf8,MAX_BODY,4);
    }catch{return response('HOLD_INVALID_BODY');}
    const allowed={
      acquire:['ttlMs'],renew:['epoch','ttlMs'],release:['epoch'],
      reserve:['epoch','eventKey','lane'],uncertain:['epoch','eventKey']
    };
    if(method==='POST' && !fields(body,allowed[op]))
      return response('HOLD_INVALID_BODY');
    try{
      this.#db.exec('BEGIN IMMEDIATE');
      try{
        // All signed requests, including reads, consume one durable nonce.
        // Invalid clients cannot fill the nonce ledger.
        const nonceInsert=this.#db.prepare(
          'INSERT OR IGNORE INTO request_nonces(device_id,nonce,at_ms) VALUES(?,?,?)')
          .run(identity.deviceId,identity.nonce,now);
        if(nonceInsert.changes!==1){
          this.#db.exec('ROLLBACK');
          return response('HOLD_REPLAY');
        }
        const result=this.#mutate(op,body,identity,now,path);
        this.#db.exec('COMMIT');
        return result;
      }catch(error){
        try{this.#db.exec('ROLLBACK');}catch{}
        return response('HOLD_STORAGE_ERROR');
      }
    }catch{return response('HOLD_STORAGE_ERROR');}
  }
}
