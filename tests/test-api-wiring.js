// Browser wiring checks: actual Git.mil label selection, not inferred team membership.
'use strict';
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const base=path.resolve(__dirname,'..');
const html=fs.readFileSync(path.join(base,'ui/index.html'),'utf8');
for(const id of ['arc-open','check-gitmil','fetch-projects','gitmil-sync','test-genai','test-grok','integration-refresh','issue-label','issue-label-options','label-apply','label-clear','label-discover','label-summary','intelligence-run','intelligence-export','intelligence-summary','leadership-kpis','leadership-export','stack-browser-run'])
 assert.ok(html.includes('id="'+id+'"'),'missing real control '+id);
assert.ok(!html.includes('id="workspace-scope"'),'legacy inferred team workspace selector must be removed');
for(const id of ['gitmil-sync-feedback','gitmil-sync-label','gitmil-sync-stage','gitmil-sync-inline-spinner'])
 assert.ok(html.includes('id="'+id+'"'),'sync status element missing: '+id);
assert.match(fs.readFileSync(path.join(base,'ui/app.css'),'utf8'),/prefers-reduced-motion/,'spinner should respect reduced-motion preferences');
class Element{
 constructor(id){this.id=id;this.value='';this.disabled=false;this.checked=false;this.innerHTML='';this.textContent='';this.style={};this.dataset={};this.classList={add(){},remove(){},toggle(){},contains(){return false}}}
 addEventListener(){}
 querySelectorAll(){return []}
 setAttribute(){}
 appendChild(){}
 click(){if(this.onclick)return this.onclick()}
}
const els=new Map(),elt=id=>{if(!els.has(id))els.set(id,new Element(id));return els.get(id)};
elt('project-choice').value='16629';
const calls=[];
const reply=(data,status=200)=>({ok:status>=200&&status<300,status,json:async()=>data});
const fakeFetch=async(url,opts={})=>{
 calls.push([url,opts.method||'GET',opts.body,opts.headers||{}]);
 if(url==='/api/status')return reply({latestPlan:false});
 if(url==='/api/config')return reply({sinceDays:30,gitLabArcItem:'gitmil',issueLabel:''});
 if(url==='/api/issue-label')return reply({issueLabel:JSON.parse(opts.body).issueLabel,requiresSync:true});
 if(url==='/api/gitmil/labels')return reply({Labels:['team::Data-Crucible','team::Other'],ProjectErrors:[]});
 if(url==='/api/integrations')return reply({arcInstalled:true,arcLoaded:false,vaultUnlocked:false,integrations:[]});
 if(url==='/api/arc/open')return reply({launched:true});
 if(url==='/api/gitmil/status')return reply({connected:true,user:'approved-user',host:'web.git.mil',arcLoaded:true});
 if(url==='/api/gitmil/projects')return reply({projects:[{id:16629,name:'Shared CBR'}]});
 if(url==='/api/provider/test')return reply({connected:true,profile:JSON.parse(opts.body).profile,model:'mock-test-model',responseReceived:true});
 if(url==='/api/intelligence/preview')return reply({Mode:'READ_ONLY',Summary:{ImportedIssues:3,LabelMatchingIssues:1,AnalyzedIssues:1,RequiresReview:1,NoMetadata:1,UnclassifiedType:1,MissingDeclaredParent:0},Items:[{Key:'gitlab:16629:1',ProjectId:16629,Iid:1,Title:'Synthetic unlabeled item',Type:'UNCLASSIFIED',CandidateType:'STORY',TypeEvidence:'heuristic / review required',StableId:'gitlab:16629:1',MissingMetadata:['type','id'],DeficiencyCodes:['missing_cascade_metadata'],RequiresReview:true,ReviewState:'NEEDS_HUMAN_REVIEW',DraftDescription:'Synthetic only',DraftWarning:'Review required'}]});
 return reply({error:'unexpected '+url},404);
};
const document={getElementById:elt,querySelectorAll(sel){return sel==='.project-choice:checked'?[elt('project-choice')]:[]},addEventListener(){},createElement(){return new Element('new')}};
const handlers={};
const w={addEventListener:(k,fn)=>handlers[k]=fn,dispatchEvent(){},fetch:fakeFetch,location:{},setTimeout,clearTimeout};
const ctx={window:w,document,fetch:(...args)=>w.fetch(...args),navigator:{clipboard:{writeText:async()=>{}}},console,Blob,URL,Date,Map,Set,JSON,Number,Boolean,String,Math,Array,Error,RegExp,AbortSignal,Event,CustomEvent,performance,Promise,setTimeout,clearTimeout};
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(path.join(base,'ui/intelligence-review-dialog.js'),'utf8'),ctx,{filename:'intelligence-review-dialog.js'});
vm.runInContext(fs.readFileSync(path.join(base,'ui/intelligence-workbench.js'),'utf8'),ctx,{filename:'intelligence-workbench.js'});
vm.runInContext(fs.readFileSync(path.join(base,'ui/app.js'),'utf8'),ctx,{filename:'app.js'});
(async()=>{
 await elt('arc-open').click();
 await elt('check-gitmil').click();
 await elt('fetch-projects').click();
 await elt('test-genai').click();
 await elt('test-grok').click();
 await elt('label-discover').click();
 assert.ok(elt('issue-label-options').innerHTML.includes('team::Data-Crucible'),'actual Git.mil label names should populate list');
 elt('issue-label').value='team::Data-Crucible';
 await elt('label-apply').click();
 assert.ok(calls.some(c=>c[0]==='/api/issue-label'&&c[1]==='POST'&&JSON.parse(c[2]).issueLabel==='team::Data-Crucible'&&c[3]['X-Cascade-Intent']==='change-label'),'label must be applied and saved through backend');
 assert.match(elt('data-status').textContent,/sync required/);
 assert.match(elt('label-summary').textContent,/team::Data-Crucible/);
 await elt('label-clear').click();
 assert.ok(calls.some(c=>c[0]==='/api/issue-label'&&JSON.parse(c[2]).issueLabel===''),'clearing restores all issue labels');
 for(const route of ['/api/arc/open','/api/gitmil/status','/api/gitmil/projects','/api/gitmil/labels','/api/provider/test'])
  assert.ok(calls.some(c=>c[0]===route),'missing backend API wiring '+route);
 await elt('intelligence-run').click();
 assert.ok(calls.some(c=>c[0]==='/api/intelligence/preview'&&c[1]==='POST'&&c[3]['X-Cascade-Intent']==='preview-intelligence'),'Read-only intelligence route must require intent header.');
 assert.match(elt('intelligence-rows').innerHTML,/missing_cascade_metadata/);
 assert.equal(calls.filter(c=>c[0]==='/api/provider/test').length,2);
 assert.equal(w.CascadeNexusBridge.getPlan(),null,'no demo or stale scoped plan may load by default');
 // Keep a synthetic sync request unresolved to verify the spinner is visible while waiting,
 // and always cleared when the backend responds with an error.
 const priorFetch=w.fetch;let finishSync;
 w.fetch=async(url,opts)=>url==='/api/gitmil/sync'?
  await new Promise(resolve=>{finishSync=resolve}):priorFetch(url,opts);
 const pendingSync=elt('gitmil-sync').click();
 assert.equal(elt('gitmil-sync').disabled,true,'sync button must be disabled while waiting');
 assert.equal(elt('gitmil-sync-feedback').hidden,false,'sync status must be visible while waiting');
 assert.equal(elt('gitmil-sync-inline-spinner').hidden,false,'inline spinner must be visible while waiting');
 assert.match(elt('gitmil-sync-label').textContent,/Synchronizing Git.mil/);
 finishSync(reply({error:'synthetic Git.mil connection failure'},503));
 await pendingSync;
 assert.equal(elt('gitmil-sync').disabled,false,'sync button must re-enable after failure');
 assert.equal(elt('gitmil-sync-feedback').hidden,true,'sync spinner must stop after failure');
 assert.equal(elt('gitmil-sync-inline-spinner').hidden,true,'inline spinner must stop after failure');
 assert.equal(elt('gitmil-sync-label').textContent,'Synchronize selected projects');
 console.log('PASS: real ARC/Git.mil/AI controls, GitLab label discovery, exact label apply/clear and no inferred workspaces');
})().catch(e=>{console.error(e);process.exitCode=1});
