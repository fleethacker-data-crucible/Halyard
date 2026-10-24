/* Cascade read-only normalization workbench.
 * The detailed review opens immediately in an accessible dialog instead of
 * appearing after a 200-item table. No Git.mil writes or AI side effects.
 */
(function(){
'use strict';
const $=id=>document.getElementById(id);
const safe=x=>String(x==null?'':x).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const arr=x=>Array.isArray(x)?x:x==null?[]:[x];
const typeName=x=>window.CascadeReviewDialog?.displayType(x)||(String(x)==='UNCLASSIFIED'?'Type not assigned':String(x||'Type not assigned'));
const codeName=x=>window.CascadeReviewDialog?.displayCode(x)||(String(x)==='UNCLASSIFIED_TYPE'?'TYPE_NOT_ASSIGNED':String(x));
let review=null,selected='',status='not-run';
function reset(){
 review=null;selected='';status='not-run';
 window.CascadeReviewDialog?.reset();
 render();
}
function setReview(data){
 if(!data||data.Mode!=='READ_ONLY'||!Array.isArray(data.Items))throw Error('Expected read-only intelligence preview.');
 window.CascadeReviewDialog?.reset();
 review=data;selected='';status='ready';render();
}
function render(){
 const summary=$('intelligence-summary'),tbody=$('intelligence-rows'),stats=$('intelligence-kpis');
 if(!summary||!tbody||!stats)return;
 if(!review){
  summary.textContent=status==='running'?'Running deterministic schema and topology checks on the imported Git.mil snapshot…':
   'No preview loaded. Synchronize Git.mil, then click Run intelligence review. No AI model or Git.mil write is performed.';
  tbody.innerHTML='<tr><td colspan="5">No analyzed work items yet.</td></tr>';
  stats.innerHTML='';return;
 }
 const m=review.Summary||{},relations=review.RelationshipSummary||{},label=String(review.Label||'');
 summary.textContent=(m.AnalyzedIssues||0)+' of '+(m.LabelMatchingIssues||0)+' label-matching issues analyzed'+
  (m.Truncated?' (first 200 only; narrow by GitLab label and rerun).':'')+
  '. Label: '+(label||'(all imported labels)')+'. Organization review: '+(relations.WithCandidateEvidence||0)+
  ' items have evidence-backed candidates; '+(relations.MissingParentWithoutCandidates||0)+
  ' unresolved items have no supported suggestion. Click any issue for an immediate review dialog.';
 const metric=(name,value,desc)=>'<div class="dashboard-metric static"><span>'+safe(name)+'</span><strong>'+safe(value)+'</strong><small>'+safe(desc)+'</small></div>';
 stats.innerHTML=[
  metric('Analyzed',m.AnalyzedIssues||0,'Current label selection'),
  metric('Needs review',m.RequiresReview||0,'Decision required'),
  metric('Missing metadata',m.NoMetadata||0,'Incomplete cascade-meta'),
  metric('Type not assigned',m.UnclassifiedType||0,'No reliable work-item type'),
  metric('Missing parent',m.MissingDeclaredParent||0,'Unresolved declared hierarchy'),
  metric('Related candidates',relations.WithCandidateEvidence||0,'Evidence-based suggestions only')
 ].join('');
 const filter=String($('intelligence-filter').value||'ALL'),q=String($('intelligence-search').value||'').trim().toLowerCase();
 const candidates=review.Items.filter(item=>(filter==='ALL'||filter===item.ReviewState)&&
  (!q||(String(item.Title)+' '+item.Key+' '+item.StableId+' '+item.ProjectId).toLowerCase().includes(q)));
 tbody.innerHTML=candidates.map(item=>{
  const url=String(item.Url||'');
  const link=/^https:\/\/web\.git\.mil\//.test(url)?
   '<a href="'+safe(url)+'" target="_blank" rel="noopener noreferrer">Git.mil ↗</a>':'';
  const progress=window.CascadeReviewDialog?.countFor(item.Key);
  const text=progress?progress.approved+' / '+progress.total+' sections approved'+
   (progress.needsChanges?' · '+progress.needsChanges+' need changes':''):'Not reviewed';
  const problems=arr(item.DeficiencyCodes).map(codeName);
  const hasCandidate=arr(item.ParentReview?.Candidates).length;
  return '<tr><td><button class="example-item" type="button" data-intelligence-key="'+safe(item.Key)+
   '" aria-label="Open review dialog for '+safe(item.Title)+'">'+safe(item.Title)+'</button>'+
   '<small>#'+safe(item.Iid)+' · project '+safe(item.ProjectId)+'</small> '+link+'</td>'+
   '<td><span class="example-type-badge">'+safe(typeName(item.Type))+'</span></td>'+
   '<td>'+safe(arr(item.MissingMetadata).join(', ')||'—')+'</td>'+
   '<td>'+safe(problems.join(' · ')||'No structural findings')+
   (hasCandidate?'<small>'+hasCandidate+' possible parent relationship(s) to inspect</small>':'')+'</td>'+
   '<td><span class="tag '+(progress?.approved===5?'ok':progress?.needsChanges?'warn':'')+'">'+safe(text)+'</span>'+
   '<small>'+safe(item.RequiresReview?'Schema review recommended':'No structural deficiencies flagged')+'</small></td></tr>';
 }).join('')||'<tr><td colspan="5">No work items match this filter.</td></tr>';
}
async function run(){
 const btn=$('intelligence-run');if(!btn)return null;
 btn.disabled=true;status='running';render();
 try{
  const r=await fetch('/api/intelligence/preview',{
   method:'POST',headers:{'Content-Type':'application/json','X-Cascade-Intent':'preview-intelligence'},
   body:'{}',
   signal:typeof AbortSignal!=='undefined'&&AbortSignal.timeout?AbortSignal.timeout(60000):undefined
  });
  const data=await r.json();
  if(!r.ok)throw Error(data.error||'Read-only intelligence review failed');
  setReview(data);return data;
 }catch(e){
  review=null;status='failed';render();
  $('intelligence-summary').textContent='Review unavailable: '+e.message+'. Synchronize Git.mil or inspect diagnostics.';
  return null;
 }finally{btn.disabled=false}
}
function openItem(key){
 const it=review?.Items?.find(x=>x.Key===key);
 if(!it)return false;
 selected=key;
 const opened=window.CascadeReviewDialog?.open(it,review)||false;
 return opened;
}
function exportReview(){
 if(!review)return false;
 const doc={
  schema:'cascade.intelligence_review_export.v2',
  label:review.Label,source:review.Source,mode:'REVIEW_ONLY',summary:review.Summary,
  relationshipSummary:review.RelationshipSummary,
  decisions:window.CascadeReviewDialog?.bundle(false)||{items:[]},
  note:'The reviewed proposal package contains local approval states only. No GitLab writes occurred. Review internal descriptions before sharing.'
 };
 const link=document.createElement('a'),file=new Blob([JSON.stringify(doc,null,2)],{type:'application/json'});
 const url=URL.createObjectURL(file);
 link.href=url;link.download='cascade-intelligence-review-'+new Date().toISOString().slice(0,10)+'.json';link.click();
 setTimeout(()=>URL.revokeObjectURL(url),1000);return true;
}
const api=Object.freeze({reset,setReview,getReview:()=>review,getSelected:()=>selected,run,render,openItem,select:openItem,exportReview});
if(typeof window!=='undefined')window.CascadeIntelligenceWorkbench=api;
if(typeof module!=='undefined'&&module.exports)module.exports=api;
})();
