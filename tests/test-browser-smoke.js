/* Offline Node smoke test: deliberately tiny DOM, no dependency installation. */
'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const {EventEmitter}=require('node:events');
const root=path.resolve(__dirname,'..');
const html=fs.readFileSync(path.join(root,'ui/index.html'),'utf8');
assert.match(html,/<script src="app\.js"><\/script><script src="nexus\.js"><\/script>/);
class Element {
  constructor(id){this.id=id;this.dataset={};this.style={};this.value='';this.checked=false;this.disabled=false;this.innerHTML='';this.textContent='';this.hidden=false;this.classList={add(){},remove(){},toggle(){},contains(){return false}};}
  addEventListener(){}
  querySelectorAll(){return []}
  appendChild(){}
  setAttribute(){}
  click(){if(this.onclick)this.onclick()}
}
const elements=new Map();
const $=id=>{if(!elements.has(id))elements.set(id,new Element(id));return elements.get(id)};
const bus=new EventEmitter();
const document={getElementById:$,querySelectorAll(){return []},addEventListener(){},createElement(){return new Element('created')}};
const w={addEventListener:(name,fn)=>bus.on(name,fn),dispatchEvent:event=>bus.emit(event.type,event),fetch:async()=>{throw Error('offline')},location:{},setTimeout,clearTimeout};
const ctx={window:w,document,console,Blob,URL,Date,Map,Set,JSON,Number,Boolean,String,Math,Array,Error,RegExp,AbortSignal,Event,CustomEvent,performance,Promise,setTimeout,clearTimeout,navigator:{clipboard:{writeText:async()=>{}}}};
vm.createContext(ctx);
for(const name of ['example-work-items.js','example-explorer.js','leadership-dashboard.js','intelligence-review-dialog.js','intelligence-workbench.js','stack-compatibility.js','app.js']){
 vm.runInContext(fs.readFileSync(path.join(root,'ui',name),'utf8'),ctx,{filename:name});
}
assert.equal(typeof w.CascadeNexusBridge.getPlan,'function','Bridge available');
vm.runInContext(fs.readFileSync(path.join(root,'ui/nexus.js'),'utf8'),ctx,{filename:'nexus.js'});
assert.equal($('nexus-count').textContent,'0 / 0 items');
// dispatch real UI's demo action; should render Nexus grid and graph without leaking lexical variables
$('demo-btn').click();
assert.equal(w.CascadeNexusBridge.getPlan().RunId,'DEMO-NOT-REAL-DATA');
assert.equal($('nexus-count').textContent,'6 / 6 items');
assert.match($('nexus-rows').innerHTML,/JWT authentication/);
assert.match($('graph-stage').innerHTML,/graph-node/);
assert.match($('timeline-stage').innerHTML,/2026-10-20/);
// String is HTML-escaped, and no Git.mil URL is navigable unless from approved host.
assert.equal(w.CascadeNexusBridge.getPlan().Hierarchy.length,6);
const planBeforeExample=w.CascadeNexusBridge.getPlan();
assert.equal(typeof w.CascadeExampleBridge.getCatalog,'function','isolated sample bridge available');
w.CascadeExampleBridge.render();
assert.match($('example-summary').textContent,/46 synthetic work items/);
assert.match($('example-tree').innerHTML,/Data-Crucible Demonstration Portfolio/);
assert.match($('example-tree').innerHTML,/data-example-toggle/,'Tree must have working collapse controls');
assert.ok(!$('example-tree').innerHTML.includes('THEME ::'),'Redundant type prefixes must not appear in titles.');
assert.match($('example-rows').innerHTML,/gitlab-scoped-label/,'GitLab scoped labels should have chip-style rendering.');
const firstStory=w.CascadeExampleBridge.getCatalog().workItems.find(x=>x.type==='STORY');
w.CascadeExampleBridge.select(firstStory.id);
assert.match($('example-detail').innerHTML,/cascade-meta/);
assert.match($('example-detail').innerHTML,/Acceptance Criteria/);
assert.match($('example-detail').innerHTML,/data-example-parent/,'Parent ID must be clickable');
assert.ok(w.CascadeExampleExplorer.select(firstStory.parentId),'Clicking a parent ID should select the actual parent');
assert.match($('example-detail').innerHTML,new RegExp(firstStory.parentId));
w.CascadeExampleExplorer.collapseAll();
assert.equal(w.CascadeExampleExplorer.getExpandedIds().length,0);
w.CascadeExampleExplorer.expandAll();
assert.equal(w.CascadeExampleExplorer.getExpandedIds().length,46);
w.CascadeExampleExplorer.select(firstStory.id);
assert.match($('leadership-kpis').innerHTML,/Verified completions/,'Leadership must display evidence limitations');

assert.equal(w.CascadeNexusBridge.getPlan(),planBeforeExample,'Viewing example must NEVER replace real plan');
$('example-type').value='EPIC';
$('example-type').onchange();
assert.match($('example-summary').textContent,/3 epics/);
assert.ok(!$('example-rows').innerHTML.includes('type::task'),'Example type filter must omit tasks');
console.log('PASS: sample-only navigation, 46-item table, complete metadata/body, type filtering and no real Git.mil data replacement.');
// The same UI accepts read-only topology findings from a normal imported plan.
(async()=>{
 const original=w.CascadeNexusBridge.getPlan();
 const audited={...original,TopologyAudit:{
  Mode:'READ_ONLY',IssueLabel:'team::Data-Crucible',
  Summary:{VisibleIssueCount:2,SourceIssueCount:5,ClassifiedIssueCount:1,AffectedIssueCount:2,ErrorCount:1,WarningCount:1,InfoCount:0},
  Findings:[
   {Key:'gitlab:16629:1',Iid:1,ProjectId:16629,Title:'Legacy <work>',Type:'STORY',Url:'https://web.git.mil/safe/-/issues/1',Severity:'ERROR',Code:'SELF_PARENT',Explanation:'Self-parent link.'},
   {Key:'gitlab:16629:2',Iid:2,ProjectId:16629,Title:'Other issue',Type:'FEATURE',Url:'https://evil.invalid/test',Severity:'WARNING',Code:'PARENT_NOT_DECLARED',Explanation:'Parent not declared.'}
  ]
 }};
 await $('file-input').onchange({target:{files:[{text:async()=>JSON.stringify(audited)}],value:'test'}});
 assert.match($('topology-summary').textContent,/2 issues in selected label/);
 assert.match($('topology-rows').innerHTML,/SELF_PARENT/);
 assert.match($('topology-rows').innerHTML,/&lt;work&gt;/,'issue titles must be HTML-escaped');
 assert.ok(!$('topology-rows').innerHTML.includes('href="https://evil.invalid'), 'unapproved GitLab link must not be clickable');
 $('topology-filter').value='ERROR';
 $('topology-filter').onchange();
 assert.match($('topology-rows').innerHTML,/SELF_PARENT/);
 assert.ok(!$('topology-rows').innerHTML.includes('PARENT_NOT_DECLARED'),'severity filter must hide unrelated warnings');
 console.log('PASS: Phase 2A topology table, severity filter, HTML escaping and approved Git.mil links');
})().catch(e=>{console.error(e);process.exitCode=1});

