// Current-user operational controls. Read afresh for every reset event.
// UI draft alone cannot activate the Sender; the distinct operator activation,
// persisted existing-thread binding and the user's current draft must ALL agree.
// No writes, credentials, Codex RPC, filesystem traversal or network access.
import {lstatSync,readFileSync} from 'node:fs';
import {resolve,join} from 'node:path';

const HASH=/^[a-f0-9]{64}$/;
const UUID=/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/;
const OFF=Object.freeze({master:false,fiveHour:false,sevenDay:false});
const exactly=(x,attrs)=>x && typeof x==='object'&&!Array.isArray(x) &&
  attrs.every(k=>Object.hasOwn(x,k));
function readLocalJson(root,name) {
  const path=join(root,name);
  const file=lstatSync(path);
  if(!file.isFile() || file.isSymbolicLink() || file.size<2 ||
     file.size>16_384)throw Error('Untrusted local owner setting');
  return JSON.parse(readFileSync(path,'utf8'));
}
function oneFor(rows,profileId) {
  if(!Array.isArray(rows) || rows.length>100)return null;
  const list=rows.filter(x=>x && x.ProfileId===profileId);
  return list.length===1?list[0]:null;
}
export function readCodexWindowsOwnerSwitches({
  appDataHome,profileId,accountSha256,threadId,ownerDevice
}={}) {
  if(process.platform!=='win32' || typeof appDataHome!=='string' ||
     !UUID.test(profileId??'') || !UUID.test(threadId??'') ||
     !HASH.test(accountSha256??'') || !/^[A-Za-z0-9-]{1,48}$/.test(ownerDevice??''))
    return OFF;
  try{
    const root=resolve(appDataHome);
    if(!/^[A-Za-z]:\\/.test(root) ||
       lstatSync(root).isSymbolicLink() ||
       !lstatSync(root).isDirectory())
      return OFF;
    const activation=readLocalJson(root,'codex-chat-production-v1.json');
    if(!exactly(activation,['enabled','profileId','accountSha256',
          'threadId','ownerDevice','fiveHour','sevenDay']) ||
       activation.enabled!==true ||
       activation.profileId!==profileId ||
       activation.accountSha256!==accountSha256 ||
       activation.threadId!==threadId ||
       activation.ownerDevice!==ownerDevice ||
       typeof activation.fiveHour!=='boolean' ||
       typeof activation.sevenDay!=='boolean')
      return OFF;
    const draft=oneFor(
      readLocalJson(root,'dedicated-autostart-draft-v1.json'),profileId);
    if(!draft || draft.ProfileId!==profileId ||
       draft.AccountSha256!==accountSha256 ||
       draft.ExistingThreadId!==threadId ||
       draft.SchemaVersion!==1 ||
       draft.WantAutomatic!==true ||
       typeof draft.WantFiveHour!=='boolean' ||
       typeof draft.WantSevenDay!=='boolean')
      return OFF;
    const binding=oneFor(
      readLocalJson(root,'dedicated-existing-thread-bindings-v1.json'),
      profileId);
    if(!binding || binding.AccountSha256!==accountSha256 ||
       binding.ExistingThreadId!==threadId ||
       binding.ThreadStorage!=='desktop-default' ||
       binding.Message!=='안녕' ||
       binding.AutomaticMessagesEnabled!==true ||
       typeof binding.StartOnFiveHour!=='boolean' ||
       typeof binding.StartOnSevenDay!=='boolean')
      return OFF;
    return Object.freeze({
      master:true,
      fiveHour:activation.fiveHour && draft.WantFiveHour &&
        binding.StartOnFiveHour,
      sevenDay:activation.sevenDay && draft.WantSevenDay &&
        binding.StartOnSevenDay
    });
  }catch{return OFF;}
}
