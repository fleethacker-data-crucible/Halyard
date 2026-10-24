/* Local-browser compatibility checks, no external network and no installs.
 * Captures observations, not an authorization decision by workplace IT.
 */
(function(){
'use strict';
const $=id=>document.getElementById(id),safe=s=>String(s==null?'':s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
let records=[],lastRun='';
function add(component,status,detail){records.push({Component:component,Status:status,Detail:String(detail||'').slice(0,360)})}
function render(){
 const box=$('stack-rows');if(!box)return;
 box.innerHTML=records.map(x=>'<tr><td>'+safe(x.Component)+'</td><td><span class="tag '+(x.Status==='PASS'?'ok':x.Status==='BLOCKED'?'warn':'')+'">'+safe(x.Status)+'</span></td><td>'+safe(x.Detail)+'</td></tr>').join('')||'<tr><td colspan="3">Not tested.</td></tr>';
 $('stack-summary').textContent=records.length+' checks recorded · '+records.filter(x=>x.Status==='PASS').length+' passed · '+records.filter(x=>x.Status==='BLOCKED').length+' blocked/unavailable. Runtime capability is not authorization to install or deploy.';
}
async function workerProbe(){
 if(typeof Worker==='undefined')return add('Web Worker','UNAVAILABLE','Worker constructor is not exposed in this browser context.');
 try{
  const result=await new Promise((resolve,reject)=>{
   let done=false,worker;
   const timer=setTimeout(()=>{if(!done){done=true;worker?.terminate();reject(Error('Worker response exceeded 4 seconds'))}},4000);
   try{
    worker=new Worker('stack-worker.js');
    worker.onmessage=e=>{if(done)return;done=true;clearTimeout(timer);worker.terminate();resolve(e.data)};
    worker.onerror=()=>{if(done)return;done=true;clearTimeout(timer);worker.terminate();reject(Error('Worker loading/execution blocked'))};
    worker.postMessage({value:21});
   }catch(e){clearTimeout(timer);worker?.terminate();reject(e)}
  });
  add('Web Worker',result?.ok&&result?.answer===42?'PASS':'BLOCKED',result?.ok?'Dedicated worker executed the bundled, local JavaScript fixture.':'Worker returned an unexpected result.');
 }catch(e){add('Web Worker','BLOCKED',e.name||e.message)}
}
async function wasmProbe(){
 if(typeof WebAssembly==='undefined')return add('WebAssembly VM','UNAVAILABLE','WebAssembly runtime is not exposed.');
 try{
  // The smallest standalone WASM module exporting an i32 add(a,b) function.
  // This proves WASM execution, NOT that rustc or native Rust .exe is allowed.
  const bytes=new Uint8Array([0,97,115,109,1,0,0,0,1,7,1,96,2,127,127,1,127,3,2,1,0,7,7,1,3,97,100,100,0,0,10,9,1,7,0,32,0,32,1,106,11]);
  const module=await WebAssembly.instantiate(bytes,{});
  const actual=module.instance.exports.add(19,23);
  add('WebAssembly VM',actual===42?'PASS':'BLOCKED',actual===42?'Local WASM module executed in browser; native Rust binaries and Rust toolchain are untested.':'Unexpected WASM calculation result.');
 }catch(e){add('WebAssembly VM','BLOCKED',e.name||e.message)}
}
async function indexedDbProbe(){
 if(typeof indexedDB==='undefined')return add('IndexedDB','UNAVAILABLE','No IndexedDB implementation is exposed.');
 const name='cascade-capability-check-'+Math.floor(Math.random()*1000000);
 try{
  await new Promise((resolve,reject)=>{
   const req=indexedDB.open(name,1);
   req.onupgradeneeded=()=>req.result.createObjectStore('probes');
   req.onsuccess=()=>{const db=req.result;db.close();resolve()};
   req.onerror=()=>reject(Error('IndexedDB open blocked'));
  });
  add('IndexedDB','PASS','Temporary local database created and closed; no issue data or credentials stored.');
 }catch(e){add('IndexedDB','BLOCKED',e.name||e.message)}
 finally{try{indexedDB.deleteDatabase(name)}catch{}}
}
async function opfsProbe(){
 if(!navigator?.storage?.getDirectory)return add('Origin Private File System','UNAVAILABLE','OPFS not exposed. SQLite-in-browser persistence requires further testing.');
 const name='cascade-capability-'+Math.floor(Math.random()*1000000);
 try{
  const root=await navigator.storage.getDirectory();
  const handle=await root.getFileHandle(name,{create:true});
  const writer=await handle.createWritable();
  await writer.write('CASCADE-STACK-PROBE');
  await writer.close();
  const file=await handle.getFile();
  const text=await file.text();
  await root.removeEntry(name);
  add('Origin Private File System',text==='CASCADE-STACK-PROBE'?'PASS':'BLOCKED','Temporary local file written/read/removed; no Git.mil data used. SQLite WASM driver still must be separately validated.');
 }catch(e){add('Origin Private File System','BLOCKED',e.name||e.message)}
}
async function run(){
 records=[];lastRun=new Date().toISOString();
 const btn=$('stack-browser-run');if(btn)btn.disabled=true;
 $('stack-summary').textContent='Running local browser checks…';
 try{
  add('Modern JavaScript',typeof Promise==='function'&&typeof Map==='function'?'PASS':'BLOCKED','Promise, Map and static browser scripts are available; Svelte and TypeScript builds must be compiled before deployment.');
  add('ES Modules','noModule' in document.createElement('script')?'PASS':'UNAVAILABLE','Browser module parser capability; no Node.js or Vite installation at runtime.');
  await wasmProbe();await workerProbe();await indexedDbProbe();await opfsProbe();
  add('WebCrypto',!!(globalThis.crypto?.subtle)?'PASS':'UNAVAILABLE','WebCrypto API exposed. This does not unlock ARC or replace CAC/TOTP.');
  add('Native Rust executable','NOT TESTED','Browser tests cannot prove .exe execution is permitted. Use the PowerShell host test to report tooling, not to bypass policy.');
  add('Svelte + TypeScript runtime','NOT DEPLOYED','Target frontend build will be committed as static assets only after compatibility acceptance.');
  add('SQLite managed provider','HOST TEST','Use scripts/Test-CascadeStack.ps1 to probe available .NET providers on the approved workstation.');
 }finally{if(btn)btn.disabled=false;render()}
 return getResult();
}
function getResult(){return {schema:'cascade.stack.browser.v1',checkedAtUtc:lastRun,checks:records.slice(),warning:'Read-only capability observations. Do not infer IT authorization, deployment approval, or native Rust execution.'}}
function exportResults(){
 if(!records.length)return false;
 const blob=new Blob([JSON.stringify(getResult(),null,2)],{type:'application/json'});
 const url=URL.createObjectURL(blob),a=document.createElement('a');
 a.href=url;a.download='cascade-browser-stack-check-'+new Date().toISOString().slice(0,10)+'.json';
 a.click();setTimeout(()=>URL.revokeObjectURL(url),1500);return true;
}
const api=Object.freeze({run,render,getResult,exportResults});
if(typeof window!=='undefined')window.CascadeStackCheck=api;
if(typeof module!=='undefined'&&module.exports)module.exports=api;
})();
