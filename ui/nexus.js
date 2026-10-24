/* Cascade Nexus workspace. ES2018 browser-only; no CDN, no third-party runtime. */
(function(){'use strict';
const el=id=>document.getElementById(id);
const bridge=window.CascadeNexusBridge;
if(!bridge){throw Error('CascadeNexusBridge not initialized. Load app.js before nexus.js.');}
const escapeHtml=x=>String(x==null?'':x).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const asArray=x=>Array.isArray(x)?x:[];
const getNodes=()=>{const p=bridge.getPlan()||{};return asArray(p.Hierarchy).length?asArray(p.Hierarchy):asArray(p.Issues).map(i=>({Id:'gitlab:'+i.project_id+':'+i.iid,ParentId:'',Title:i.title,Type:'UNCLASSIFIED',State:i.state,ProjectId:i.project_id,Url:i.web_url}));};
let sortKey='Title',sortDirection=1,visible=[];
function grid(){const q=el('nexus-search').value.trim().toLowerCase(),type=el('nexus-type').value;
const nodes=getNodes(),types=[...new Set(nodes.map(n=>String(n.Type||'UNCLASSIFIED')))].sort();
const current=el('nexus-type').value;el('nexus-type').innerHTML='<option value="">All types</option>'+types.map(t=>'<option value="'+escapeHtml(t)+'">'+escapeHtml(t)+'</option>').join('');el('nexus-type').value=types.includes(current)?current:'';
visible=nodes.filter(n=>(!type||String(n.Type||'UNCLASSIFIED')===type)&&[n.Title,n.Type,n.State,n.ProjectId,n.ParentId].join(' ').toLowerCase().includes(q)).sort((a,b)=>sortDirection*String(a[sortKey]||'').localeCompare(String(b[sortKey]||''),undefined,{numeric:true}));
el('nexus-count').textContent=visible.length+' / '+nodes.length+' items';
el('nexus-rows').innerHTML=visible.slice(0,1500).map(n=>{const url=String(n.Url||'');const title=url.startsWith('https://web.git.mil/')?'<a target="_blank" rel="noopener noreferrer" href="'+escapeHtml(url)+'">'+escapeHtml(n.Title)+'</a>':escapeHtml(n.Title);return '<tr><td>'+escapeHtml(n.Type||'—')+'</td><td>'+title+'</td><td>'+escapeHtml(n.State||'—')+'</td><td>'+escapeHtml(n.ProjectId||'—')+'</td><td>'+escapeHtml(n.ParentId||'—')+'</td></tr>';}).join('')||'<tr><td colspan="5">No matching work items. Synchronize or import a dataset.</td></tr>';
}
function graph(){const nodes=getNodes().slice(0,Math.max(10,Math.min(150,Number(el('graph-limit').value)||70)));const stage=el('graph-stage');if(!nodes.length){stage.textContent='No hierarchy data available.';return;}
const ids=new Map(nodes.map((n,i)=>[String(n.Id),i]));const levels=new Array(nodes.length).fill(0);
for(let i=0;i<nodes.length;i++){let cursor=nodes[i],visited=new Set([String(cursor.Id)]);for(let d=0;d<10;d++){const pi=ids.get(String(cursor.ParentId));if(pi===undefined||visited.has(String(nodes[pi].Id)))break;levels[i]++;cursor=nodes[pi];visited.add(String(cursor.Id));}}
const byLevel={};levels.forEach((d,i)=>(byLevel[d]||(byLevel[d]=[])).push(i));const width=Math.max(1100,Math.max(...levels)*220+500),height=Math.max(480,...Object.values(byLevel).map(a=>a.length*78+100));const pos=nodes.map((_,i)=>({x:100+levels[i]*220,y:75+byLevel[levels[i]].indexOf(i)*78}));
let svg='<svg viewBox="0 0 '+width+' '+height+'" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="Work item parent child graph">';
nodes.forEach((n,i)=>{const pi=ids.get(String(n.ParentId));if(pi!==undefined){const a=pos[pi],b=pos[i];svg+='<path class="graph-edge" fill="none" d="M'+a.x+' '+a.y+' C'+(a.x+100)+' '+a.y+' '+(b.x-100)+' '+b.y+' '+b.x+' '+b.y+'"/>';}});
nodes.forEach((n,i)=>{const p=pos[i],label=String(n.Title||'Untitled').slice(0,23);svg+='<g class="graph-node" tabindex="0" role="button" data-index="'+i+'" transform="translate('+p.x+' '+p.y+')"><circle r="16"/><text x="24" y="4">'+escapeHtml(label)+'</text></g>';});svg+='</svg>';stage.innerHTML=svg;
function show(index){const n=nodes[index];if(!n)return;el('graph-detail').textContent=[n.Type||'UNCLASSIFIED',n.Title||'',n.State||'',n.Id||''].filter(Boolean).join(' · ');}
stage.querySelectorAll('.graph-node').forEach(g=>{g.onclick=()=>show(Number(g.dataset.index));g.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();show(Number(g.dataset.index));}};});
}
function timeline(){const dates={};asArray(bridge.getPlan()&&bridge.getPlan().Activity).forEach(a=>{const d=String(a.Timestamp||a.Date||'').slice(0,10);if(/^\d{4}-\d{2}-\d{2}$/.test(d))dates[d]=(dates[d]||0)+1;});const rows=Object.entries(dates).sort((a,b)=>b[0].localeCompare(a[0])).slice(0,90),max=Math.max(1,...rows.map(x=>x[1]));el('timeline-stage').innerHTML=rows.map(([date,n])=>'<div class="timeline-row"><small>'+escapeHtml(date)+'</small><div class="timeline-bar"><div style="width:'+(100*n/max).toFixed(1)+'%"></div></div><b>'+n+'</b></div>').join('')||'<p>No timestamped commit activity in the current dataset.</p>';}
function refresh(){grid();graph();timeline();}
function exportCsv(){const columns=['Type','Title','State','ProjectId','ParentId','Id'];const quote=x=>'"'+String(x==null?'':x).replace(/"/g,'""').replace(/^[=+@\-\t\r]/,'\'$&')+'"';const content=[columns.join(','),...visible.map(n=>columns.map(c=>quote(n[c])).join(','))].join('\r\n');bridge.download('cascade-nexus-work-items.csv',content,'text/csv;charset=utf-8');}
window.addEventListener('cascade:plan-changed',refresh);
window.addEventListener('cascade:page-changed',e=>{if(e.detail && e.detail.page==='nexus')refresh();});
document.querySelectorAll('[data-nexus-tab]').forEach(b=>b.onclick=()=>{document.querySelectorAll('[data-nexus-tab]').forEach(x=>x.classList.toggle('selected',x===b));document.querySelectorAll('.nexus-view').forEach(v=>v.hidden=v.id!=='nexus-'+b.dataset.nexusTab);});
document.querySelectorAll('[data-nexus-sort]').forEach(b=>b.onclick=()=>{const key=b.dataset.nexusSort;if(key===sortKey)sortDirection*=-1;else{sortKey=key;sortDirection=1;}grid();});
el('nexus-search').oninput=grid;el('nexus-type').onchange=grid;el('graph-limit').onchange=graph;el('nexus-refresh').onclick=refresh;el('nexus-export').onclick=exportCsv;
refresh();
})();
