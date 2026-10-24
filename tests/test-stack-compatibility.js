'use strict';
const vm=require('node:vm');
const fs=require('node:fs');
const path=require('node:path');
const assert=require('node:assert/strict');
const base=path.join(__dirname,'..');
const html=fs.readFileSync(path.join(base,'ui/index.html'),'utf8');
for(const required of ['id="stack-browser-run"','id="stack-rows"','id="intelligence-run"','id="leadership-kpis"'])
 assert.ok(html.includes(required),'Missing GUI stack/intelligence/leadership control');
const nodes=new Map();
const elt=id=>{
 if(!nodes.has(id))nodes.set(id,{id,disabled:false,textContent:'',innerHTML:'',style:{},click(){}});
 return nodes.get(id);
};
const document={
 getElementById:elt,
 createElement(tag){return tag==='script'?{noModule:true}:{href:'',download:'',click(){}}},
};
const w={};
const ctx={window:w,document,navigator:{storage:{}},WebAssembly,Uint8Array,Promise,Map,Set,Date,Math,Blob,URL,setTimeout,clearTimeout,console,crypto:{subtle:{}},Event,RegExp,Number,String,Array,Object,JSON};
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(path.join(base,'ui/stack-compatibility.js'),'utf8'),ctx,{filename:'stack-compatibility.js'});
(async()=>{
 const result=await w.CascadeStackCheck.run();
 assert.equal(result.schema,'cascade.stack.browser.v1');
 const wasm=result.checks.find(x=>x.Component==='WebAssembly VM');
 assert.equal(wasm.Status,'PASS','real browser-compatible WebAssembly addition must return 42');
 const workers=result.checks.find(x=>x.Component==='Web Worker');
 assert.equal(workers.Status,'UNAVAILABLE','offline Node has no browser Worker');
 assert.ok(result.checks.some(x=>x.Component==='Origin Private File System'));
 assert.match(elt('stack-rows').innerHTML,/WebAssembly VM/);
 assert.ok(!JSON.stringify(result).includes('GITLAB_TOKEN'));
 console.log('PASS: real WASM smoke function, explicit unavailable capabilities and safe JSON reporting.');
})().catch(e=>{console.error(e);process.exitCode=1});
