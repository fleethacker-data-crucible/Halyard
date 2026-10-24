(() => {
'use strict';
const $ = id => document.getElementById(id);
const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const state = {plan:null, decisions:{}, settings:{roots:'',days:30}, connectionSettings:{issueLabel:'',selectedProjectIds:[]}, server:false, diagnostics:[],serverDiagnostics:[]};
const diagnostic=(event,details='')=>{state.diagnostics.push({at:new Date().toISOString(),event,details:String(details).slice(0,160)});if(state.diagnostics.length>400)state.diagnostics.shift();};
window.addEventListener('error',e=>diagnostic('JS_ERROR',e.message));
window.addEventListener('unhandledrejection',e=>diagnostic('UNHANDLED_REJECTION',e.reason?.name||'Promise failed'));


// Diagnostic recording is opt-in, memory-only, and never collects input values.
const recorder={enabled:false,sessionId:null,events:[],started:null};
const record=(kind,data={})=>{
 if(!recorder.enabled)return;
 const event={at:new Date().toISOString(),kind,...data};
 recorder.events.push(event);
 if(recorder.events.length>3000)recorder.events.shift();
 const n=$('record-count');if(n)n.textContent=recorder.events.length+' events recorded';
 const out=$('record-output');if(out&&$('diagnostics')?.classList.contains('active'))out.textContent=recorder.events.slice(-35).map(x=>JSON.stringify(x)).join('\n');
};
const safeTarget=el=>{
 if(!el)return 'unknown';
 const e=el.closest?.('button,select,input,textarea,a,[role="button"]')||el;
 // IDs and control types only; never labels, values, links, text or DOM contents.
 return (e.id?'#'+e.id:(e.getAttribute?.('data-page')?'nav:'+e.getAttribute('data-page'):(e.tagName||'element').toLowerCase()));
};
document.addEventListener('click',e=>record('CLICK',{control:safeTarget(e.target)}),true);
document.addEventListener('change',e=>record('CHANGE',{control:safeTarget(e.target),value:'[OMITTED]'}),true);
document.addEventListener('submit',e=>record('SUBMIT',{control:safeTarget(e.target)}),true);
window.addEventListener('error',e=>record('JS_ERROR',{message:String(e.message||'Script error').slice(0,250)}));
window.addEventListener('unhandledrejection',e=>record('PROMISE_REJECTION',{reason:String(e.reason?.name||'Unknown').slice(0,100)}));
const originalFetch=window.fetch.bind(window);
window.fetch=async(input,init)=>{
 const url=typeof input==='string'?input:input?.url||'';
 // API path only; never query strings or credentials.
 const path=url.split('?')[0].replace(/^https?:\/\/[^/]+/,'');
 const method=init?.method||'GET',started=performance.now();
 record('API_REQUEST',{path,method});
 try{
  const response=await originalFetch(input,init);
  record('API_RESPONSE',{path,method,status:response.status,ok:response.ok,durationMs:Math.round(performance.now()-started)});
  return response;
 }catch(error){
  record('API_NETWORK_ERROR',{path,method,errorName:String(error?.name||'Error'),durationMs:Math.round(performance.now()-started)});
  throw error;
 }
};
function setRecording(active){
 recorder.enabled=active;
 $('record-status').textContent=active?'● Recording':'Recording off';
 $('record-start').disabled=active;
 $('record-stop').disabled=!active;
}
function exportRecording(){
 const payload={schemaVersion:2,sessionId:recorder.sessionId,started:recorder.started,exported:new Date().toISOString(),events:recorder.events,serverEvents:state.serverDiagnostics,notice:'Review server logs before sharing; may contain project identifiers or internal paths.'};
 download('cascade-session-'+(recorder.sessionId||'empty')+'.json',JSON.stringify(payload,null,2),'application/json');
}
const names = {nexus:['Nexus workspace','Connected hierarchy and engineering evidence.'],hierarchy:['Agile hierarchy','Deeply nested GitLab work-item relationships.'],examples:['Example work items','Fully defined, synthetic Theme-to-Task schema and hierarchy.'],diagnostics:['Diagnostics','Inspect failures and export a redacted support bundle.'],gitmil:['Git.mil integration','ARC-backed live GitLab synchronization and project selection.'],overview:['Command overview','Engineering evidence, reconstructed into delivery intelligence.'],work:['Work intelligence','Review proposed work and reconcile it with existing issues.'],evidence:['Git evidence','Source-of-truth engineering activity.'],report:['Leadership dashboard','Live work-item metrics, topology risks and evidence limitations.'],intelligence:['Intelligence workbench','Run existing normalization and schema checks as read-only proposals.'],stack:['Stack compatibility','Validate browser capabilities before the target-architecture refactor.'],settings:['Data & settings','Configure read-only scans and import offline data.']};
const arr = x => Array.isArray(x) ? x : x == null ? [] : [x];
const count = a => arr(a).length;
const items = () => arr(state.plan?.Actions);
const clusters = () => arr(state.plan?.Clusters);
const activity = () => arr(state.plan?.Activity);
const mapCluster = () => new Map(clusters().map(c=>[c.Key,c]));
const notify = (msg,error=false) => {const e=$('toast');e.textContent=msg;e.style.background=error?'#683944':'#28465d';e.classList.add('show');clearTimeout(notify.t);notify.t=setTimeout(()=>e.classList.remove('show'),5500)};
function page(id){if(!names[id])return;diagnostic('NAVIGATE',id);document.querySelectorAll('.page').forEach(e=>e.classList.toggle('active',e.id===id));document.querySelectorAll('#nav button').forEach(e=>e.classList.toggle('active',e.dataset.page===id));$('page-title').textContent=names[id][0];$('page-subtitle').textContent=names[id][1];if(id==='examples')renderExampleCatalog();if(id==='intelligence')window.CascadeIntelligenceWorkbench?.render();if(id==='stack')window.CascadeStackCheck?.render();window.dispatchEvent(new CustomEvent('cascade:page-changed',{detail:{page:id}}));}
function load(data,source){if(!data || !Array.isArray(data.Actions) || !Array.isArray(data.Clusters) || !Array.isArray(data.Activity))throw Error('Expected Cascade plan.json with Actions, Clusters and Activity arrays.');state.plan=data;state.decisions={};window.CascadeIntelligenceWorkbench?.reset();if(Array.isArray(data.Scope?.AvailableLabels)){$('issue-label-options').innerHTML=data.Scope.AvailableLabels.map(v=>'<option value="'+esc(v)+'"></option>').join('')}diagnostic('PLAN_LOADED','source='+source+' actions='+data.Actions.length);$('data-status').textContent=`${data.Scope?.Label?('Label: '+data.Scope.Label):'All labels'} · ${source}`;render();notify(`Loaded ${data.Actions.length} work candidates`);}
function renderLabelSummary(){
 const box=$('label-summary'),inspect=$('label-inspection');
 if(!box)return;
 const label=String(state.connectionSettings.issueLabel||'');
 const scope=state.plan?.Scope;
 if(!scope){
  box.textContent=label?'Exact Git.mil label "'+label+'" selected. Synchronize source projects to import matching Git.mil issues.':'All labels selected. Synchronize source projects to show all imported issues.';
  if(inspect)inspect.textContent='No Git.mil issue snapshot is loaded. Discover the accessible source projects, select them and synchronize.';
  return;
 }
 if(String(scope.Label||'').toLowerCase()!==label.toLowerCase()){
  box.textContent='The displayed plan was created for another label. Apply the new label and synchronize if necessary.';
  if(inspect)inspect.textContent='The active issue label has changed since this plan was loaded.';
  return;
 }
 const countShown=Number(scope.IncludedIssueCount)||0,sourceCount=Number(scope.SourceIssueCount)||0;
 const remote=!!scope.ServerLabelQueryPerformed,errors=Number(scope.ServerLabelQueryFailures)||0;
 box.textContent=(label?'Exact GitLab label "'+label+'"':'All labels')+': '+countShown+' of '+sourceCount+' locally imported issues shown. '+
  (label&&remote?'GitLab was queried for matching issues by label. ':label?'Only cached issues were filtered. ':'')+
  String(scope.Warning||'');
 if(!inspect)return;
 if(!label){
  inspect.textContent='All imported issues are visible. This is not necessarily every GitLab issue: your selected source projects and GitLab pagination determine coverage.';
  return;
 }
 const all=arr(scope.AvailableLabels);
 const simplified=x=>String(x||'').toLowerCase().replace(/[^a-z0-9]/g,'');
 const q=simplified(label);
 const suggestions=all.filter(x=>simplified(x).includes(q)||q.includes(simplified(x))).filter(x=>x.toLowerCase()!==label.toLowerCase()).slice(0,10);
 const guidance=countShown===0?String(scope.ZeroResultsExplanation||'No matching issues in the imported snapshot.'):'';
 const status=remote?
   'Server-side GitLab label search: yes; '+Number(scope.ServerLabelMatches||0)+' matching API results, '+Number(scope.ServerLabelAddedIssues||0)+' imported beyond broad issue pages.'+
   (errors?' '+errors+' source project(s) could not complete the label query.':'')+
   (scope.ServerLabelPageLimitReached?' The label scan also reached its page cap.':''):
   'Server-side GitLab label search: not performed for the current label. Synchronize selected projects to search older issues that may be beyond the normal page limit.';
 inspect.textContent=(countShown===0?'ZERO RESULTS. ':'')+guidance+' '+status+
   (suggestions.length?' Similar but DISTINCT Git.mil labels found: '+suggestions.join(' · ')+'. Select the real label explicitly; none is chosen automatically.':'')+
   ' If your Theme or Epic is a native GitLab work item rather than a project issue, its API may differ.';
}
// Isolated, interactive example topology. Never replaces the real Git.mil plan.
const getExampleCatalog=()=>window.CascadeExampleExplorer?.getCatalog()||window.CascadeExampleCatalog||null;
const selectExampleRecord=id=>window.CascadeExampleExplorer?.select(id);
const renderExampleCatalog=()=>window.CascadeExampleExplorer?.render();
window.CascadeExampleBridge=Object.freeze({getCatalog:getExampleCatalog,getActiveId:()=>window.CascadeExampleExplorer?.getActiveId()||'',select:selectExampleRecord,render:renderExampleCatalog});
function render(){
renderLabelSummary();
const a=activity(), c=clusters(), w=items(), repos=new Set(a.map(x=>x.Repository)); 
$('stat-repos').textContent=repos.size;$('stat-commits').textContent=a.length;$('stat-clusters').textContent=c.length;$('stat-triage').textContent=w.filter(x=>x.Action!=='EXISTS').length;
const cats={};c.forEach(x=>cats[x.Category||'UNKNOWN']=(cats[x.Category||'UNKNOWN']||0)+1);
const top=Math.max(1,...Object.values(cats));
$('category-chart').classList.toggle('empty',!c.length);
$('category-chart').innerHTML=c.length?Object.entries(cats).sort((a,b)=>b[1]-a[1]).map(([k,n])=>`<div class="bar-row"><label>${esc(k)}</label><div class="bar-track"><div class="bar-fill" style="width:${100*n/top}%"></div></div><b>${n}</b></div>`).join(''):'Import a plan or run a scan to see activity.';
const covered=c.filter(x=>count(x.EvidenceIds)>0).length;
$('quality-value').textContent=c.length?Math.round(100*covered/c.length)+'%':'—';
$('queue-preview').innerHTML=w.filter(x=>x.Action!=='EXISTS').slice(0,6).map(x=>`<div class="queue-item"><div><strong>${esc(x.Title)}</strong><small>${esc(x.Key)} · ${count(x.EvidenceIds)} evidence link(s)</small></div><span class="tag ${x.Action==='CONFLICT'?'warn':''}">${esc(x.Action)}</span></div>`).join('')||'<p>No work candidates in the current dataset.</p>';
renderWork();renderEvidence();renderReport();renderHierarchy();renderTopologyAudit();window.dispatchEvent(new Event('cascade:plan-changed'));
}
function renderWork(){
const q=$('search').value.toLowerCase(),filter=$('filter').value,byKey=mapCluster();
const list=items().filter(x=>(filter==='ALL'||x.Action===filter) && (x.Title+' '+(byKey.get(x.Key)?.Repository||'')+' '+x.Key).toLowerCase().includes(q));
$('work-rows').innerHTML=list.map(x=>{
const c=byKey.get(x.Key)||{}, decision=state.decisions[x.Key]||'PENDING';
return `<tr><td><button class="link" data-key="${esc(x.Key)}"><strong>${esc(x.Title)}</strong></button><small>${esc(c.Repository||'Unknown repository')}</small></td><td>${esc(c.Category||'UNKNOWN')}</td><td>${count(x.EvidenceIds)} commit(s)</td><td><span class="tag ${x.Action==='CONFLICT'?'warn':x.Action==='EXISTS'?'ok':''}">${esc(x.Action)}</span></td><td><select data-decision="${esc(x.Key)}"><option ${decision==='PENDING'?'selected':''}>PENDING</option><option ${decision==='APPROVE'?'selected':''}>APPROVE</option><option ${decision==='REJECT'?'selected':''}>REJECT</option><option ${decision==='NEEDS_INFO'?'selected':''}>NEEDS_INFO</option></select></td></tr>`}).join('')||'<tr><td colspan="5">No matching work candidates.</td></tr>';
document.querySelectorAll('[data-key]').forEach(b=>b.onclick=()=>showDetail(b.dataset.key));
document.querySelectorAll('[data-decision]').forEach(s=>s.onchange=()=>{state.decisions[s.dataset.decision]=s.value;renderReport()});
}
function showDetail(key){const a=items().find(x=>x.Key===key);const c=mapCluster().get(key);if(!a)return;
$('work-detail').innerHTML=`<h2>${esc(a.Title)}</h2><p>${esc(c?.Repository||'')} · ${esc(c?.Category||'UNKNOWN')} · ${esc(a.Action)}</p><p><b>Evidence:</b> ${arr(a.EvidenceIds).map(esc).join(', ')||'none'}</p><p><b>Completion:</b> Unverified · <b>Estimated hours:</b> Not inferred</p><h2>Generated work-item description</h2><pre>${esc(a.Description)}</pre>`;}
function renderEvidence(){const q=$('evidence-search').value.toLowerCase();$('evidence-rows').innerHTML=activity().filter(x=>(x.Subject+' '+x.Repository+' '+arr(x.Files).join(' ')+' '+x.Commit).toLowerCase().includes(q)).slice(0,1000).map(x=>`<tr><td title="${esc(x.Commit)}">${esc(String(x.Commit||'').slice(0,10))}</td><td>${esc(x.Subject)}</td><td>${esc(String(x.Repository||'').split(/[\\/]/).pop())}</td><td>${esc(x.Timestamp||'')}</td><td>${count(x.Files)}</td></tr>`).join('')||'<tr><td colspan="5">No matching Git evidence.</td></tr>';}

function renderHierarchy(){
 const box=$('hierarchy-tree');if(!box)return;
 const issues=arr(state.plan?.Hierarchy);
 const nodes=issues.length?issues:arr(state.plan?.Issues).map(i=>({Id:'gitlab:'+i.project_id+':'+i.iid,ParentId:null,Title:i.title,Type:'UNCLASSIFIED',State:i.state,Url:i.web_url,ProjectId:i.project_id,Iid:i.iid}));
 const map=new Map(nodes.map(n=>[String(n.Id),{...n,children:[]}]));
 const roots=[];
 for(const n of map.values()){
   const p=map.get(String(n.ParentId));
   if(p && p!==n){let cur=p,seen=new Set([String(n.Id)]),cycle=false;while(cur){if(seen.has(String(cur.Id))){cycle=true;break}seen.add(String(cur.Id));cur=map.get(String(cur.ParentId));}if(!cycle){p.children.push(n);continue}}
   roots.push(n);
 }
 const order={THEME:0,EPIC:1,FEATURE:2,STORY:3,TASK:4,BUG:5,UNCLASSIFIED:6};
 const sort=ns=>ns.sort((a,b)=>(order[a.Type]??9)-(order[b.Type]??9)||String(a.Title).localeCompare(String(b.Title)));
 const q=$('tree-search').value.toLowerCase();
 function matches(n){return !q||String(n.Title).toLowerCase().includes(q)||n.children.some(matches)}
 function branch(n,depth){if(depth>60||!matches(n))return '';const kids=sort(n.children).filter(matches),has=kids.length>0;
 const href=String(n.Url||'');const link=href.startsWith('https://web.git.mil/')?`<a class="tree-title" href="${esc(href)}" target="_blank" rel="noopener noreferrer">${esc(n.Title)}</a>`:`<span class="tree-title">${esc(n.Title)}</span>`;
 return `<div class="tree-node"><div class="tree-line"><button class="tree-toggle" ${has?'':'disabled'} aria-label="Toggle children">${has?'▾':'·'}</button><span class="tree-type">${esc(n.Type==='UNCLASSIFIED'?'Type not assigned':(n.Type||'Type not assigned'))}</span>${link}<span class="tree-state">${esc(n.State||'')}</span><span class="tag">#${esc(n.Iid)}</span></div>${has?`<div class="tree-children">${kids.map(c=>branch(c,depth+1)).join('')}</div>`:''}</div>`;
 }
 box.innerHTML=sort(roots).filter(matches).map(n=>branch(n,0)).join('')||'<p>No hierarchy records available. Synchronize Git.mil or import a plan with issues.</p>';
 $('hierarchy-summary').textContent=`${nodes.length} work items · ${roots.length} top-level/unlinked items · ${nodes.filter(n=>n.Type==='UNCLASSIFIED').length} with no assigned work-item type · Hierarchy is based on explicit metadata, not guessed relationships.`;
 box.querySelectorAll('.tree-toggle:not([disabled])').forEach(b=>b.onclick=()=>{const child=b.parentElement.nextElementSibling;child.hidden=!child.hidden;b.textContent=child.hidden?'▸':'▾'});
}
function renderTopologyAudit(){
 const summary=$('topology-summary'),tbody=$('topology-rows');
 if(!summary||!tbody)return;
 const audit=state.plan?.TopologyAudit;
 if(!audit||!Array.isArray(audit.Findings)){
  summary.textContent='No topology audit is available. Synchronize Git.mil using this version to analyze imported work items.';
  tbody.innerHTML='<tr><td colspan="5">No review findings are available.</td></tr>';
  return;
 }
 const m=audit.Summary||{},selected=$('topology-filter').value||'ALL';
 const all=arr(audit.Findings);
 const results=all.filter(f=>selected==='ALL'||f.Severity===selected||f.Code===selected);
 const shown=results.slice(0,750);
 summary.textContent=(m.VisibleIssueCount||0)+' issues in selected label · '+(m.ClassifiedIssueCount||0)+' with an assigned work-item type · '+ (m.AffectedIssueCount||0)+' with findings · '+(m.ErrorCount||0)+' errors · '+(m.WarningCount||0)+' warnings · '+(m.InfoCount||0)+' informational. Showing '+shown.length+' of '+results.length+' '+(selected==='ALL'?'findings':selected+' findings')+'. No Git.mil work items were changed.';
 tbody.innerHTML=shown.map(f=>{
  const url=String(f.Url||'');
  const title=url.startsWith('https://web.git.mil/')?
    '<a href="'+esc(url)+'" target="_blank" rel="noopener noreferrer">'+esc(f.Title||'#'+f.Iid)+'</a>':
    esc(f.Title||'#'+f.Iid);
  const sev=String(f.Severity||'INFO');
  return '<tr><td>'+title+'<small> #'+esc(f.Iid)+' · project '+esc(f.ProjectId)+'</small></td><td>'+esc(f.Type==='UNCLASSIFIED'?'Type not assigned':(f.Type||'Type not assigned'))+'</td><td><span class="tag '+(sev==='ERROR'?'warn':sev==='WARNING'?'warn':'')+'">'+esc(sev)+'</span></td><td><strong>'+esc(f.Code==='UNCLASSIFIED_TYPE'?'TYPE_NOT_ASSIGNED':(f.Code||''))+'</strong></td><td>'+esc(f.Explanation||'')+'</td></tr>';
 }).join('')||'<tr><td colspan="5">No findings for this selection. This is not proof that every imported issue is complete.</td></tr>';
}
function renderDiagnostics(){
 const entries=[...state.diagnostics,...state.serverDiagnostics];
 $('diagnostics-output').textContent=entries.length?entries.map(e=>JSON.stringify(e)).join('\n'):'No diagnostic events yet.';
}

function reportMarkdown(){
const a=activity(),c=clusters(),w=items(),reps=new Set(a.map(x=>x.Repository));
const byAction=k=>w.filter(x=>x.Action===k).length;
const pending=w.filter(x=>!state.decisions[x.Key]||state.decisions[x.Key]==='PENDING').length;
const sc=state.plan?.Scope;
const lines=['# Cascade — Leadership delivery intelligence','',`Generated: ${new Date().toISOString()}`,'',`Git.mil issue label: ${sc?.Label||'(all labels / unfiltered)'}`,sc?`Issues shown: ${sc.IncludedIssueCount}/${sc.SourceIssueCount}; excluded: ${sc.ExcludedIssueCount}; unattributed project commits suppressed: ${sc.ExcludedActivityCount}`:'No label-filtered Git.mil plan is loaded.','', '> **Evidence limitation:** This is an engineering activity brief, not a verified delivery or acceptance report.',sc?`> **Team attribution limit:** ${sc.Warning}`:'> **Team attribution limit:** Source data is unscoped.','',
'## Executive summary',`- Repositories with observed commits: ${reps.size}`,`- Commits observed: ${a.length}`,`- Candidate work clusters: ${c.length}`,`- New issue proposals: ${byAction('PROPOSE_CREATE')}`,`- Matched existing issues: ${byAction('EXISTS')}`,`- Reconciliation conflicts: ${byAction('CONFLICT')}`,`- Pending human triage: ${pending}`,'',
'## Engineering activity observed'];
if(!c.length)lines.push('- No activity data loaded.');
for(const x of c){lines.push(`- **${x.Title}** (${x.Category}): ${count(x.EvidenceIds)} linked commits; acceptance and completion not verified. Evidence key: \`${x.Key}\`.`);}
lines.push('','## Delivery risks and evidence gaps','- Git activity cannot establish actual hours worked or individual productivity.','- Completion requires policy-defined acceptance, CI, integration and/or deployment evidence.','- Classifications are heuristic and should be validated before publishing as delivery outcomes.','- Repository scan coverage and Git history retention may limit visibility.','','## Triage decisions');
for(const x of w)lines.push(`- ${x.Key}: ${state.decisions[x.Key]||'PENDING'} — ${x.Title}`);
lines.push('','## Recommended management actions','- Review pending work candidates and unresolved conflicts.','- Confirm acceptance criteria and traceability to GitLab work items.','- Connect CI/merge/deployment evidence before reporting verified completions.');
return lines.join('\n');
}
function renderReport(){return window.CascadeLeadershipDashboard?.render(state.plan)}
window.CascadeNexusBridge=Object.freeze({getPlan:()=>state.plan,download:(name,content,mime)=>download(name,content,mime)});
function download(name,content,mime){const blob=new Blob([content],{type:mime}),url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),1500);}
function decisions(){return {schemaVersion:1,sourceRunId:state.plan?.RunId||null,generatedAtUtc:new Date().toISOString(),mode:'REVIEW_ONLY',decisions:items().map(x=>({key:x.Key,action:x.Action,decision:state.decisions[x.Key]||'PENDING',title:x.Title}))};}
async function ping(){try{const r=await fetch('/api/status',{signal:AbortSignal.timeout?AbortSignal.timeout(1800):undefined});if(!r.ok)throw Error('offline');state.server=true;$('api-dot').classList.remove('muted');$('api-status').textContent='PowerShell backend online';const data=await r.json();if(data.latestPlan)try{const plan=await (await fetch('/api/latest')).json();load(plan,'Latest scan')}catch(_){} }catch(_){state.server=false;$('api-status').textContent='Backend offline · import only';}}
async function scan(){if(!state.server){page('settings');notify('Launch Cascade-GUI.bat to enable local Git scanning. You can also import an existing plan.json.',true);return}const roots=$('roots').value.split(/\r?\n/).map(s=>s.trim()).filter(Boolean);if(!roots.length){page('settings');notify('Enter at least one local repository root.',true);return}const btn=$('run-btn');btn.disabled=true;btn.textContent='Scanning…';try{const r=await fetch('/api/scan',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({roots,sinceDays:Number($('days').value)})});const body=await r.json();if(!r.ok)throw Error(body.error||'Scan failed');load(body.plan,'Live local scan');page('overview');}catch(e){notify(e.message,true)}finally{btn.disabled=false;btn.textContent='↻ Sync Git.mil';}}
document.querySelectorAll('#nav button').forEach(b=>b.onclick=()=>page(b.dataset.page));
document.querySelectorAll('[data-go]').forEach(b=>b.onclick=()=>page(b.dataset.go));
$('demo-btn').onclick=()=>{load({
 RunId:'DEMO-NOT-REAL-DATA',
 Hierarchy:[
 {Id:'demo-theme',Title:'Demo delivery capability',Type:'THEME',ParentId:'',State:'opened',ProjectId:'DEMO'},
 {Id:'demo-epic',Title:'Demo platform hardening',Type:'EPIC',ParentId:'demo-theme',State:'opened',ProjectId:'DEMO'},
 {Id:'demo-feature',Title:'JWT authentication',Type:'FEATURE',ParentId:'demo-epic',State:'opened',ProjectId:'DEMO'},
 {Id:'demo-story',Title:'Identity middleware',Type:'STORY',ParentId:'demo-feature',State:'opened',ProjectId:'DEMO'},
 {Id:'demo-task',Title:'Integration test coverage',Type:'TASK',ParentId:'demo-story',State:'opened',ProjectId:'DEMO'},
 {Id:'demo-etl',Title:'ETL retry reliability',Type:'FEATURE',ParentId:'demo-epic',State:'opened',ProjectId:'DEMO'}],
 Activity:[
 {Repository:'C:\\Demo\\api-service',Commit:'a'.repeat(40),EvidenceId:'git:'+'a'.repeat(40),Subject:'Add JWT auth middleware',Timestamp:'2026-10-19T10:00:00Z',Files:['src/auth.ps1']},
 {Repository:'C:\\Demo\\api-service',Commit:'b'.repeat(40),EvidenceId:'git:'+'b'.repeat(40),Subject:'Add auth integration tests',Timestamp:'2026-10-20T10:00:00Z',Files:['tests/auth.tests.ps1']},
 {Repository:'C:\\Demo\\data-pipeline',Commit:'c'.repeat(40),EvidenceId:'git:'+'c'.repeat(40),Subject:'Fix ETL retry logic',Timestamp:'2026-10-20T13:00:00Z',Files:['src/etl.ps1']}],
 Clusters:[
 {Key:'demo-auth',Repository:'C:\\Demo\\api-service',Title:'Add JWT auth middleware',Category:'CAPABILITY',EvidenceIds:['git:'+'a'.repeat(40),'git:'+'b'.repeat(40)]},
 {Key:'demo-etl',Repository:'C:\\Demo\\data-pipeline',Title:'Fix ETL retry logic',Category:'DEFECT',EvidenceIds:['git:'+'c'.repeat(40)]}],
 Actions:[
 {Key:'demo-auth',Action:'PROPOSE_CREATE',Title:'[Discovered] Add JWT auth middleware',EvidenceIds:['git:'+'a'.repeat(40),'git:'+'b'.repeat(40)],Description:'## Engineering evidence\n- Authentication middleware and tests\n\n### Acceptance criteria\n- [ ] Verify integration and acceptance'},
 {Key:'demo-etl',Action:'CONFLICT',Title:'[Discovered] Fix ETL retry logic',EvidenceIds:['git:'+'c'.repeat(40)],Description:'## Engineering evidence\n- ETL retry changes\n\n### Acceptance criteria\n- [ ] Resolve duplicate work item'}]
},'DEMO · NOT REAL DATA');page('overview');};
$('topology-filter').onchange=renderTopologyAudit;
$('topology-export').onclick=()=>{
 const audit=state.plan?.TopologyAudit;
 if(!audit||!Array.isArray(audit.Findings)){notify('Synchronize Git.mil to generate a topology audit first.',true);return}
 const label=String(audit.IssueLabel||'all-labels').replace(/[^a-z0-9_-]/gi,'-').slice(0,50);
 download('cascade-topology-'+label+'-'+new Date().toISOString().slice(0,10)+'.json',JSON.stringify(audit,null,2),'application/json');
 diagnostic('TOPOLOGY_AUDIT_EXPORTED','Findings='+audit.Findings.length);
};
$('example-search').oninput=renderExampleCatalog;
$('example-type').onchange=renderExampleCatalog;
$('example-rows').onclick=e=>window.CascadeExampleExplorer?.handleClick(e);
$('example-tree').onclick=e=>window.CascadeExampleExplorer?.handleClick(e);
$('example-detail').onclick=e=>window.CascadeExampleExplorer?.handleClick(e);
$('example-expand').onclick=()=>window.CascadeExampleExplorer?.expandAll();
$('example-collapse').onclick=()=>window.CascadeExampleExplorer?.collapseAll();
$('example-export').onclick=()=>{
 const catalog=getExampleCatalog();
 if(!catalog){notify('Local example catalog unavailable',true);return}
 download('cascade-complete-example-hierarchy.json',JSON.stringify(catalog,null,2),'application/json');
 notify('Synthetic hierarchy exported. It has no Git.mil credentials or data.');
};
$('example-copy').onclick=async()=>{
 const catalog=getExampleCatalog(),item=arr(catalog?.workItems).find(x=>x.id===window.CascadeExampleExplorer?.getActiveId());
 if(!item){notify('Choose an example record to copy.',true);return}
 try{await navigator.clipboard.writeText(item.description);notify('Selected synthetic work-item description copied.')}
 catch(_){notify('Clipboard unavailable. Select the description text or export JSON.',true)}
};
$('intelligence-run').onclick=async()=>{const result=await window.CascadeIntelligenceWorkbench?.run();if(result)diagnostic('INTELLIGENCE_REVIEW_COMPLETE','analyzed='+result.Summary?.AnalyzedIssues)};
$('intelligence-export').onclick=()=>{if(!window.CascadeIntelligenceWorkbench?.exportReview())notify('Run intelligence review before exporting.',true)};
$('intelligence-topology').onclick=()=>page('hierarchy');
$('intelligence-search').oninput=()=>window.CascadeIntelligenceWorkbench?.render();
$('intelligence-filter').onchange=()=>window.CascadeIntelligenceWorkbench?.render();
window.CascadeReviewDialog?.bind();
$('intelligence-rows').onclick=e=>{const button=e.target?.closest?.('[data-intelligence-key]');if(button)window.CascadeIntelligenceWorkbench?.openItem(button.dataset.intelligenceKey)};
$('leadership-kpis').onclick=e=>{const target=e.target?.closest?.('[data-dash-go]');if(target)page(target.dataset.dashGo)};
$('leadership-export').onclick=()=>{
 const metrics=window.CascadeLeadershipDashboard?.compute(state.plan);
 if(!metrics)return notify('No metrics available.',true);
 download('cascade-leadership-dashboard-'+new Date().toISOString().slice(0,10)+'.json',JSON.stringify({schema:'cascade.leadership.dashboard.v1',observedAtUtc:new Date().toISOString(),metrics,warning:'Issue closure, commits and topology do not establish verified delivery.'},null,2),'application/json');
};
$('stack-browser-run').onclick=async()=>{diagnostic('STACK_CHECK_STARTED');await window.CascadeStackCheck?.run();diagnostic('STACK_CHECK_COMPLETE','Browser-only; host probe separate')};
$('stack-export').onclick=()=>{if(!window.CascadeStackCheck?.exportResults())notify('Run browser stack checks first.',true)};
$('tree-search').oninput=renderHierarchy;
$('tree-expand').onclick=()=>{document.querySelectorAll('.tree-children').forEach(e=>e.hidden=false);document.querySelectorAll('.tree-toggle:not([disabled])').forEach(e=>e.textContent='▾')};
$('tree-collapse').onclick=()=>{document.querySelectorAll('.tree-children').forEach(e=>e.hidden=true);document.querySelectorAll('.tree-toggle:not([disabled])').forEach(e=>e.textContent='▸')};
$('diagnostics-refresh').onclick=async()=>{try{const r=await fetch('/api/diagnostics');if(!r.ok)throw Error('HTTP '+r.status);const d=await r.json();state.serverDiagnostics=arr(d.entries);diagnostic('SERVER_LOG_LOADED','entries='+state.serverDiagnostics.length)}catch(e){diagnostic('SERVER_LOG_FAILED',e.message)}renderDiagnostics()};
$('diagnostics-export').onclick=()=>{renderDiagnostics();download('cascade-support-'+new Date().toISOString().slice(0,10)+'.json',JSON.stringify({schemaVersion:1,uiEvents:state.diagnostics,serverEvents:state.serverDiagnostics,environment:{userAgent:navigator.userAgent,source:state.plan?.Source||'unknown',hasPlan:!!state.plan}},null,2),'application/json')};
$('diagnostics-clear').onclick=()=>{state.diagnostics=[];state.serverDiagnostics=[];renderDiagnostics()};
$('load-file').onclick=()=>$('file-input').click();
$('file-input').onchange=async e=>{const f=e.target.files[0];if(!f)return;try{load(JSON.parse(await f.text()),f.name)}catch(ex){notify(ex.message,true)}e.target.value='';};
$('run-btn').onclick=()=>page('gitmil');
$('arc-open').onclick=async()=>{const btn=$('arc-open'),box=$('gitmil-status');btn.disabled=true;box.textContent='Launching the modern ARC browser in a separate process…';try{const r=await fetch('/api/arc/open',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}',signal:AbortSignal.timeout?AbortSignal.timeout(9000):undefined});const d=await r.json();if(!r.ok)throw Error((d.code?'['+d.code+'] ':'')+(d.error||'ARC browser launch failed'));box.textContent=d.hint||'Modern ARC browser is opening. Restart Cascade to authenticate its own vault session.';await refreshIntegrations();}catch(e){box.textContent='ARC browser launch failed: '+e.message;notify(e.message,true)}finally{btn.disabled=false}};
async function refreshIntegrations(){const target=$('integration-results');target.textContent='Reading ARC state…';try{const r=await fetch('/api/integrations');const d=await r.json();if(!r.ok)throw Error(d.error||'ARC state unavailable');const integrations=Array.isArray(d.integrations)?d.integrations.flat(Infinity):[];target.innerHTML=`<p>ARC installed: <b>${d.arcInstalled?'Yes':'No'}</b> · Loaded: <b>${d.arcLoaded?'Yes':'No'}</b> · Vault unlocked: <b>${d.vaultUnlocked?'Yes':'No'}</b></p>`+integrations.map(x=>`<div class="queue-item"><strong>${esc(x.role)} · ${esc(x.item)}</strong><span class="tag">Mapping configured · API untested</span></div>`).join('')+`<p>${esc(d.hint||'')}</p>`+((d.arcErrorCode||(!d.vaultUnlocked&&d.authStage&&d.authStage!=='not-started'))?`<p><strong>Authentication:</strong> ${esc(d.arcErrorCode||'NOT_UNLOCKED')} · Stage: ${esc(d.authStage||'unknown')}</p><p>Launch <code>Cascade-GUI.bat --console</code> to review ARC's local authentication prompts and diagnostics. If ARC loads in Keystone but not Cascade, ensure both use the same ARC installation folder.</p>`:'');}catch(e){target.textContent='ARC status failed: '+e.message;diagnostic('INTEGRATION_CHECK_FAILED',e.message)}}
async function testProvider(profile){const target=$('provider-results');target.textContent='Testing '+profile+' API (real authenticated request)…';try{const r=await fetch('/api/provider/test',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({profile})});const d=await r.json();if(!r.ok||!d.connected)throw Error((d.code?'['+d.code+'] ':'')+(d.error||'Provider probe failed'));target.textContent=profile+' connected. Model: '+(d.model||'unknown')+'; received response: '+d.responseReceived;notify(profile+' API connected')}catch(e){target.textContent=profile+' failed: '+e.message;notify(e.message,true)}}
$('test-genai').onclick=()=>testProvider('GenAi');$('test-grok').onclick=()=>testProvider('Grok');
$('check-gitmil').onclick=async()=>{const box=$('gitmil-status'),btn=$('check-gitmil');btn.disabled=true;box.textContent='Checking authenticated Git.mil /api/v4/user…';try{const r=await fetch('/api/gitmil/status',{signal:AbortSignal.timeout?AbortSignal.timeout(19000):undefined});const d=await r.json();if(!r.ok||!d.connected)throw Error((d.code?'['+d.code+'] ':'')+(d.error||'Git.mil authentication rejected'));box.textContent='Connected to '+d.host+' as '+d.user+'. ARC session: '+(d.arcLoaded?'loaded':'not loaded (environment credential).')+' Select projects next.';notify('Git.mil authenticated')}catch(e){box.textContent='Connection unavailable: '+e.message;diagnostic('GITMIL_STATUS_FAILED',e.message);notify(e.message,true)}finally{btn.disabled=false}};
$('fetch-projects').onclick=async()=>{const box=$('project-picker');box.textContent='Discovering Git.mil projects…';try{const r=await fetch('/api/gitmil/projects',{signal:AbortSignal.timeout?AbortSignal.timeout(45000):undefined});const d=await r.json();if(!r.ok)throw Error((d.code?'['+d.code+'] ':'')+(d.error||'Discovery failed'));const flatten=x=>Array.isArray(x)?x.flat(Infinity):x?[x]:[];const projects=flatten(d.projects).filter(x=>x&&Number.isSafeInteger(Number(x.id))&&Number(x.id)>0);const saved=new Set((state.connectionSettings.selectedProjectIds||[]).map(Number));box.innerHTML=projects.length?projects.map(x=>`<label style="display:block;padding:7px;border-bottom:1px solid #27364b"><input type="checkbox" class="project-choice" value="${Number(x.id)}" ${saved.has(Number(x.id))?'checked':''}> ${esc(x.path_with_namespace||x.name||'Unnamed project')} (#${Number(x.id)})</label>`).join(''):'No accessible projects returned. Check ARC token scope and membership.';diagnostic('PROJECT_DISCOVERY_RESULT','Count: '+projects.length);}catch(e){diagnostic('PROJECT_DISCOVERY_FAILED',e.name||'NetworkError');box.textContent='Project discovery failed: '+e.message;notify(e.message,true)}};
function setGitMilSyncBusy(busy){
 const button=$('gitmil-sync'),label=$('gitmil-sync-label'),feedback=$('gitmil-sync-feedback'),inline=$('gitmil-sync-inline-spinner');
 if(button){button.disabled=busy;button.setAttribute('aria-busy',busy?'true':'false');}
 if(label)label.textContent=busy?'Synchronizing Git.mil…':'Synchronize selected projects';
 if(feedback)feedback.hidden=!busy;
 if(inline)inline.hidden=!busy;
}
$('gitmil-sync').onclick=async()=>{if($('issue-label').value.trim().toLowerCase()!==String(state.connectionSettings.issueLabel||'').toLowerCase()){notify('Click Apply label before synchronizing.',true);return}const ids=[...document.querySelectorAll('.project-choice:checked')].map(x=>Number(x.value)).filter(x=>Number.isSafeInteger(x)&&x>0);if(!ids.length){notify('Select at least one Git.mil project',true);return}setGitMilSyncBusy(true);try{const r=await fetch('/api/gitmil/sync',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({projectIds:ids,sinceDays:Number($('gitmil-days').value)})});const d=await r.json();if(!r.ok)throw Error(d.error||'Sync failed');load(d.plan,'Git.mil live');state.connectionSettings.selectedProjectIds=ids;
 if(d.plan?.Scope?.Label && Number(d.plan.Scope.IncludedIssueCount)===0){
  page('gitmil');
  notify('No exact matches for "'+d.plan.Scope.Label+'". Review the label diagnosis and GitLab source projects.',true);
 }else{
  page('overview');
  notify('Git.mil synchronized. '+(d.plan?.Scope?.Label?'Filtered by '+d.plan.Scope.Label+'.':'All labels included.')+' Review issue counts.');
 }}catch(e){diagnostic('GITMIL_SYNC_FAILED',e.name||'NetworkError');notify('Git.mil sync failed: '+e.message,true)}finally{setGitMilSyncBusy(false)}};
$('search').oninput=renderWork;$('filter').onchange=renderWork;$('evidence-search').oninput=renderEvidence;
$('export-decisions').onclick=()=>download('cascade-triage-decisions.json',JSON.stringify(decisions(),null,2),'application/json');
$('download-report').onclick=()=>download('cascade-leadership-brief.md',reportMarkdown(),'text/markdown');
$('copy-report').onclick=async()=>{try{await navigator.clipboard.writeText(reportMarkdown());notify('Report copied')}catch(_){notify('Clipboard unavailable; use Export Markdown',true)}};
$('save-settings').onclick=()=>{state.settings={roots:$('roots').value,days:Number($('days').value)};notify('Settings saved for this session')};
$('clear-data').onclick=()=>{state.plan=null;state.decisions={};$('data-status').textContent='No dataset loaded';render();notify('Loaded data cleared')};

async function loadConfig(){
 try{const r=await fetch('/api/config');if(!r.ok)throw Error('Settings endpoint unavailable');const c=await r.json();
 state.connectionSettings=c;$('cfg-arc-path').value=c.arcPath||'';$('cfg-gitlab-arc').value=c.gitLabArcItem||'gitmil';$('cfg-gemini-arc').value=c.geminiArcItem||'genapi';$('cfg-grok-arc').value=c.grokArcItem||'advana-api';$('cfg-genai-url').value=c.genaiUrl||'https://api.genai.mil/v1/chat/completions';$('cfg-genai-model').value=c.genaiModel||'gemini-3.8-flash';$('cfg-advana-url').value=c.advanaUrl||'https://advana-jupiter.cloud.databricks.mil/serving-endpoints/databricks-grok-4-6/invocations';$('cfg-advana-model').value=c.advanaModel||'system.ai.grok-4-6';$('cfg-lookback').value=c.sinceDays||30;
 $('gitmil-days').value=c.sinceDays||30;
 $('issue-label').value=c.issueLabel||'';renderLabelSummary();
 }catch(e){diagnostic('SETTINGS_LOAD_FAILED',e.message);}
}
$('cfg-save').onclick=async()=>{
 const config={gitLabUrl:'https://web.git.mil',arcPath:$('cfg-arc-path').value.trim(),gitLabArcItem:$('cfg-gitlab-arc').value.trim(),geminiArcItem:$('cfg-gemini-arc').value.trim(),grokArcItem:$('cfg-grok-arc').value.trim(),issueLabel:state.connectionSettings.issueLabel||'',genaiUrl:$('cfg-genai-url').value.trim(),genaiModel:$('cfg-genai-model').value.trim(),advanaUrl:$('cfg-advana-url').value.trim(),advanaModel:$('cfg-advana-model').value.trim(),sinceDays:Number($('cfg-lookback').value),localRoots:$('roots').value.split(/\r?\n/).map(x=>x.trim()).filter(Boolean),selectedProjectIds:[...document.querySelectorAll('.project-choice:checked')].map(x=>Number(x.value))};
 try{const r=await fetch('/api/config',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(config)});const d=await r.json();if(!r.ok)throw Error(d.error||'Save failed');state.connectionSettings=d;$('issue-label').value=d.issueLabel||'';notify('Integration mappings saved to backend configuration. No tokens stored.');$('gitmil-days').value=d.sinceDays;renderLabelSummary();}catch(e){notify('Could not save settings: '+e.message,true);diagnostic('SETTINGS_SAVE_FAILED',e.message);}
};
$('integration-refresh').onclick=refreshIntegrations;
async function applyIssueLabel(label){
 const button=$('label-apply'),clear=$('label-clear');
 button.disabled=true;clear.disabled=true;
 try{
  const r=await fetch('/api/issue-label',{method:'POST',headers:{'Content-Type':'application/json','X-Cascade-Intent':'change-label'},body:JSON.stringify({issueLabel:label}),signal:AbortSignal.timeout?AbortSignal.timeout(5000):undefined});
  const d=await r.json();if(!r.ok)throw Error(d.error||'Cannot apply issue label');
  state.connectionSettings.issueLabel=d.issueLabel||'';
  $('issue-label').value=d.issueLabel||'';
  diagnostic('ISSUE_LABEL_FILTER_CHANGED',d.issueLabel?'Label selected':'Cleared');
  if(d.plan){
   load(d.plan,'Git.mil local snapshot');
   notify('Exact issue-label filter applied to the already imported Git.mil work items. No new API sync was required.');
  }else{
   state.plan=null;state.decisions={};
   $('data-status').textContent=(d.issueLabel?('Label: '+d.issueLabel):'All labels')+' · first sync required';
   render();page('gitmil');
   notify('Label saved. Synchronize Git.mil once to import work items for filtering.');
  }
 }catch(e){$('issue-label').value=state.connectionSettings.issueLabel||'';notify('Could not apply label: '+e.message,true);diagnostic('ISSUE_LABEL_FILTER_FAILED',e.name||'RequestError')}
 finally{button.disabled=false;clear.disabled=false}
}
$('label-apply').onclick=()=>applyIssueLabel($('issue-label').value);
$('label-clear').onclick=()=>applyIssueLabel('');
$('issue-label').onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();$('label-apply').click()}};
$('issue-label').oninput=()=>{
 $('label-summary').textContent='Label edited. Click Apply label to filter the local snapshot; synchronize selected projects to run a targeted GitLab label search if needed.';
};
$('label-discover').onclick=async()=>{
 const ids=[...document.querySelectorAll('.project-choice:checked')].map(x=>Number(x.value)).filter(x=>Number.isSafeInteger(x)&&x>0);
 const box=$('label-results'),button=$('label-discover');
 if(!ids.length){notify('Select source Git.mil projects before discovering labels.',true);return}
 button.disabled=true;box.textContent='Reading existing Git.mil issue labels…';
 try{
  const r=await fetch('/api/gitmil/labels',{method:'POST',headers:{'Content-Type':'application/json','X-Cascade-Intent':'list-labels'},body:JSON.stringify({projectIds:ids}),signal:AbortSignal.timeout?AbortSignal.timeout(35000):undefined});
  const d=await r.json();if(!r.ok)throw Error(d.error||'Could not discover labels');
  const labels=arr(d.Labels);
  $('issue-label-options').innerHTML=labels.map(v=>'<option value="'+esc(v)+'"></option>').join('');
  box.textContent=labels.length+' distinct Git.mil labels found'+(arr(d.ProjectErrors).length?' ('+arr(d.ProjectErrors).length+' project(s) could not be queried)':'')+'. Choose one above and click Apply label.';
  diagnostic('ISSUE_LABELS_DISCOVERED','Count: '+labels.length);
 }catch(e){box.textContent='Label discovery failed: '+e.message;notify(e.message,true)}
 finally{button.disabled=false}
};
loadConfig();
$('end-session').onclick=async()=>{
 if(typeof window.confirm==='function' && !window.confirm('Stop the local Cascade PowerShell engine? The browser will no longer be connected.'))return;
 const button=$('end-session');button.disabled=true;
 try{
  const r=await fetch('/api/session/stop',{method:'POST',headers:{'Content-Type':'application/json','X-Cascade-Intent':'end-session'},body:'{}',signal:AbortSignal.timeout?AbortSignal.timeout(5000):undefined});
  const body=await r.json();
  if(!r.ok)throw Error(body.error||'Shutdown request rejected');
  diagnostic('SESSION_STOP_REQUESTED','Operator ended local engine');
  $('api-status').textContent='Cascade engine stopped. Close this tab or restart Cascade-GUI.bat.';
  state.server=false;
  notify('Cascade engine stopped. You can close this tab.');
 }catch(e){button.disabled=false;notify('Could not end session: '+e.message,true)}
};
$('record-start').onclick=()=>{recorder.sessionId='s-'+Date.now().toString(36);recorder.started=new Date().toISOString();recorder.events=[];setRecording(true);record('SESSION_START',{sessionId:recorder.sessionId});notify('Session recording started')};
$('record-stop').onclick=()=>{record('SESSION_STOP');setRecording(false);notify('Session recording stopped')};
$('record-export').onclick=async()=>{if(state.server){try{const r=await fetch('/api/diagnostics');if(r.ok){const d=await r.json();state.serverDiagnostics=arr(d.entries)}}catch(_){}}exportRecording()};
$('record-reset').onclick=()=>{setRecording(false);recorder.events=[];recorder.sessionId=null;recorder.started=null;$('record-count').textContent='0 events recorded';$('record-output').textContent='';notify('Recording cleared')};
setRecording(false);
render();page('gitmil');ping();refreshIntegrations();document.querySelectorAll('button').forEach(b=>b.addEventListener('click',()=>{diagnostic('BUTTON_CLICK',b.id||b.dataset.page||b.dataset.go||'button');if($('diagnostics').classList.contains('active'))renderDiagnostics()}));
})();