/* Cascade normalization approval dialog (read-only proposals).
 * Section decisions are user-local and never sent to Git.mil.
 */
(function(){
'use strict';
const $=id=>document.getElementById(id);
const esc=x=>String(x==null?'':x).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const types=['THEME','EPIC','FEATURE','STORY','TASK','BUG'];
const sections=[
 {id:'identity',label:'Identity and work-item type',hint:'Use a human-readable title and a stable metadata ID.'},
 {id:'metadata',label:'Required metadata',hint:'Review the complete cascade-meta fence; values must match the identity and parent sections.'},
 {id:'content',label:'Description and acceptance criteria',hint:'Edit the human-readable body, testable outcomes and completion requirements.'},
 {id:'organization',label:'Organization and parent-child relationship',hint:'Choose a verified eligible parent or evaluate evidence-based suggestions. No fallback parent is ever assigned.'},
 {id:'labels',label:'GitLab issue labels',hint:'Review the actual issue-label list, including the active scope label.'}
];
const expectedParent={EPIC:'THEME',FEATURE:'EPIC',STORY:'FEATURE',TASK:'STORY'};
const state={visible:false,itemKey:'',review:null,drafts:new Map(),focusReturn:null,bodyOverflow:''};
const displayType=x=>String(x||'').toUpperCase()==='UNCLASSIFIED'?'Type not assigned':String(x||'Type not assigned');
const displayCode=x=>String(x)==='UNCLASSIFIED_TYPE'?'TYPE_NOT_ASSIGNED':String(x);
const stageName=s=>s==='APPROVED'?'Approved':s==='NEEDS_CHANGES'?'Needs changes':'Pending';
function parsedBlock(raw){
 const match=String(raw||'').match(/~~~cascade-meta\s*([\s\S]*?)~~~/i);
 const meta={};
 if(match){
  for(const line of match[1].split(/\r?\n/)){
   const m=line.match(/^\s*([a-z0-9_-]+)\s*:\s*(.*?)\s*$/i);
   if(m)meta[m[1].toLowerCase()]=m[2];
  }
 }
 return {meta,match};
}
function makeBlock(meta){
 const clean=Object.entries(meta).filter(([k])=>/^[a-z0-9_-]+$/i.test(k));
 return ['~~~cascade-meta',...clean.map(([k,v])=>k+': '+String(v||'').replace(/[\r\n]+/g,' ')),'~~~'].join('\n');
}
function newDraft(item){
 const desc=String(item.DraftDescription||'');
 const {meta,match}=parsedBlock(desc);
 const typed=types.includes(item.Type)?item.Type:'';
 const metaId=String(meta.id||'');
 const id=metaId||((item.StableId||'').startsWith('gitlab:')?'':item.StableId);
 const draft={
  sourceKey:String(item.Key),id:String(id||''),title:String(item.Title||''),type:typed,
  parentId:String(item.DeclaredParent||meta.parent||''),
  metadata:match?match[0].trim():makeBlock({type:typed,id,parent:item.DeclaredParent||'',state:'open'}),
  content:match?desc.replace(match[0],'').trim():desc.trim(),
  labels:Array.isArray(item.Labels)?item.Labels.map(String):[],
  notes:Object.fromEntries(sections.map(s=>[s.id,''])),
  decisions:Object.fromEntries(sections.map(s=>[s.id,'PENDING'])),
  errors:{}
 };
 return draft;
}
function getDraft(key){
 const item=state.review?.Items?.find(x=>x.Key===key);
 if(!item)return null;
 if(!state.drafts.has(key))state.drafts.set(key,newDraft(item));
 return state.drafts.get(key);
}
function item(){return state.review?.Items?.find(x=>x.Key===state.itemKey)||null}
function draft(){return getDraft(state.itemKey)}
function syncReservedMetadata(d){
 const parsed=parsedBlock(d.metadata),meta=parsed.meta;
 if(!Object.keys(meta).length)meta.state='open';
 meta.type=d.type;
 meta.id=d.id;
 meta.parent=d.parentId||'';
 d.metadata=makeBlock(meta);
}
function currentCatalog(d,it){
 const required=expectedParent[d.type];
 if(!required)return [];
 return (state.review?.ParentCatalog||[]).filter(x=>x.Type===required&&
  String(x.ProjectId)===String(it.ProjectId)&&
  x.Key!==it.Key&&x.Id!==d.id);
}
function formsCycle(d,it,parentId){
 const found=new Set();
 const catalog=state.review?.ParentCatalog||[];
 const byId=new Map(catalog.map(x=>[x.Id,x]));
 let cursor=parentId;
 for(let i=0;i<catalog.length+1;i++){
  if(!cursor)return false;
  if(cursor===d.id)return true;
  if(found.has(cursor))return true;
  found.add(cursor);
  const match=byId.get(cursor);
  if(!match)return false;
  const childDraft=state.drafts.get(match.Key);
  const source=state.review?.Items?.find(x=>x.Key===match.Key);
  cursor=childDraft?.parentId||source?.DeclaredParent||'';
 }
 return true;
}
function validate(section,d,it){
 if(section==='identity'){
  if(d.title.trim().length<4)return 'Enter a meaningful title before approving.';
  if(!types.includes(d.type))return 'Choose the work-item type; a heuristic default is not approved automatically.';
  if(!/^[A-Za-z0-9][A-Za-z0-9._:-]{2,}$/.test(d.id))return 'Enter a stable ID (at least three characters; letters, numbers and . _ : -).';
 }
 if(section==='metadata'){
  const p=parsedBlock(d.metadata);
  if(!p.match)return 'The cascade-meta fenced block is missing.';
  if(p.meta.type!==d.type||p.meta.id!==d.id||String(p.meta.parent||'')!==d.parentId)
   return 'The metadata type, ID or parent disagrees with the other review sections. Update or synchronize the metadata.';
  for(const key of it.RequiredMetadata||['type','id']){
   const value=String(p.meta[key]||'');
   if(!value.trim()||value.trim().toUpperCase()==='TBD')return 'Required metadata field "'+key+'" is missing or TBD.';
  }
 }
 if(section==='content'){
  if(d.content.trim().length<25)return 'Provide a substantive description before approving.';
  if(d.type==='STORY'&&!/acceptance\s+criteria/i.test(d.content))
   return 'A Story needs explicit acceptance criteria.';
  if(d.type==='FEATURE'&&!/(done.when|definition\s+of\s+done)/i.test(d.content))
   return 'A Feature needs its completion / Done-When criteria.';
 }
 if(section==='organization'){
  if(d.type==='THEME')return d.parentId?'A Theme is a root work item and must not have a parent.':'';
  if(!expectedParent[d.type])return 'The chosen work-item type has no supported automatic parent rule. Request an owner review.';
  if(!d.parentId)return 'Choose a parent, or mark Needs changes while the relationship is unresolved.';
  if(!currentCatalog(d,it).some(x=>x.Id===d.parentId))
   return 'The selected parent is not a unique, correctly typed item in the current project and label scope.';
  if(formsCycle(d,it,d.parentId))return 'That parent selection would create a circular relationship.';
 }
 if(section==='labels'){
  if(!d.labels.length)return 'At least one valid GitLab label is required for this sample review.';
  const scope=String(state.review?.Label||'');
  if(scope&&!d.labels.some(x=>x.toLowerCase()===scope.toLowerCase()))
   return 'Keep the selected GitLab scope label "'+scope+'" or review a different scope.';
 }
 return '';
}
function refreshTable(){window.CascadeIntelligenceWorkbench?.render()}
function change(key,value){
 const d=draft();if(!d)return false;
 const section={title:'identity',type:'identity',id:'identity',metadata:'metadata',content:'content',parentId:'organization',labels:'labels'}[key];
 if(!section)return false;
 if(key==='labels'){
  d.labels=String(value).split(/\r?\n/).map(x=>x.trim()).filter(Boolean).filter((v,i,a)=>a.findIndex(x=>x.toLowerCase()===v.toLowerCase())===i);
 }else{
  d[key]=String(value||'');
 }
 d.decisions[section]='PENDING';
 delete d.errors[section];
 if(['type','id','parentId'].includes(key)){
  syncReservedMetadata(d);
  d.decisions.metadata='PENDING';
  delete d.errors.metadata;
  if(key==='type'){d.decisions.organization='PENDING';delete d.errors.organization;}
 }
 updateProgress();refreshTable();
 const card=$('intelligence-dialog-sections')?.querySelector?.('[data-review-section="'+section+'"]');
 if(card){card.className='review-section review-pending';
  const badge=card.querySelector?.('.review-status');if(badge)badge.textContent='Pending';
  const error=card.querySelector?.('.review-error');if(error)error.textContent='';
 }
 return true;
}
function decide(section,decision){
 if(!sections.some(x=>x.id===section)||!['APPROVED','NEEDS_CHANGES','PENDING'].includes(decision))return false;
 const d=draft(),it=item();if(!d||!it)return false;
 if(decision==='APPROVED'){
  const err=validate(section,d,it);
  if(err){d.errors[section]=err;d.decisions[section]='PENDING';render();refreshTable();return false}
 }
 d.decisions[section]=decision;delete d.errors[section];render();refreshTable();return true;
}
function approveCount(d){return sections.filter(s=>d?.decisions?.[s.id]==='APPROVED').length}
function updateProgress(){
 const d=draft();if(!d)return;
 const yes=approveCount(d);
 $('intelligence-dialog-progress').textContent=yes+' / '+sections.length+' sections approved. '+
  sections.filter(s=>d.decisions[s.id]==='NEEDS_CHANGES').length+' sections need changes. All decisions are local.';
 $('intelligence-dialog-outcome').textContent=yes===sections.length?
  'All sections reviewed locally — export the package; Git.mil is unchanged.':
  'Review incomplete. No Git.mil changes; continue or export a partial review.';
}
function sectionActions(id){
 return '<div class="review-actions">'+
  '<button type="button" data-review-decide="'+id+'" data-review-decision="APPROVED" class="button review-approve">✓ Approve section</button>'+
  '<button type="button" data-review-decide="'+id+'" data-review-decision="NEEDS_CHANGES" class="button review-changes">Needs changes</button>'+
  '<button type="button" data-review-decide="'+id+'" data-review-decision="PENDING" class="button review-reset">Reset to pending</button></div>';
}
function suggestionsHTML(d,it){
 const parent=it.ParentReview;
 if(!d.type)return '<p class="notice">Select a work-item type before reviewing potential parents.</p>';
 if(!expectedParent[d.type])return '<p class="muted">No parent hierarchy is configured for this work-item type.</p>';
 const candidates=(parent?.Candidates||[]).filter(x=>x.ParentType===expectedParent[d.type]&&
   (!parent?.CandidateChildType||parent.CandidateChildType===d.type));
 if(!candidates.length)return '<p class="notice">No evidence-backed relationship candidates found. The previous CLI first-Feature fallback is deliberately disabled. Leave unresolved or choose an eligible existing parent with human verification.</p>';
 return '<div class="review-candidates">'+candidates.map(x=>
  '<div class="review-candidate"><div><strong>'+esc(x.ParentTitle)+'</strong>'+
  ' <span class="example-type-badge">'+esc(x.ParentType)+'</span> <code>'+esc(x.ParentId)+'</code>'+
  '<p>'+esc(x.Confidence)+' confidence · '+(x.Evidence||[]).map(esc).join('; ')+'</p></div>'+
  '<button type="button" data-review-choose-parent="'+esc(x.ParentId)+'" class="button">Choose as draft parent</button></div>').join('')+'</div>';
}
function drawSections(){
 const d=draft(),it=item(),host=$('intelligence-dialog-sections');if(!host||!d||!it)return;
 const typeOptions=['<option value="">Type not assigned</option>'].concat(types.map(t=>
  '<option value="'+t+'"'+(d.type===t?' selected':'')+'>'+t+'</option>')).join('');
 const parentChoices=currentCatalog(d,it);
 const parentList=['<option value="">Unresolved — no parent selected</option>'];
 if(d.parentId&&!parentChoices.some(x=>x.Id===d.parentId))
  parentList.push('<option value="'+esc(d.parentId)+'" selected>Existing declared parent: '+esc(d.parentId)+' (not verified in current scope)</option>');
 for(const x of parentChoices)parentList.push('<option value="'+esc(x.Id)+'"'+(x.Id===d.parentId?' selected':'')+'>'+
  esc(x.Title)+' · '+esc(x.Id)+'</option>');
 const stageBody={
  identity:'<div class="review-fields"><label>Issue title<input data-review-field="title" value="'+esc(d.title)+'"></label>'+
   '<label>Work-item type<select data-review-field="type">'+typeOptions+'</select></label>'+
   '<label>Stable metadata ID<input data-review-field="id" value="'+esc(d.id)+'" autocomplete="off"></label></div>'+
   '<p class="muted">Source: '+esc(displayType(it.Type))+' · Candidate from existing normalizer: '+esc(it.CandidateType)+' ('+esc(it.TypeEvidence)+'). Suggested type is never automatically approved.</p>',
  metadata:'<label>Complete editable cascade-meta block<textarea data-review-field="metadata" rows="9" spellcheck="false">'+esc(d.metadata)+'</textarea></label>'+
   '<p class="muted">Required metadata: '+esc((it.RequiredMetadata||[]).join(', ')||'type, id')+'. The ID, type, and parent must agree across sections.</p>',
  content:'<label>Editable description, Story acceptance criteria, Feature Done-When<textarea data-review-field="content" rows="10" spellcheck="true">'+esc(d.content)+'</textarea></label>'+
   '<p class="muted">Current known content findings: '+esc((it.DeficiencyCodes||[]).map(displayCode).join(' · ')||'none flagged')+'. Review is not automatic acceptance.</p>',
  organization:'<p><strong>Expected parent type:</strong> '+esc(expectedParent[d.type]||'None')+
   ' · <strong>Original parent:</strong> <code>'+esc(it.DeclaredParent||'(not specified)')+'</code>'+
   ' · <strong>Source resolution:</strong> '+esc(it.ParentReview?.DeclaredParentStatus||'Unknown')+'</p>'+
   '<label>Choose a review-only parent from current eligible work items<select data-review-field="parentId">'+parentList.join('')+'</select></label>'+
   '<h4>Evidence-based candidate relationships</h4>'+suggestionsHTML(d,it)+
   '<p class="muted">Only work items carrying this review label, with unique canonical IDs, expected parent type and matching Git.mil project, are offered. Selecting does not modify the source.</p>',
  labels:'<label>GitLab issue labels (one literal label per line)<textarea data-review-field="labels" rows="5">'+esc(d.labels.join('\n'))+'</textarea></label>'+
   '<p class="muted">GitLab labels are separate from cascade-meta area or hierarchy fields. Changing labels here makes a proposal only.</p>'
 };
 host.innerHTML=sections.map((sec,index)=>{
  const status=d.decisions[sec.id]||'PENDING';
  return '<section class="review-section review-'+(status==='APPROVED'?'approved':status==='NEEDS_CHANGES'?'needs-changes':'pending')+'" data-review-section="'+sec.id+'">'+
   '<div class="review-section-header"><div><span class="review-step">'+(index+1)+'</span><strong>'+sec.label+'</strong>'+
   '<small>'+sec.hint+'</small></div><span class="review-status">'+stageName(status)+'</span></div>'+
   '<div class="review-section-content">'+stageBody[sec.id]+
   '<label class="review-note-label">Reviewer note (optional)<textarea data-review-note="'+sec.id+'" rows="2" placeholder="Reason, open question or follow-up">'+esc(d.notes[sec.id]||'')+'</textarea></label>'+
   '<p class="review-error" role="alert">'+esc(d.errors[sec.id]||'')+'</p>'+sectionActions(sec.id)+'</div></section>';
 }).join('');
}
function render(){
 if(!state.visible)return;
 const it=item();if(!it)return;
 $('intelligence-dialog-title').textContent=it.Title||'Work item review';
 $('intelligence-dialog-subtitle').textContent='Project '+it.ProjectId+' · issue #'+it.Iid+' · '+displayType(it.Type)+' · explicit human review';
 drawSections();updateProgress();
}
function open(it,review){
 if(!it||!review)return false;
 state.review=review;state.itemKey=it.Key;getDraft(it.Key);
 state.focusReturn=document.activeElement||null;
 const backdrop=$('intelligence-dialog-backdrop');
 if(!backdrop)return false;
 state.visible=true;backdrop.hidden=false;
 if(document.body?.style){state.bodyOverflow=document.body.style.overflow||'';document.body.style.overflow='hidden'}
 render();
 $('intelligence-dialog')?.focus?.();
 return true;
}
function close(){
 if(!state.visible)return;
 state.visible=false;
 const backdrop=$('intelligence-dialog-backdrop');
 if(backdrop)backdrop.hidden=true;
 if(document.body?.style)document.body.style.overflow=state.bodyOverflow;
 state.focusReturn?.focus?.();
}
function reset(){
 close();state.itemKey='';state.review=null;state.drafts.clear();
}
function handleClick(e){
 const target=e.target,nearest=s=>target?.closest?.(s);
 const decision=nearest('[data-review-decide]');
 if(decision){decide(decision.dataset.reviewDecide,decision.dataset.reviewDecision);return}
 const parent=nearest('[data-review-choose-parent]');
 if(parent){change('parentId',parent.dataset.reviewChooseParent);render();return}
 if(target?.id==='intelligence-dialog-backdrop')close();
}
function handleInput(e){
 const target=e.target;if(!target)return;
 if(target.dataset?.reviewNote){
  const d=draft();if(d)d.notes[target.dataset.reviewNote]=String(target.value||'');return;
 }
 const field=target.dataset?.reviewField;
 if(!field)return;
 const value=String(target.value||'');
 change(field,value);
 if(field==='type'||field==='parentId'||(field==='id'&&e.type==='change')){render()}
}
function countFor(key){const d=state.drafts.get(key);return d?{approved:approveCount(d),total:sections.length,
 needsChanges:sections.filter(s=>d.decisions[s.id]==='NEEDS_CHANGES').length}:null}
function compileDescription(d){
 syncReservedMetadata(d);
 return d.metadata.trim()+'\n\n'+d.content.trim();
}
function packageFor(key){
 const d=getDraft(key),it=state.review?.Items?.find(x=>x.Key===key);if(!d||!it)return null;
 return {
  source:{projectId:it.ProjectId,iid:it.Iid,key:it.Key,url:it.Url,label:state.review.Label},
  mode:'LOCAL_REVIEW_ONLY',readyForWrite:false,
  decisions:{...d.decisions},reviewNotes:{...d.notes},
  parentReview:{
   originalParentId:it.DeclaredParent||'',
   selectedParentId:d.parentId,
   relationshipStatus:d.decisions.organization,
   candidateEvidence:(it.ParentReview?.Candidates||[]).filter(x=>x.ParentId===d.parentId).map(x=>({
    candidateParentId:x.ParentId,confidence:x.Confidence,reasons:(x.Evidence||[]).slice()
   })),
   manualSelection:!!d.parentId&&!(it.ParentReview?.Candidates||[]).some(x=>x.ParentId===d.parentId)
  },
  proposed:{title:d.title,type:d.type,stableId:d.id,parentId:d.parentId,
   labels:d.labels.slice(),metadata:d.metadata,description:compileDescription(d)},
  approvedSections:approveCount(d),requiredSections:sections.length,
  warning:'Local reviewer choices only; Git.mil has not been changed. A separate, authorized validation and write plan is needed.'
 };
}
function bundle(selectedOnly=false){
 const entries=(selectedOnly?[state.itemKey]:[...state.drafts.keys()])
  .map(packageFor).filter(Boolean);
 return {schema:'cascade.intelligence_decisions.v1',reviewSource:state.review?.Source||'imported Git.mil snapshot',
  label:state.review?.Label||'',exportedAtUtc:new Date().toISOString(),
  mode:'REVIEW_ONLY',items:entries,
  safety:'No GitLab issues were modified. This export may contain internal issue titles, labels and body text; review before sharing.'};
}
function saveBundle(selectedOnly=false){
 const value=bundle(selectedOnly);
 if(!value.items.length)return false;
 const file=new Blob([JSON.stringify(value,null,2)],{type:'application/json'});
 const url=URL.createObjectURL(file),a=document.createElement('a');
 a.href=url;a.download='cascade-reviewed-proposals-'+(selectedOnly?'single-':'')+new Date().toISOString().slice(0,10)+'.json';
 a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);return true;
}
function bind(){
 const backdrop=$('intelligence-dialog-backdrop');
 if(!backdrop||backdrop.dataset.bound==='yes')return;
 backdrop.dataset.bound='yes';
 backdrop.addEventListener('click',handleClick);
 backdrop.addEventListener('input',handleInput);
 backdrop.addEventListener('change',handleInput);
 $('intelligence-dialog-close').onclick=close;
 $('intelligence-dialog-done').onclick=close;
 $('intelligence-dialog-export').onclick=()=>saveBundle(true);
 document.addEventListener('keydown',e=>{
  if(!state.visible)return;
  if(e.key==='Escape'){e.preventDefault();close();}
  if(e.key!=='Tab')return;
  const elements=Array.from($('intelligence-dialog')?.querySelectorAll?.('button:not([disabled]), input, textarea, select, [tabindex="0"]')||[]);
  const focusables=elements.filter(x=>!x.disabled&&!x.hidden);
  if(!focusables.length)return;
  const first=focusables[0],last=focusables[focusables.length-1];
  if(e.shiftKey&&document.activeElement===first){e.preventDefault();last.focus()}
  else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first.focus()}
 });
}
const api=Object.freeze({bind,open,close,reset,render,getDraft,countFor,change,decide,compileDescription,bundle,saveBundle,displayType,displayCode,isOpen:()=>state.visible});
if(typeof window!=='undefined')window.CascadeReviewDialog=api;
if(typeof module!=='undefined'&&module.exports)module.exports=api;
})();
