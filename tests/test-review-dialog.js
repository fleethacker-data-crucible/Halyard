/* Modal approval workflow and suggested parent association tests. No browser or Git.mil calls. */
'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.resolve(__dirname,'..');
const html=fs.readFileSync(path.join(root,'ui/index.html'),'utf8');
for(const x of ['intelligence-dialog-backdrop','intelligence-dialog-sections','intelligence-dialog-progress','intelligence-dialog-close']){
 assert.ok(html.includes('id="'+x+'"'),'Missing modal control '+x);
}
assert.ok(html.includes('<th>Recorded work-item type</th>'),'The visible table heading must identify work-item type');
assert.ok(!html.includes('<th>Declared type</th>'),'Avoid ambiguous Declared type heading');
const appJs=fs.readFileSync(path.join(root,'ui/app.js'),'utf8');
assert.match(appJs,/with no assigned work-item type/,'Hierarchy totals should avoid classification terminology');
assert.match(appJs,/TYPE_NOT_ASSIGNED/,'Topology must present a neutral finding label');
const leadershipJs=fs.readFileSync(path.join(root,'ui/leadership-dashboard.js'),'utf8');
assert.match(leadershipJs,/Work items with assigned types/,'Leadership metrics need neutral type language');
class Node {
 constructor(id){this.id=id;this.innerHTML='';this.textContent='';this.value='';this.style={};this.dataset={};this.hidden=true;this.disabled=false;this.focus=()=>{document.activeElement=this};}
 querySelector(){return null}
 querySelectorAll(){return []}
 addEventListener(){}
 click(){if(this.onclick)this.onclick()}
}
const nodes=new Map(),$=id=>{if(!nodes.has(id))nodes.set(id,new Node(id));return nodes.get(id)};
const document={getElementById:$,body:{style:{overflow:''}},activeElement:null,addEventListener(){},createElement(){return new Node('created')}};
const window={};
const ctx={document,window,console,Map,Set,Array,JSON,Math,RegExp,Number,Boolean,String,Object,Date,Promise,Blob,URL,setTimeout,clearTimeout,AbortSignal};
vm.createContext(ctx);
for(const file of ['intelligence-review-dialog.js','intelligence-workbench.js']){
 vm.runInContext(fs.readFileSync(path.join(root,'ui',file),'utf8'),ctx,{filename:file});
}
const dialog=window.CascadeReviewDialog,bench=window.CascadeIntelligenceWorkbench;
const source={
 Key:'gitlab:16629:41',ProjectId:16629,Iid:41,
 Title:'Batch ingestion retries <script>alert(1)</script>',Url:'https://web.git.mil/team/issues/41',
 Type:'UNCLASSIFIED',CandidateType:'STORY',TypeEvidence:'heuristic / review required',
 StableId:'gitlab:16629:41',DeclaredParent:'',
 MissingMetadata:['type','id','parent','points'],RequiredMetadata:['type','id','parent','theme','state','area','points','sprint'],
 DeficiencyCodes:['UNCLASSIFIED_TYPE','PARENT_NOT_DECLARED'],RequiresReview:true,
 ReviewState:'NEEDS_HUMAN_REVIEW',Labels:['team::Data-Crucible','component::batch'],
 DraftDescription:'~~~cascade-meta\n'+
  'type: STORY\nid: STORY-batch-retries\nparent:\ntheme: THEME-data\nstate: open\narea: batch\npoints: 3\nsprint: TEST-S1\n~~~\n'+
  '## Acceptance Criteria\n- Given a retryable sample batch, when restarted, then no duplicate records are created.',
 DraftWarning:'Local proposal only.',
 ParentReview:{
  Key:'gitlab:16629:41',ChildType:'STORY',ExpectedParentType:'FEATURE',DeclaredParent:'',
  DeclaredParentStatus:'NOT_SET',Candidates:[
   {ParentId:'FEATURE-batch-ingestion',ParentKey:'gitlab:16629:11',ParentTitle:'Batch ingestion validation',ParentType:'FEATURE',
    ProjectId:16629,Score:65,Confidence:'MODERATE',Evidence:['Shared specific title term: ingestion'],Status:'SUGGESTION_ONLY'}
  ]
 }
};
const review={Mode:'READ_ONLY',Label:'team::Data-Crucible',Source:'Synthetic snapshot',
 Summary:{AnalyzedIssues:1,LabelMatchingIssues:1,RequiresReview:1,NoMetadata:1,UnclassifiedType:1,MissingDeclaredParent:1},
 RelationshipSummary:{WithCandidateEvidence:1,MissingParentWithoutCandidates:0},
 ParentCatalog:[{Key:'gitlab:16629:11',Id:'FEATURE-batch-ingestion',Type:'FEATURE',Title:'Batch ingestion validation',ProjectId:16629}],
 Items:[source]};
bench.setReview(review);
assert.match($('intelligence-rows').innerHTML,/Type not assigned/);
assert.ok(!$('intelligence-rows').innerHTML.includes('>Unclassified<'));
assert.match($('intelligence-rows').innerHTML,/TYPE_NOT_ASSIGNED/,'Neutral user-facing finding code required');
assert.equal(bench.openItem(source.Key),true,'Row must open dialog');
assert.equal(dialog.isOpen(),true);
assert.equal($('intelligence-dialog-backdrop').hidden,false);
assert.doesNotMatch($('intelligence-dialog-sections').innerHTML,/Choose as draft parent/,'No hierarchy proposal before reviewer chooses a work-item type');
assert.ok(!$('intelligence-dialog-sections').innerHTML.includes('<script>alert'),'Issue title must be HTML escaped');
const draft=dialog.getDraft(source.Key);
assert.equal(draft.type,'','Unverified heuristic type must not auto-become approved work-item type');
assert.equal(dialog.decide('identity','APPROVED'),false,'Explicit work-item type is required');
assert.equal(dialog.decide('organization','APPROVED'),false,'Missing parent cannot silently be approved');
dialog.change('type','STORY');
dialog.render();
assert.match($('intelligence-dialog-sections').innerHTML,/FEATURE-batch-ingestion/);
assert.match($('intelligence-dialog-sections').innerHTML,/Choose as draft parent/);
dialog.change('id','STORY-batch-retries');
assert.equal(dialog.decide('identity','APPROVED'),true);
assert.equal(dialog.countFor(source.Key).approved,1);
dialog.change('title','Batch retry behavior');
assert.equal(dialog.getDraft(source.Key).decisions.identity,'PENDING','Editing invalidates prior approval');
assert.equal(dialog.decide('identity','APPROVED'),true);
dialog.change('parentId','FEATURE-batch-ingestion');
assert.equal(dialog.decide('organization','APPROVED'),true,'Real eligible parent can be approved locally');
dialog.change('labels','team::Other');
assert.equal(dialog.decide('labels','APPROVED'),false,'Active GitLab scope label must not silently disappear');
dialog.change('labels','team::Data-Crucible\ncomponent::batch');
assert.equal(dialog.decide('labels','APPROVED'),true);
dialog.change('content','## Acceptance Criteria\n- Given a batch in a synthetic test environment, when it retries, then no duplicates are produced.');
assert.equal(dialog.decide('content','APPROVED'),true);
assert.equal(dialog.decide('metadata','APPROVED'),true,'Metadata should be synchronized to reviewed type, ID and parent');
assert.equal(dialog.countFor(source.Key).approved,5);
assert.match($('intelligence-rows').innerHTML,/5 \/ 5 sections approved/,'Queue progress must refresh');
const local=dialog.bundle(true);
assert.equal(local.items.length,1);
assert.equal(local.mode,'REVIEW_ONLY');
assert.equal(local.items[0].mode,'LOCAL_REVIEW_ONLY');
assert.equal(local.items[0].readyForWrite,false);
assert.equal(local.items[0].decisions.organization,'APPROVED');
assert.equal(local.items[0].parentReview.selectedParentId,'FEATURE-batch-ingestion');
assert.equal(local.items[0].parentReview.candidateEvidence[0].confidence,'MODERATE');
assert.equal(local.items[0].parentReview.manualSelection,false);
assert.match(local.items[0].proposed.description,/parent: FEATURE-batch-ingestion/);
dialog.change('parentId','');
assert.equal(dialog.getDraft(source.Key).decisions.organization,'PENDING','Changing approved relationship must reset organization');
assert.equal(dialog.getDraft(source.Key).decisions.metadata,'PENDING','Changing parent must also reset metadata approval');
dialog.decide('organization','NEEDS_CHANGES');
assert.equal(dialog.countFor(source.Key).needsChanges,1);
dialog.close();
assert.equal(dialog.isOpen(),false);
assert.equal($('intelligence-dialog-backdrop').hidden,true);
assert.equal(dialog.open(source,review),true);
assert.equal(dialog.getDraft(source.Key).decisions.organization,'NEEDS_CHANGES','Decisions persist on reopen within current review');
dialog.reset();
assert.equal(dialog.isOpen(),false);
assert.equal(dialog.getDraft(source.Key),null,'A cleared preview cannot retain old approval decisions');
console.log('PASS: modal opens from long list, neutral terminology, metadata and parent review, section approval/reset, local export and no writes.');
