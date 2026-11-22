// ISOLATED CODEX CHAT authority core: not the Telegram authority.
// Does NOT send messages, read Codex credentials, open a listener, or
// grant production send permission. Explicit-only SQLite database.
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
const LANES=new Set(['CODEX_FIVE_HOUR','CODEX_SEVEN_DAY']);
const THREAD=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const TURN_ID=/^[0-9a-z-]{8,128}$/i;
const PROFILE=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const MAX_SKEW_MS=30_000;
const MAX_BODY=1024;
const MODES=new Set(['GET','POST']);
const SAFE=Object.freeze({
  actualSendAuthorized:false,
  automaticRetryAllowed:false,
  remoteSenderActivated:false
});
const response=(code, details={})=>Object.freeze({schemaVersion:1,code,...details,...SAFE});
const oneShotResponse=details=>Object.freeze({
  schemaVersion:1,code:'SINGLE_USE_CODEX_GREETING_CLAIMED',...details,
  actualSendAuthorized:true,automaticRetryAllowed:false,remoteSenderActivated:false
});
const sha=(data)=>createHash('sha256').update(data,'utf8').digest('hex');

export function codexChatSenderCanonical({deviceId,method,path,timestampMs,nonce,bodyUtf8=''}) {
  return ['CODEX_CHAT_SENDER_AUTH_V1',deviceId,method,path,
    String(timestampMs),nonce,sha(bodyUtf8),''].join('\n');
}

export function signCodexChatSenderRequest({keyHex, deviceId, method, path,
  timestampMs, nonce, bodyUtf8=''}) {
  if(!KEY.test(keyHex??'') || !DEVICE.test(deviceId??'') ||
     !MODES.has(method) || typeof path!=='string' ||
     !Number.isSafeInteger(timestampMs) || !NONCE.test(nonce??'') ||
     typeof bodyUtf8!=='string')
    throw new TypeError('Invalid signed request arguments');
  return createHmac('sha256',Buffer.from(keyHex,'hex')).update(
    codexChatSenderCanonical({deviceId,method,path,timestampMs,nonce,bodyUtf8}),
    'utf8').digest('hex');
}

const routes=new Map([
  ['GET /v1/codex-chat/status','status'],
  ['POST /v1/codex-chat/lease/acquire','acquire'],
  ['POST /v1/codex-chat/lease/renew','renew'],
  ['POST /v1/codex-chat/lease/release','release'],
  ['POST /v1/codex-chat/events/reserve','reserve'],
  ['POST /v1/codex-chat/events/uncertain','uncertain'],
  ['POST /v1/codex-chat/events/acknowledge','acknowledge'],
  ['POST /v1/codex-chat/dispatch/claim','claim']
]);
function routeFor(method,path) {
  if(method==='GET' && /^\/v1\/codex-chat\/events\/[0-9a-f]{64}$/.test(path))
    return 'event';
  if(method==='GET' && /^\/v1\/codex-chat\/dispatch\/readiness\/[0-9a-f]{64}$/.test(path))
    return 'dispatchReadiness';
  return routes.get(method+' '+path)??null;
}
const isObject=x=>x!==null&&typeof x==='object'&&!Array.isArray(x)&&
  Object.getPrototypeOf(x)===Object.prototype;
function fields(body, expected) {
  return isObject(body) && Object.keys(body).length===expected.length &&
    Object.keys(body).every(k=>expected.includes(k)) &&
    expected.every(k=>Object.hasOwn(body,k));
}
export function codexChatResetEventKey({accountSha256,threadId,lane,resetAtUnix}) {
  if(!EVENT.test(accountSha256??'') || !THREAD.test(threadId??'') ||
     !LANES.has(lane) || !Number.isSafeInteger(resetAtUnix) ||
     resetAtUnix<=0)throw new TypeError('Invalid Codex reset key attributes');
  return sha(['CODEX_CHAT_RESET_EVENT_V1',accountSha256,threadId,lane,
    String(resetAtUnix),''].join('\n'));
}
const validEpoch=e=>Number.isSafeInteger(e)&&e>0;
const validTtl=t=>Number.isSafeInteger(t)&&t>=1000&&t<=60_000;

export function provisionCodexChatAuthorityDatabase(dbFile) {
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
      'CREATE TABLE authority_meta(id INTEGER PRIMARY KEY CHECK(id=1),schema_version INTEGER NOT NULL CHECK(schema_version=6),last_epoch INTEGER NOT NULL CHECK(last_epoch>=0));'+
      'INSERT INTO authority_meta VALUES(1,6,0);'+
      'CREATE TABLE active_lease(id INTEGER PRIMARY KEY CHECK(id=1),owner_device TEXT,fencing_epoch INTEGER NOT NULL CHECK(fencing_epoch>=0),expires_at_ms INTEGER NOT NULL);'+
      'INSERT INTO active_lease VALUES(1,NULL,0,0);'+
      'CREATE TABLE request_nonces(device_id TEXT NOT NULL,nonce TEXT NOT NULL,at_ms INTEGER NOT NULL,PRIMARY KEY(device_id,nonce));'+
      'CREATE TABLE event_ledger(event_key TEXT PRIMARY KEY,lane TEXT NOT NULL,owner_device TEXT NOT NULL,epoch INTEGER NOT NULL CHECK(epoch>0),state TEXT NOT NULL CHECK(state IN (\'RESERVED_BEFORE_SEND\',\'SEND_OUTCOME_UNCERTAIN\',\'SEND_ACKNOWLEDGED\')),created_at_ms INTEGER NOT NULL,account_hash TEXT NOT NULL,thread_id TEXT NOT NULL,reset_at_unix INTEGER NOT NULL,turn_id TEXT UNIQUE,verified_at_ms INTEGER);'
    );
    db.exec('COMMIT;');
  } catch(error) {
    try{db?.exec('ROLLBACK;');}catch{}
    // Keep incomplete DB for inspection rather than deleting silently.
    throw error;
  } finally {db?.close();}
  return file;
}

export class CodexChatSenderAuthority {
  #db;
  #enrolled;
  #now;
  #closed=false;
  #providerVerifier;
  #senderQuiescenceVerifier;
  #recurringConsent;
  constructor({databasePath,enrollments,now=Date.now,
    verifyCodexTurnCompletion=null,verifySenderQuiescence=null,
    recurringConsent=null}) {
    if(typeof databasePath!=='string'||!existsSync(databasePath))
      throw new Error('Explicit pre-provisioned authority database required');
    if(!Array.isArray(enrollments)||enrollments.length<1||enrollments.length>16)
      throw new Error('Explicit 1-16 device credentials required');
    if(typeof now!=='function')throw new TypeError('Invalid server clock');
    if(verifyCodexTurnCompletion!==null &&
       typeof verifyCodexTurnCompletion!=='function')
      throw new TypeError('Verifier must be a trusted server-side function');
    if(verifySenderQuiescence!==null &&
       typeof verifySenderQuiescence!=='function')
      throw new TypeError('Quiescence verifier must be a trusted server-side function');
    this.#providerVerifier=verifyCodexTurnCompletion;
    this.#senderQuiescenceVerifier=verifySenderQuiescence;
    // Only trusted local provisioning may record recurring user consent.
    // Signed remote clients cannot self-declare it in a request body.
    if(recurringConsent!==null &&
       (!isObject(recurringConsent) ||
        !fields(recurringConsent,['approvalId','approvedByUser','profileId',
          'accountSha256','threadId','fiveHour','sevenDay']) ||
        recurringConsent.approvedByUser!==true ||
        !/^USER_APPROVAL_[A-Z0-9_]{12,120}$/.test(recurringConsent.approvalId??'') ||
        !PROFILE.test(recurringConsent.profileId??'') ||
        !EVENT.test(recurringConsent.accountSha256??'') ||
        !THREAD.test(recurringConsent.threadId??'') ||
        typeof recurringConsent.fiveHour!=='boolean' ||
        typeof recurringConsent.sevenDay!=='boolean' ||
        (!recurringConsent.fiveHour&&!recurringConsent.sevenDay)))
      throw new TypeError('Exact local recurring user consent required');
    this.#recurringConsent=recurringConsent===null?null:
      Object.freeze({...recurringConsent});
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
      // Refuse schema-1/2/3 ledgers: older code allowed clients to clear an
      // in-flight reservation via uncertain or an unverified turn ACK.
      if(meta?.schema_version!==6 || !Number.isSafeInteger(meta.last_epoch) ||
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
  // Trusted authority-local reconciliation ONLY. There is deliberately NO
  // matching HTTP route or client-supplied evidence shortcut. This callback
  // must independently query the provider's read-only thread/turn metadata,
  // including the authenticated account, exact user input and completion.
  // No real verifier is installed in the isolated Windows sender.
  async reconcileCompletedTurn({eventKey,turnId}={}) {
    if(this.#closed)return response('HOLD_CLOSED');
    if(!EVENT.test(eventKey??'')||!TURN_ID.test(turnId??''))
      return response('HOLD_INVALID_RECONCILIATION');
    if(this.#providerVerifier===null)
      return response('HOLD_PROVIDER_VERIFIER_NOT_CONFIGURED');
    let now,record;
    try {
      now=this.#time();
      record=this.#db.prepare(
        'SELECT event_key AS eventKey,owner_device AS ownerDevice,epoch,'+
        'state,account_hash AS accountSha256,thread_id AS threadId,'+
        'created_at_ms AS reservedAtMs,reset_at_unix AS resetAtUnix '+
        'FROM event_ledger WHERE event_key=?'
      ).get(eventKey);
    }catch{return response('HOLD_STORAGE_ERROR');}
    if(!record)return response('HOLD_EVENT_NOT_FOUND');
    if(record.state==='SEND_ACKNOWLEDGED')
      return response('ALREADY_INDEPENDENTLY_VERIFIED');
    if(!['RESERVED_BEFORE_SEND','SEND_OUTCOME_UNCERTAIN'].includes(record.state))
      return response('HOLD_EVENT_STATE');
    let evidence;
    try {
      evidence=await this.#providerVerifier(Object.freeze({
        accountSha256:record.accountSha256,threadId:record.threadId,turnId,
        resetAtUnix:record.resetAtUnix,createdAtMs:record.reservedAtMs
      }));
    }catch{
      return response('HOLD_PROVIDER_VERIFICATION_UNAVAILABLE');
    }
    if(!isObject(evidence) ||
       evidence.accountSha256!==record.accountSha256 ||
       evidence.threadId!==record.threadId ||
       evidence.turnId!==turnId ||
       evidence.status!=='completed' ||
       evidence.userInputText!=='안녕' ||
       !Number.isSafeInteger(evidence.startedAtMs) ||
       !Number.isSafeInteger(evidence.completedAtMs) ||
       evidence.startedAtMs<record.reservedAtMs-1000 ||
       evidence.completedAtMs<evidence.startedAtMs ||
       evidence.completedAtMs>now+30_000)
      return response('HOLD_PROVIDER_COMPLETION_NOT_PROVED');

    // A provider-completed turn does NOT prove that the prior sender
    // subprocess/session is quiescent. A separate authoritative supervisor
    // must prove no pending worker can submit another turn. This cannot
    // be based on a remote signed client assertion or a positive turn ACK.
    if(this.#senderQuiescenceVerifier===null)
      return response('HOLD_SENDER_QUIESCENCE_NOT_CONFIGURED');
    let quiescence;
    try{
      quiescence=await this.#senderQuiescenceVerifier(Object.freeze({
        eventKey,ownerDevice:record.ownerDevice,epoch:record.epoch,
        accountSha256:record.accountSha256,threadId:record.threadId,
        turnId,reservedAtMs:record.reservedAtMs
      }));
    }catch{
      return response('HOLD_SENDER_QUIESCENCE_UNAVAILABLE');
    }
    if(!isObject(quiescence) ||
       quiescence.eventKey!==eventKey ||
       quiescence.ownerDevice!==record.ownerDevice ||
       quiescence.epoch!==record.epoch ||
       quiescence.noActiveSendWorker!==true ||
       !Number.isSafeInteger(quiescence.verifiedAtMs) ||
       quiescence.verifiedAtMs<record.reservedAtMs ||
       quiescence.verifiedAtMs>now+30_000)
      return response('HOLD_SENDER_QUIESCENCE_NOT_PROVED');

    // Between the async provider read and the SQLite write, the event
    // must remain immutable; recheck exact identity and original state.
    try {
      this.#db.exec('BEGIN IMMEDIATE');
      try {
        const latest=this.#db.prepare(
          'SELECT state,account_hash AS accountSha256,thread_id AS threadId,'+
          'created_at_ms AS reservedAtMs FROM event_ledger WHERE event_key=?'
        ).get(eventKey);
        if(!latest || latest.state!==record.state ||
           latest.accountSha256!==record.accountSha256 ||
           latest.threadId!==record.threadId ||
           latest.reservedAtMs!==record.reservedAtMs) {
          this.#db.exec('ROLLBACK');
          return response('HOLD_EVENT_CHANGED_DURING_VERIFICATION');
        }
        const result=this.#db.prepare(
          "UPDATE event_ledger SET state='SEND_ACKNOWLEDGED',"+
          'turn_id=?,verified_at_ms=? WHERE event_key=? AND state=?'
        ).run(turnId,now,eventKey,record.state);
        if(result.changes!==1)throw new Error('Reconcile update failed');
        this.#db.exec('COMMIT');
        // Completion is proven for the exact turn. The authority remains
        // incapable of authorizing a new real Codex turn.
        return response('PROVIDER_TURN_COMPLETION_VERIFIED',{
          eventKey,verifiedAtMs:now,turnId
        });
      }catch(error){
        try{this.#db.exec('ROLLBACK');}catch{}
        return response('HOLD_STORAGE_ERROR');
      }
    }catch{return response('HOLD_STORAGE_ERROR');}
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
    const canonical=codexChatSenderCanonical({
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
    // Both an outstanding reservation and an uncertain send result are
    // durable ownership fences. Expired TTL MUST NOT transfer ownership
    // while a prior Codex app-server session could still submit a turn.
    const inFlight=()=>db.prepare(
      "SELECT event_key FROM event_ledger WHERE state IN "+
      "('RESERVED_BEFORE_SEND','SEND_OUTCOME_UNCERTAIN') LIMIT 1"
    ).get();
    const settleOwner=()=>current.owner===identity.deviceId&&
      current.epoch===body.epoch;
    switch(op) {
      case 'status':
        return response('STATUS',{authorityTimeMs:now,lease:current});
      case 'event':{
        const key=path.slice('/v1/codex-chat/events/'.length);
        const event=db.prepare('SELECT lane,state,owner_device AS owner,epoch FROM event_ledger WHERE event_key=?').get(key);
        return response(event?'EVENT_FOUND':'EVENT_ABSENT',{event:event??null});
      }
      case 'dispatchReadiness':{
        const eventKey=path.slice('/v1/codex-chat/dispatch/readiness/'.length);
        const event=db.prepare(
          'SELECT owner_device AS owner,epoch,state,account_hash AS accountSha256,'+
          'thread_id AS threadId,reset_at_unix AS resetAtUnix FROM event_ledger WHERE event_key=?'
        ).get(eventKey);
        if(!event)return response('HOLD_DISPATCH_EVENT_ABSENT');
        if(event.owner!==identity.deviceId || event.epoch!==current.epoch ||
           current.owner!==identity.deviceId || current.expiresAtMs<=now)
          return response('HOLD_DISPATCH_OWNER_NOT_CURRENT');
        if(event.state!=='RESERVED_BEFORE_SEND')
          return response('HOLD_DISPATCH_EVENT_NOT_RESERVED');
        const c=this.#recurringConsent;
        const approved=c!==null &&
          c.accountSha256===event.accountSha256 &&
          c.threadId===event.threadId;
        // Readiness is NOT a send permit. The claim is atomic and once-only.
        return response(approved?'READY_FOR_SINGLE_USE_CLAIM':
          'HOLD_OPERATOR_AUTOMATIC_SEND_APPROVAL_REQUIRED',{
          eventKey,
          accountSha256:event.accountSha256,
          threadId:event.threadId,
          resetAtUnix:event.resetAtUnix,
          ownerDevice:current.owner,
          epoch:current.epoch,
          leaseExpiresAtMs:current.expiresAtMs,
          permitExpiresAtMs:null
        });
      }
      case 'claim':{
        if(!validEpoch(body.epoch) || !EVENT.test(body.eventKey??'') ||
           !EVENT.test(body.accountSha256??'') ||
           !THREAD.test(body.threadId??'') ||
           !PROFILE.test(body.profileId??'') ||
           !LANES.has(body.lane) ||
           !Number.isSafeInteger(body.resetAtUnix) || body.resetAtUnix<=0)
          return response('HOLD_INVALID_BODY');
        const c=this.#recurringConsent;
        if(c===null || c.profileId!==body.profileId ||
           c.accountSha256!==body.accountSha256 ||
           c.threadId!==body.threadId ||
           (body.lane==='CODEX_FIVE_HOUR'&&!c.fiveHour) ||
           (body.lane==='CODEX_SEVEN_DAY'&&!c.sevenDay))
          return response('HOLD_RECURRING_SEND_NOT_APPROVED_FOR_TARGET');
        if(!owned() || current.expiresAtMs-now<5000)
          return response('HOLD_CLAIM_LEASE_NOT_FRESH');
        const key=codexChatResetEventKey({
          accountSha256:body.accountSha256,threadId:body.threadId,
          lane:body.lane,resetAtUnix:body.resetAtUnix
        });
        if(body.eventKey!==key)return response('HOLD_CLAIM_EVENT_KEY_MISMATCH');
        const age=now-body.resetAtUnix*1000;
        if(!Number.isSafeInteger(age)||age<0||age>600_000)
          return response('HOLD_RESET_TIMESTAMP');
        const event=db.prepare(
          'SELECT owner_device AS owner,epoch,state,lane,'+
          'account_hash AS accountSha256,thread_id AS threadId,'+
          'reset_at_unix AS resetAtUnix FROM event_ledger WHERE event_key=?'
        ).get(body.eventKey);
        if(!event || event.owner!==identity.deviceId ||
           event.epoch!==body.epoch || event.state!=='RESERVED_BEFORE_SEND' ||
           event.lane!==body.lane ||
           event.accountSha256!==body.accountSha256 ||
           event.threadId!==body.threadId ||
           event.resetAtUnix!==body.resetAtUnix)
          return response('HOLD_CLAIM_NOT_RESERVED_OR_ALREADY_USED');
        // Consume one-shot permission durably BEFORE returning to caller.
        // A lost reply or crashed worker must never permit automatic retry.
        const written=db.prepare(
          "UPDATE event_ledger SET state='SEND_OUTCOME_UNCERTAIN' "+
          "WHERE event_key=? AND state='RESERVED_BEFORE_SEND'"
        ).run(body.eventKey);
        if(written.changes!==1)
          return response('HOLD_CLAIM_TRANSACTION_NOT_COMMITTED');
        return oneShotResponse({
          eventKey:body.eventKey,accountSha256:body.accountSha256,
          threadId:body.threadId,profileId:body.profileId,
          lane:body.lane,resetAtUnix:body.resetAtUnix,
          ownerDevice:identity.deviceId,epoch:body.epoch,
          permitExpiresAtMs:Math.min(now+4000,current.expiresAtMs)
        });
      }
      case 'acquire':{
        if(!validTtl(body.ttlMs))return response('HOLD_INVALID_BODY');
        if(inFlight())return response('HOLD_SEND_IN_FLIGHT');
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
        if(inFlight())return response('HOLD_SEND_IN_FLIGHT');
        if(!owned())return response('HOLD_STALE_OWNER');
        db.prepare('UPDATE active_lease SET owner_device=NULL,expires_at_ms=0 WHERE id=1').run();
        return response('LEASE_RELEASED',{epoch:body.epoch});
      }
      case 'reserve':{
        if(!validEpoch(body.epoch)||!EVENT.test(body.eventKey??'') ||
           !LANES.has(body.lane) || !EVENT.test(body.accountSha256??'') ||
           !THREAD.test(body.threadId??'') ||
           !Number.isSafeInteger(body.resetAtUnix) ||
           body.resetAtUnix<=0)
          return response('HOLD_INVALID_BODY');
        // A signed request is only a claim, not independent provider quota
        // evidence. It cannot authorize a real Codex message.
        const offset=now-body.resetAtUnix*1000;
        if(!Number.isSafeInteger(offset)||offset<0||offset>600_000)
          return response('HOLD_RESET_TIMESTAMP');
        const computed=codexChatResetEventKey({
          accountSha256:body.accountSha256,threadId:body.threadId,
          lane:body.lane,resetAtUnix:body.resetAtUnix
        });
        if(body.eventKey!==computed)return response('HOLD_EVENT_KEY_MISMATCH');
        if(!owned())return response('HOLD_STALE_OWNER');
        const found=db.prepare('SELECT event_key FROM event_ledger WHERE event_key=?')
          .get(body.eventKey);
        if(found)return response('HOLD_EVENT_ALREADY_RESERVED');
        // Both lanes at the same reset moment are ONE Codex message even
        // though their canonical event hashes differ.
        const simultaneous=db.prepare(
          'SELECT event_key FROM event_ledger WHERE account_hash=? AND thread_id=? '+ 
          'AND ABS(reset_at_unix-?)<=300 LIMIT 1'
        ).get(body.accountSha256,body.threadId,body.resetAtUnix);
        if(simultaneous)return response('HOLD_SIMULTANEOUS_CODEX_RESET');
        if(inFlight())return response('HOLD_SEND_IN_FLIGHT');
        db.prepare(
          'INSERT INTO event_ledger(event_key,lane,owner_device,epoch,state,'+
          'created_at_ms,account_hash,thread_id,reset_at_unix) VALUES(?,?,?,?,?,?,?,?,?)'
        ).run(body.eventKey,body.lane,identity.deviceId,body.epoch,
          'RESERVED_BEFORE_SEND',now,body.accountSha256,body.threadId,
          body.resetAtUnix);
        // The event is durably reserved, but even a positive reservation is
        // NOT an authorization to call turn/start.
        return response('EVENT_RESERVED_NO_SEND',{
          eventKey:body.eventKey,epoch:body.epoch,sendFenceDurable:true
        });
      }
      case 'uncertain':{
        if(!validEpoch(body.epoch)||!EVENT.test(body.eventKey??''))
          return response('HOLD_INVALID_BODY');
        // A signed owner alone CANNOT attest that a Codex turn transport has
        // terminated. Releasing the fence would let another host send while
        // the old child is still running (especially after lease expiry).
        // Fail CLOSED until a separately reviewed operator reconciliation
        // protocol can prove quiescence. Do not mutate the SQLite event.
        return response('HOLD_OPERATOR_RECONCILIATION_REQUIRED');
      }
      case 'acknowledge':{
        if(!validEpoch(body.epoch)||!EVENT.test(body.eventKey??'')||
           !Number.isSafeInteger(body.messageId)||body.messageId<=0)
          return response('HOLD_INVALID_BODY');
        // An enrolled device can submit any positive messageId, even when
        // no independently verified Codex turn. An HMAC authenticates the
        // caller, NOT the native turn's completion or provider timer change.
        // Therefore an ACK is not yet a safe event settlement. Preserve
        // the durable in-flight fence, even beyond TTL/server restart.
        // Implement an independently verifiable quiescence protocol before
        // enabling any terminal event transition or cross-host handoff.
        if(!settleOwner())return response('HOLD_STALE_OWNER');
        return response('HOLD_ACK_EVIDENCE_REQUIRED');

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
      reserve:['epoch','eventKey','lane','accountSha256','threadId','resetAtUnix'],uncertain:['epoch','eventKey'],
      acknowledge:['epoch','eventKey','messageId'],
      claim:['epoch','eventKey','lane','profileId','accountSha256','threadId','resetAtUnix']
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
