// Authority-process-only composition: reviewed Windows host -> supervised
// job exit -> exact native Codex read-only provider proof -> SQLite settlement.
// No user-facing API or automatic scheduler is exported. Provisioning is
// separate, default OFF, and cannot be enabled by a client ACK.
import {readFileSync,lstatSync} from 'node:fs';
import {basename,isAbsolute} from 'node:path';
import {createHash} from 'node:crypto';
import {CodexChatSenderAuthority} from './codex-chat-authority.mjs';
import {createCodexWindowsLocalRuntime}
  from './codex-chat-windows-local-runtime.mjs';
import {createTrustedWindowsNativeCodexVerifier}
  from './native-codex-readonly-verifier.mjs';

const HASH=/^[a-f0-9]{64}$/;
const VERIFY_NAME='Codex-Chat-Native-ReadOnly-Verifier.exe';

function assertVerifierPinned(path,expectedSha256) {
  if(process.platform!=='win32' || typeof path!=='string' ||
     !isAbsolute(path) || basename(path)!==VERIFY_NAME ||
     !HASH.test(expectedSha256??''))
    throw Error('HOLD_UNREVIEWED_NATIVE_VERIFIER_PATH');
  const info=lstatSync(path);
  if(!info.isFile() || info.isSymbolicLink())
    throw Error('HOLD_NATIVE_VERIFIER_FILE_IDENTITY');
  if(createHash('sha256').update(readFileSync(path)).digest('hex')!==
     expectedSha256)
    throw Error('HOLD_NATIVE_VERIFIER_SHA256');
}

// An independent native verifier is never replaced by the sender's turn ACK.
// Exact SHA is checked once at construction and again before every invocation.
export function createCodexWindowsNativeAuthorityRuntime({
  databasePath,enrollments,recurringConsent=null,
  reviewedHostExecutable,reviewedHostSha256,reviewedHostBundleSha256,
  reviewedVerifierExecutable,reviewedVerifierSha256,reviewedVerifierBundleSha256,
  reviewedWorkerExecutable=null,reviewedWorkerSha256=null,
  reviewedWorkerBundleSha256=null,
  resolveApprovedPrivateJob,localOwnerEnrollment=null,now=Date.now
}={}) {
  if(typeof resolveApprovedPrivateJob!=='function' ||
     typeof databasePath!=='string' || !Array.isArray(enrollments) ||
     typeof now!=='function')
    throw TypeError('Explicit local authority provisioning required');
  if(localOwnerEnrollment!==null &&
     (recurringConsent?.approvedByUser!==true ||
      recurringConsent.profileId!==localOwnerEnrollment.profileId ||
      recurringConsent.accountSha256!==localOwnerEnrollment.accountSha256 ||
      recurringConsent.threadId!==localOwnerEnrollment.threadId ||
      (localOwnerEnrollment.fiveHour&&!recurringConsent.fiveHour) ||
      (localOwnerEnrollment.sevenDay&&!recurringConsent.sevenDay)))
    throw Error('HOLD_LOCAL_OWNER_RECURRING_CONSENT_MISMATCH');
  assertVerifierPinned(reviewedVerifierExecutable,reviewedVerifierSha256);
  const nativeRead=createTrustedWindowsNativeCodexVerifier({
    executablePath:reviewedVerifierExecutable,
    expectedExecutableSha256:reviewedVerifierSha256,
    expectedBundleSha256:reviewedVerifierBundleSha256
  });
  const providerVerify=async query=>{
    assertVerifierPinned(reviewedVerifierExecutable,reviewedVerifierSha256);
    return nativeRead(query);
  };
  return createCodexWindowsLocalRuntime({
    reviewedHostExecutable,reviewedHostSha256,reviewedHostBundleSha256,
    reviewedWorkerExecutable,reviewedWorkerSha256,reviewedWorkerBundleSha256,
    resolveApprovedPrivateJob,ownerEnrollment:localOwnerEnrollment,now,
    createAuthority:({verifySenderQuiescence})=>
      new CodexChatSenderAuthority({
        databasePath,enrollments,recurringConsent,now,
        verifyCodexTurnCompletion:providerVerify,
        verifySenderQuiescence
      })
  });
}
