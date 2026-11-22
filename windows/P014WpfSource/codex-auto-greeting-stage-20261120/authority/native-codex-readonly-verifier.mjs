// Trusted HOST-LOCAL verifier backend for Codex-chat authority.
// It invokes a reviewed, separate Windows read-only Codex app-server helper.
// No browser automation, login, thread creation, turn/start, network listener
// or credential copying. Not wired to an installed/live Sender.
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {basename,isAbsolute} from 'node:path';
import {lstatSync} from 'node:fs';
import {assertReviewedDotNetBundle}
  from './codex-reviewed-dotnet-bundle.mjs';

const execFileAsync=promisify(execFile);
const HASH=/^[a-f0-9]{64}$/;
const UUID=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const expectedKeys=[
  'accountSha256','completedAtMs','startedAtMs','status',
  'threadId','turnId','userInputText'
];

export function createTrustedWindowsNativeCodexVerifier({
  executablePath,expectedExecutableSha256,expectedBundleSha256,
  timeoutMs=25_000
}={}) {
  if(process.platform!=='win32' ||
     typeof executablePath!=='string' ||
     !isAbsolute(executablePath) ||
     basename(executablePath)!=='Codex-Chat-Native-ReadOnly-Verifier.exe' ||
     !Number.isSafeInteger(timeoutMs) ||
     timeoutMs<1000 || timeoutMs>60_000)
    throw new TypeError('Explicit reviewed Windows verifier executable required');
  const info=lstatSync(executablePath);
  if(!info.isFile() || info.isSymbolicLink())
    throw new TypeError('Verifier must be a real file, not a link');
  const checked=()=>assertReviewedDotNetBundle({
    executablePath,expectedExecutableSha256,expectedBundleSha256
  });
  checked();

  return async function independentlyVerifyCodexTurn(request) {
    if(!request || !HASH.test(request.accountSha256??'') ||
       !UUID.test(request.threadId??'') ||
       !UUID.test(request.turnId??''))
      throw new TypeError('Exact account, thread and turn identifiers required');
    let output;
    try {
      checked();
      const {stdout}=await execFileAsync(executablePath,[
        '--readonly-verify',
        request.accountSha256,
        request.threadId,
        request.turnId
      ],{
        shell:false,windowsHide:true,timeout:timeoutMs,maxBuffer:8192,
        encoding:'utf8'
      });
      checked();
      output=stdout.trim();
    }catch{
      // Do not expose raw stderr, paths or Codex provider messages.
      throw new Error('Native read-only verifier did not prove completion');
    }

    if(output.length===0 || output.length>4096 ||
       output.split(/\r?\n/).length!==1)
      throw new Error('Malformed native verification evidence');
    let proof;
    try{proof=JSON.parse(output);}
    catch{throw new Error('Unparseable native verification evidence');}
    if(!proof || Array.isArray(proof) || typeof proof!=='object' ||
       Object.keys(proof).sort().join('|')!==expectedKeys.join('|') ||
       proof.accountSha256!==request.accountSha256 ||
       proof.threadId!==request.threadId ||
       proof.turnId!==request.turnId ||
       proof.status!=='completed' || proof.userInputText!=='안녕' ||
       !Number.isSafeInteger(proof.startedAtMs) ||
       !Number.isSafeInteger(proof.completedAtMs) ||
       proof.startedAtMs>proof.completedAtMs)
      throw new Error('Native provider evidence inconsistent');
    return Object.freeze(proof);
  };
}
