/* Leadership dashboard: descriptive metrics only, based on current imported plan.
 * A closed GitLab issue is NOT proof of acceptance/delivery.
 */
(function(){
'use strict';
const $=id=>document.getElementById(id);
const escape=s=>String(s==null?'':s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const arr=x=>Array.isArray(x)?x:x==null?[]:[x];
function compute(plan){
 const issues=arr(plan?.Issues),hierarchy=arr(plan?.Hierarchy),scope=plan?.Scope||null,topology=plan?.TopologyAudit||null;
 const findings=arr(topology?.Findings),types={},statuses={};
 for(const node of hierarchy){
  const sourceType=String(node.Type||'UNCLASSIFIED').toUpperCase();const type=sourceType==='UNCLASSIFIED'?'Type not assigned':sourceType;types[type]=(types[type]||0)+1;
 }
 for(const issue of issues){
  const raw=String(issue.state||'unknown').toLowerCase();const status=raw==='opened'||raw==='open'?'Open':raw==='closed'?'Closed (not verified)':'Unknown';
  statuses[status]=(statuses[status]||0)+1;
 }
 const projects=arr(plan?.Projects);
 const affected=new Set(findings.map(f=>String(f.Key)));
 const severity={ERROR:0,WARNING:0,INFO:0};
 for(const finding of findings){
  const code=String(finding.Severity||'INFO').toUpperCase();
  severity[code]=(severity[code]||0)+1;
 }
 const major=[];
 const buckets=new Map();
 for(const f of findings){
  const code=String(f.Code||'OTHER')==='UNCLASSIFIED_TYPE'?'TYPE_NOT_ASSIGNED':String(f.Code||'OTHER');
  const b=buckets.get(code)||{code,count:0,explanation:String(f.Explanation||''),severity:String(f.Severity||'INFO')};
  b.count++;buckets.set(code,b);
 }
 major.push(...Array.from(buckets.values()).sort((a,b)=>{
  const prio={ERROR:3,WARNING:2,INFO:1};
  return (prio[b.severity]||0)-(prio[a.severity]||0)||b.count-a.count
 }).slice(0,7));
 const total=issues.length;
 const noFindings=topology?Math.max(0,total-affected.size):null;
 const closed=statuses['Closed (not verified)']||0;
 const sourceIssues=scope?.SourceIssueCount??total;
 return {
  hasPlan:!!plan,hasTopology:!!topology,
  label:String(scope?.Label||''),
  issueCount:total,sourceIssues,
  projectCount:projects.length,
  typed:hierarchy.filter(n=>n.Type&&String(n.Type).toUpperCase()!=='UNCLASSIFIED').length,
  unclassified:types["Type not assigned"]||0,
  noFlaggedFindings:noFindings,
  closedIssues:closed,
  rawCommits:arr(plan?.Activity).length,
  hiddenEvidence:Number(scope?.ExcludedActivityCount)||0,
  topologyErrors:severity.ERROR,topologyWarnings:severity.WARNING,topologyInfo:severity.INFO,
  statuses,types,major,
  coverageWarning:String(plan?.CoverageWarning||scope?.Warning||''),
  sourceTruncated:!!plan?.TruncatedPotentially,
  generated:plan?.GeneratedUtc||plan?.RunId||null
 };
}
const percent=(x,denom)=>Math.min(100,Math.round((x/Math.max(1,denom))*100));
function bars(obj){
 const vals=Object.entries(obj).filter(([,v])=>v>0);
 if(!vals.length)return '<p class="muted">No work-item evidence is loaded for this view.</p>';
 const max=Math.max(1,...vals.map(([,v])=>v));
 return vals.sort((a,b)=>b[1]-a[1]).map(([label,v])=>
  '<div class="dash-bar"><div class="dash-bar-line"><span>'+escape(label)+'</span><b>'+v+'</b></div>'+
  '<div class="dash-bar-track"><div style="width:'+percent(v,max)+'%" class="dash-bar-fill"></div></div></div>'
 ).join('');
}
function card(label,value,note,go){
 const title=value==null?'—':String(value);
 return '<button type="button" class="dashboard-metric" data-dash-go="'+escape(go||'hierarchy')+'">'+
  '<span>'+escape(label)+'</span><strong>'+escape(title)+'</strong><small>'+escape(note)+'</small></button>';
}
function render(plan){
 const metrics=compute(plan);
 const root=$('leadership-kpis');
 if(!root)return metrics;
 $('leadership-scope').textContent=metrics.hasPlan?
  'Current view: '+(metrics.label?('exact GitLab label "'+metrics.label+'"'):'all imported issue labels')+
  ' · '+metrics.issueCount+' shown from '+metrics.sourceIssues+' imported issues · '+metrics.projectCount+' source project(s).':
  'No real Git.mil plan is loaded. Synchronize projects to populate this dashboard. Demo and offline plan figures are not verified delivery results.';
 root.innerHTML=[
  card('Imported issues',metrics.issueCount,'Exact label-scoped set','hierarchy'),
  card('Work items with assigned types',metrics.typed,'Recorded in metadata or type label','hierarchy'),
  card('Type not assigned',metrics.unclassified,'Needs work-item type review','intelligence'),
  card('Topology errors',metrics.hasTopology?metrics.topologyErrors:null,'Declared metadata relationships only','hierarchy'),
  card('Topology warnings',metrics.hasTopology?metrics.topologyWarnings:null,'Review parent IDs and schema','intelligence'),
  card('Closed GitLab issues',metrics.closedIssues,'Closure ≠ verified acceptance','hierarchy'),
  card('Observed commits',metrics.rawCommits,'Only safely attributed imported evidence','evidence'),
  card('Verified completions','—','No accepted delivery evidence available','intelligence')
 ].join('');
 $('leadership-types').innerHTML=bars(metrics.types);
 $('leadership-status').innerHTML=bars(metrics.statuses);
 $('leadership-risks').innerHTML=metrics.major.map(f=>
  '<div class="dashboard-risk"><span class="tag '+(f.severity==='ERROR'?'warn':'')+'">'+escape(f.severity)+'</span>'+
  '<div><b>'+escape(f.code)+'</b><small>'+f.count+' issue finding(s). '+escape(f.explanation)+'</small></div></div>'
 ).join('')||'<p class="muted">No topology findings in the current imported view. Native GitLab parent links may not yet be represented.</p>';
 $('leadership-coverage').innerHTML=[
  '<p><b>Projects scanned:</b> '+metrics.projectCount+'</p>',
  '<p><b>Imported source issues:</b> '+metrics.sourceIssues+'</p>',
  '<p><b>Label-visible issues:</b> '+metrics.issueCount+'</p>',
  '<p><b>Project-level commits omitted:</b> '+metrics.hiddenEvidence+' (unattributed to this label)</p>',
  '<p><b>Items without reported topology findings:</b> '+(metrics.noFlaggedFindings==null?'not assessed':metrics.noFlaggedFindings)+' — not a quality certification</p>',
  metrics.coverageWarning?'<p>'+escape(metrics.coverageWarning)+'</p>':'',
  '<p>Delivery velocity, labor hours, budget variance, and verified acceptance are not inferable from this snapshot.</p>'
 ].join('');
 $('leadership-caveat').textContent='Metrics reflect imported Git.mil data, not verified team throughput. Missing native Epic relationships, paging limits and closed-but-unaccepted issues can affect interpretations.';
 return metrics;
}
const api=Object.freeze({compute,render});
if(typeof window!=='undefined')window.CascadeLeadershipDashboard=api;
if(typeof module!=='undefined'&&module.exports)module.exports=api;
})();
