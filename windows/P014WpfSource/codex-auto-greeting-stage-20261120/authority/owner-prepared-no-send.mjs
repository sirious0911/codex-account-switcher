import {createHmac,timingSafeEqual} from 'node:crypto';
import {closeSync,fstatSync,lstatSync,openSync,readSync} from 'node:fs';
import {basename,dirname,resolve} from 'node:path';
import {DatabaseSync} from 'node:sqlite';
import {canonicalDueText} from './codex-due-local-pipe.mjs';
import {bindPreparedOwner,requireBoundDue} from './owner-authority-binding.mjs';
import {readCodexWindowsOwnerSwitches} from './codex-windows-owner-switches.mjs';

function checkFixtureDatabase(config) {
  const database=resolve(config.databasePath);
  if(dirname(database)!==resolve(config.appDataHome) ||
     !/^codex-nosend-[A-Za-z0-9_-]+\.sqlite$/.test(basename(database)) ||
     !lstatSync(database).isFile())
    throw Error('HOLD_PREPARED_FIXTURE_DATABASE');
  // Read a bounded, standalone fixture snapshot and query an in-memory copy.
  // Never open the on-disk path with SQLite or participate in a WAL transaction.
  for(const suffix of ['-wal','-shm','-journal']) {
    if(lstatSync(database+suffix,{throwIfNoEntry:false}))
      throw Error('HOLD_PREPARED_FIXTURE_DATABASE');
  }
  const fd=openSync(database,'r');
  let snapshot;
  try {
    const stat=fstatSync(fd);
    if(!stat.isFile() || stat.size<100 || stat.size>4*1024*1024)
      throw Error('HOLD_PREPARED_FIXTURE_DATABASE');
    snapshot=Buffer.alloc(stat.size);
    if(readSync(fd,snapshot,0,snapshot.length,0)!==snapshot.length ||
       snapshot.toString('ascii',0,16)!=='SQLite format 3\0' ||
       snapshot[18]!==1 || snapshot[19]!==1)
      throw Error('HOLD_PREPARED_FIXTURE_DATABASE');
  } finally {
    closeSync(fd);
  }
  const db=new DatabaseSync(':memory:');
  try {
    db.deserialize(snapshot);
    db.exec('PRAGMA query_only=ON; PRAGMA trusted_schema=OFF');
    const meta=db.prepare('SELECT schema_version FROM authority_meta WHERE id=1').get();
    const lease=db.prepare('SELECT fencing_epoch FROM active_lease WHERE id=1').get();
    if(meta?.schema_version!==6 || !Number.isSafeInteger(lease?.fencing_epoch))
      throw Error('HOLD_PREPARED_FIXTURE_DATABASE');
  } finally {
    db.close();
  }
}

// No pipe listener, on-disk SQLite connection, native executable, or dispatch callback.
export function verifyPreparedOwnerNoSend(config) {
  bindPreparedOwner(config);
  const {owner,appDataHome,verificationPacket:packet}=config;
  const expected=createHmac('sha256',Buffer.from(config.pipeSecretHex,'hex'))
    .update(canonicalDueText(packet)).digest();
  const now=Date.now(), event=packet.event;
  if(!timingSafeEqual(expected,Buffer.from(packet.mac,'hex')) ||
     Math.abs(now-packet.sentAtMs)>30_000 ||
     event.resetAtUnix*1000>now+30_000 || now-event.resetAtUnix*1000>600_000 ||
     event.observedAtUnix*1000>now+30_000 || now-event.observedAtUnix*1000>360_000)
    throw Error('HOLD_PREPARED_PIPE_VERIFICATION');
  requireBoundDue(owner,{...event,ownerDevice:owner.deviceId},readCodexWindowsOwnerSwitches({
    appDataHome,profileId:owner.profileId,accountSha256:owner.accountSha256,
    threadId:owner.threadId,ownerDevice:owner.deviceId,
  }));
  checkFixtureDatabase(config);
}
