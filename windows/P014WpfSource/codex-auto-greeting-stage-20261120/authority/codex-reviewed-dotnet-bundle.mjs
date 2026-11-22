// Strict reviewed .NET apphost bundle pin. A .NET .exe may remain identical
// while its managed .dll changes, so an EXE SHA alone is NEVER sufficient.
// The expected bundle hash must be supplied independently of the executable
// directory and never derived from a mutable sidecar manifest at runtime.
import {readdirSync,lstatSync,openSync,readSync,closeSync} from 'node:fs';
import {dirname,basename,isAbsolute,join} from 'node:path';
import {createHash,timingSafeEqual} from 'node:crypto';

const HASH=/^[a-f0-9]{64}$/;
const SAFE=/^[A-Za-z0-9][A-Za-z0-9._-]{0,150}$/;
const MAX_FILES=80;
const MAX_FILE_SIZE=120*1024*1024;
const MAX_TOTAL_SIZE=300*1024*1024;

function hashFileByFd(fd){
  const hash=createHash('sha256');
  const buffer=Buffer.allocUnsafe(256*1024);
  let pos=0;
  for(;;){
    const n=readSync(fd,buffer,0,buffer.length,pos);
    if(n===0)break;
    hash.update(buffer.subarray(0,n));pos+=n;
    if(pos>MAX_FILE_SIZE)throw Error('HOLD_REVIEWED_BUNDLE_FILE_OVERSIZE');
  }
  return hash.digest('hex');
}

// The canonical digest exactly matches the staged manifest's JSON.stringify
// of sorted [{file,sha256}] entries. Its filename list rejects hidden files,
// nested dirs and name collisions, not just content changes.
export function inspectedDotNetBundleDigest(executablePath){
  if(process.platform!=='win32' ||
     typeof executablePath!=='string'||!isAbsolute(executablePath))
    throw Error('HOLD_REVIEWED_BUNDLE_PATH');
  const directory=dirname(executablePath);
  const stat=lstatSync(directory);
  if(!stat.isDirectory() || stat.isSymbolicLink())
    throw Error('HOLD_REVIEWED_BUNDLE_DIRECTORY_LINK');
  const names=readdirSync(directory).sort();
  if(names.length<3 || names.length>MAX_FILES ||
     !names.includes(basename(executablePath)))
    throw Error('HOLD_REVIEWED_BUNDLE_FILE_SET');
  const seen=new Set();
  const rows=[];
  let total=0;
  for(const name of names){
    const normalized=name.toLowerCase();
    if(!SAFE.test(name) || seen.has(normalized))
      throw Error('HOLD_REVIEWED_BUNDLE_FILENAME');
    seen.add(normalized);
    const file=join(directory,name);
    const info=lstatSync(file);
    if(!info.isFile() || info.isSymbolicLink() ||
       info.size>MAX_FILE_SIZE || info.size<0)
      throw Error('HOLD_REVIEWED_BUNDLE_UNTRUSTED_ENTRY');
    total+=info.size;
    if(total>MAX_TOTAL_SIZE)throw Error('HOLD_REVIEWED_BUNDLE_TOTAL_SIZE');
    const fd=openSync(file,'r');
    try{
      // A changing file, including replacement while hashing, cannot yield
      // a trusted proof. Rechecked again after the inspected process exits.
      const digest=hashFileByFd(fd);
      if(lstatSync(file).size!==info.size)
        throw Error('HOLD_REVIEWED_BUNDLE_FILE_CHANGED');
      rows.push({file:name,sha256:digest});
    }finally{closeSync(fd);}
  }
  return createHash('sha256').update(JSON.stringify(rows)).digest('hex');
}

export function assertReviewedDotNetBundle({
  executablePath,expectedExecutableSha256,expectedBundleSha256
}={}){
  if(!HASH.test(expectedBundleSha256??'') ||
     !HASH.test(expectedExecutableSha256??''))
    throw Error('HOLD_REVIEWED_BUNDLE_ANCHOR_MISSING');
  const actual=inspectedDotNetBundleDigest(executablePath);
  if(!timingSafeEqual(Buffer.from(actual,'hex'),
     Buffer.from(expectedBundleSha256,'hex')))
    throw Error('HOLD_REVIEWED_DOTNET_BUNDLE_SHA256_MISMATCH');
  const fd=openSync(executablePath,'r');
  try {
    const executableSha=hashFileByFd(fd);
    if(!timingSafeEqual(Buffer.from(executableSha,'hex'),
       Buffer.from(expectedExecutableSha256,'hex')))
      throw Error('HOLD_REVIEWED_DOTNET_EXE_SHA256_MISMATCH');
  }finally{closeSync(fd);}
  return true;
}
