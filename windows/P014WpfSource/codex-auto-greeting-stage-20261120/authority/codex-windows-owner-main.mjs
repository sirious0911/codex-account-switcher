import {bindPreparedOwner,requireBoundDue,verifyListeningCertificate} from './owner-authority-binding.mjs';
// Explicit user-session Windows owner process, not a service installer.
// --offline-stdin: real OS process/pipe lifecycle, never opens Codex/TLS/DB.
// --prepared-stdin: accepts only a separately approved private, exact account
// envelope from a future CurrentUser DPAPI launcher; zero defaults to live.
// No login, new conversation, automatic retry, browser, Telegram or Git action.
import {createCodexWindowsOwnerSession} from './codex-windows-owner-session.mjs';
import {readCodexWindowsOwnerSwitches} from './codex-windows-owner-switches.mjs';
import {verifyPreparedOwnerNoSend} from './owner-prepared-no-send.mjs';

const mode=process.argv.slice(2);
if(process.platform!=='win32' || mode.length!==1 ||
   !['--offline-stdin','--prepared-stdin','--prepared-verify-no-send'].includes(mode[0])){
  process.stderr.write('HOLD_OWNER_ENTRYPOINT_DISABLED\n');
  process.exitCode=2;
}else{
  let input='',config=null,session=null,ownerRuntime=null,https=null;
  let stopping=false;
  const stop=async()=>{
    if(stopping)return;
    stopping=true;
    try{await session?.stop();}catch{}
    if(https)await new Promise(resolve=>https.close(()=>resolve()));
    try{ownerRuntime?.close();}catch{}
  };
  try{
    for await(const part of process.stdin){
      input+=part.toString('utf8');
      if(Buffer.byteLength(input)>50_000)
        throw Error('HOLD_OWNER_INPUT_OVERSIZE');
    }
    config=JSON.parse(input);
    if(!config || config.schemaVersion!==1 ||
       !config.owner || !config.pipeName || !config.pipeSecretHex ||
       !config.appDataHome)
      throw Error('HOLD_OWNER_PRIVATE_PROVISIONING_REQUIRED');
    if(mode[0]==='--prepared-verify-no-send'){
      verifyPreparedOwnerNoSend(config);
      process.stdout.write('OWNER_PREPARED_VERIFIED_NO_SEND\n');
    }else{
      const owner=config.owner;
      const readSwitches=()=>readCodexWindowsOwnerSwitches({
        appDataHome:config.appDataHome,profileId:owner.profileId,
        accountSha256:owner.accountSha256,threadId:owner.threadId,
        ownerDevice:owner.deviceId
      });
      let runOwnerDue=async()=>({code:'HOLD_OFFLINE_OWNER_NO_SEND'});
      if(mode[0]==='--prepared-stdin'){
        const bundle=config.bundle;
        let httpsPort=0;
        const privateJob=config.privateJob;
        const authorityBinding=bindPreparedOwner(config);
        const {createCodexWindowsNativeAuthorityRuntime}=
          await import('./codex-chat-windows-native-authority-runtime.mjs');
        ownerRuntime=createCodexWindowsNativeAuthorityRuntime({
          databasePath:config.databasePath,
          enrollments:config.enrollments,
          recurringConsent:config.recurringConsent,
          localOwnerEnrollment:owner,
          reviewedHostExecutable:bundle.host.executablePath,
          reviewedHostSha256:bundle.host.exeSha256,
          reviewedHostBundleSha256:bundle.host.bundleSha256,
          reviewedWorkerExecutable:bundle.worker.executablePath,
          reviewedWorkerSha256:bundle.worker.exeSha256,
          reviewedWorkerBundleSha256:bundle.worker.bundleSha256,
          reviewedVerifierExecutable:bundle.verifier.executablePath,
          reviewedVerifierSha256:bundle.verifier.exeSha256,
          reviewedVerifierBundleSha256:bundle.verifier.bundleSha256,
          resolveApprovedPrivateJob:async event=>{
            requireBoundDue(owner,event,readSwitches());
            return {
              ...privateJob,ProfileId:event.profileId,
              AccountSha256:event.accountSha256,ThreadId:event.threadId,
              Lane:event.lane,ResetAtUnix:event.resetAtUnix,
              ObservedAtUnix:event.observedAtUnix,
              DeviceId:owner.deviceId,
              NativeWorkerExecutable:bundle.worker.executablePath,
              NativeWorkerSha256:bundle.worker.exeSha256,
              NativeWorkerBundleSha256:bundle.worker.bundleSha256,
              AuthorityUrl:'https://127.0.0.1:'+httpsPort+'/',
              ...authorityBinding,
              EnableLiveDispatch:true
            };
          }
        });
        https=ownerRuntime.createLoopbackHttpsServer({
          tlsKey:Buffer.from(config.tls.keyPem,'utf8'),
          tlsCert:Buffer.from(config.tls.certPem,'utf8')
        });
        await new Promise((resolve,reject)=>{
          https.once('error',reject);
          https.listen(config.tls.port??0,'127.0.0.1',()=>{
            https.removeListener('error',reject);
            httpsPort=https.address().port;
            resolve();
          });
        });
        await verifyListeningCertificate(httpsPort,authorityBinding);
        runOwnerDue=(event,options)=>ownerRuntime.runOwnerDue(event,options);
      }
      session=createCodexWindowsOwnerSession({
        pipeName:config.pipeName,secretHex:config.pipeSecretHex,
        profileId:owner.profileId,accountSha256:owner.accountSha256,
        threadId:owner.threadId,readSwitches,runOwnerDue
      });
      await session.start();
      // The readiness line contains no user identity, key, process argument,
      // account info or pipe name. Never echo private input to stdout/stderr.
      process.stdout.write(mode[0]==='--prepared-stdin'
        ?'OWNER_PREPARED_RUNNING\n':'OWNER_OFFLINE_RUNNING_NO_SEND\n');
      process.on('SIGINT',()=>{void stop();});
      process.on('SIGTERM',()=>{void stop();});
      process.on('SIGHUP',()=>{void stop();});
    }
  }catch{
    process.stderr.write('HOLD_OWNER_BOOTSTRAP_FAIL_CLOSED\n');
    process.exitCode=2;
    await stop();
  }
}
