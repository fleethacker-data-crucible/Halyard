/* Cascade example explorer: pure, local, synthetic, and independent of Git.mil.
 * The sample hierarchy is navigable (expand/collapse), not merely a flat list.
 */
(function(){
'use strict';
const el=id=>document.getElementById(id);
const safe=value=>String(value==null?'':value).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const catalog=()=>window.CascadeExampleCatalog||null;
const store={selected:'',expanded:new Set(),initialized:false};
const nodes=()=>new Map((catalog()?.workItems||[]).map(n=>[n.id,n]));
const children=()=>{
 const map=new Map();
 for(const item of catalog()?.workItems||[]){
  const parent=item.parentId||'';
  if(!map.has(parent))map.set(parent,[]);
  map.get(parent).push(item);
 }
 return map;
};
const labels=x=>(x.labels||[]).map(label=>{
 const value=String(label),i=value.indexOf('::');
 if(i<0)return '<span class="gitlab-label">'+safe(value)+'</span>';
 return '<span class="gitlab-label gitlab-scoped-label"><span>'+safe(value.slice(0,i))+'</span><span>'+safe(value.slice(i+2))+'</span></span>';
}).join(' ');
function initialize(){
 if(store.initialized)return;
 const list=catalog()?.workItems||[];
 for(const item of list){
  if(item.type==='THEME'||item.type==='EPIC')store.expanded.add(item.id);
 }
 store.selected=list[0]?.id||'';
 store.initialized=true;
}
function ancestry(id){
 const map=nodes(),chain=[],visited=new Set();let cursor=map.get(id);
 while(cursor&&!visited.has(cursor.id)){visited.add(cursor.id);chain.unshift(cursor);cursor=map.get(cursor.parentId)}
 return chain;
}
function reveal(id){for(const ancestor of ancestry(id).slice(0,-1))store.expanded.add(ancestor.id)}
function matches(item){
 const q=String(el('example-search')?.value||'').trim().toLowerCase();
 const type=String(el('example-type')?.value||'ALL');
 return (type==='ALL'||type===item.type)&&(!q||(item.title+' '+item.id+' '+item.parentId).toLowerCase().includes(q));
}
function parentButton(item){
 const parent=item.parentId;
 return parent?'<button type="button" class="example-id-link" data-example-parent="'+safe(parent)+'" aria-label="Navigate to parent '+safe(parent)+'">'+safe(parent)+'</button>':'<span class="muted">Root</span>';
}
function itemButton(item){return '<button type="button" class="example-item'+(store.selected===item.id?' is-selected':'')+'" data-example-id="'+safe(item.id)+'" aria-current="'+(store.selected===item.id?'true':'false')+'">'+safe(item.title)+'</button>'}
function drawTree(){
 const host=el('example-tree');if(!host)return;
 const map=children(),records=nodes();
 const filtering=String(el('example-search')?.value||'').trim()!==''||String(el('example-type')?.value||'ALL')!=='ALL';
 const visibility=new Map();
 function subtreeVisible(item){
  if(visibility.has(item.id))return visibility.get(item.id);
  const ok=matches(item)||(map.get(item.id)||[]).some(subtreeVisible);
  visibility.set(item.id,ok);return ok;
 }
 const seen=new Set();
 function renderNode(item,depth){
  if(seen.has(item.id)||depth>12)return '';
  seen.add(item.id);
  if(filtering&&!subtreeVisible(item))return '';
  const kids=(map.get(item.id)||[]).filter(k=>!filtering||subtreeVisible(k));
  const opened=filtering||store.expanded.has(item.id);
  return '<li class="example-tree-node" data-example-node="'+safe(item.id)+'">'+
    '<div class="example-tree-row'+(store.selected===item.id?' selected':'')+'">'+
    (kids.length?'<button type="button" class="example-tree-toggle" data-example-toggle="'+safe(item.id)+'" aria-label="'+(opened?'Collapse ':'Expand ')+safe(item.title)+'" aria-expanded="'+String(opened)+'">'+(opened?'▾':'▸')+'</button>':'<span class="example-tree-spacer"></span>')+
    '<span class="example-type-badge level-'+safe(item.type.toLowerCase())+'">'+safe(item.type)+'</span>'+
    itemButton(item)+'<small class="example-tree-id">'+safe(item.id)+'</small></div>'+
    (kids.length&&opened?'<ul class="example-tree-children">'+kids.map(k=>renderNode(k,depth+1)).join('')+'</ul>':'')+
   '</li>';
 }
 const roots=(map.get('')||[]).concat((catalog()?.workItems||[]).filter(x=>x.parentId&&!records.has(x.parentId)));
 host.innerHTML='<ul class="example-tree-root">'+roots.map(x=>renderNode(x,0)).join('')+'</ul>';
 if(!roots.length)host.textContent='No examples are available.';
}
function drawTable(){
 const catalogData=catalog(),tbody=el('example-rows'),summary=el('example-summary');
 if(!catalogData||!tbody||!summary)return;
 const list=catalogData.workItems||[],visible=list.filter(matches),count=catalogData.counts||{};
 summary.textContent=list.length+' synthetic work items: '+['THEME','EPIC','FEATURE','STORY','TASK'].map(t=>(count[t]||0)+' '+t.toLowerCase()+(count[t]===1?'':'s')).join(' · ')+'. '+visible.length+' match the table filter.';
 tbody.innerHTML=visible.map(n=>{
  const required=catalogData.schemaRules?.required?.[n.type]||[];
  const ok=required.every(k=>Object.prototype.hasOwnProperty.call(n.meta,k)&&String(n.meta[k]).trim());
  return '<tr><td><span class="example-type-badge level-'+safe(n.type.toLowerCase())+'">'+safe(n.type)+'</span> '+itemButton(n)+'<small class="example-tree-id">'+safe(n.id)+'</small></td>'+
   '<td>'+safe(n.type)+'</td><td>'+parentButton(n)+'</td><td class="example-labels">'+labels(n)+'</td>'+
   '<td><span class="tag '+(ok?'ok':'warn')+'">'+(ok?'Required fields present':'Needs review')+'</span></td></tr>';
 }).join('')||'<tr><td colspan="5">No example items match this filter.</td></tr>';
}
function drawDetail(){
 const data=catalog(),item=nodes().get(store.selected),host=el('example-detail');if(!host)return;
 if(!data||!item){host.textContent='Choose an example item to inspect its metadata.';return}
 const chain=ancestry(item.id);
 const required=data.schemaRules?.required?.[item.type]||[];
 const missing=required.filter(k=>!Object.prototype.hasOwnProperty.call(item.meta,k)||!String(item.meta[k]).trim());
 const crumbs=chain.map((x,i)=>i===chain.length-1?'<strong>'+safe(x.title)+'</strong>':
  '<button class="example-crumb" type="button" data-example-parent="'+safe(x.id)+'">'+safe(x.title)+'</button>').join(' <span aria-hidden="true">›</span> ');
 host.innerHTML='<nav class="example-breadcrumbs" aria-label="Selected example lineage">'+crumbs+'</nav>'+
  '<div class="example-inspector-title"><span class="example-type-badge level-'+safe(item.type.toLowerCase())+'">'+safe(item.type)+'</span><h3>'+safe(item.title)+'</h3></div>'+
  '<div class="example-inspector-meta"><span><b>ID:</b> <code>'+safe(item.id)+'</code></span><span><b>Parent ID:</b> '+parentButton(item)+'</span></div>'+
  '<div class="example-labels"><b>GitLab labels:</b> '+labels(item)+'</div>'+
  '<p><b>Schema:</b> '+(missing.length?'Needs: '+safe(missing.join(', ')):'Configured required fields present')+'</p>'+
  '<details class="example-section" open><summary>Metadata (cascade-meta)</summary><pre class="example-code">'+safe(item.metadataBlock)+'</pre></details>'+
  '<details class="example-section" open><summary>Work-item body and acceptance criteria</summary><pre class="example-code">'+safe(item.bodyMarkdown)+'</pre></details>'+
  '<details class="example-section"><summary>Complete copy-ready GitLab description</summary><pre class="example-code">'+safe(item.description)+'</pre></details>'+
  '<p class="muted">'+safe(item.note||'')+'</p>';
}
function render(){
 initialize();
 if(!catalog()?.workItems){const host=el('example-tree');if(host)host.textContent='Local example catalog unavailable';return}
 drawTree();drawTable();drawDetail();
}
function select(id){
 initialize();
 if(!nodes().has(id))return false;
 store.selected=id;reveal(id);render();
 return true;
}
function toggle(id){
 initialize();
 if(store.expanded.has(id))store.expanded.delete(id);else store.expanded.add(id);
 render();return store.expanded.has(id);
}
function expandAll(){initialize();for(const x of catalog()?.workItems||[])store.expanded.add(x.id);render()}
function collapseAll(){initialize();store.expanded.clear();render()}
function clicked(event){
 const target=event?.target,closest=sel=>target?.closest?.(sel);
 const toggleBtn=closest('[data-example-toggle]');
 if(toggleBtn){toggle(toggleBtn.dataset.exampleToggle);return}
 const button=closest('[data-example-id]')||closest('[data-example-parent]');
 if(button){select(button.dataset.exampleId||button.dataset.exampleParent)}
}
const api=Object.freeze({
 getCatalog:catalog,getActiveId:()=>store.selected,getExpandedIds:()=>Array.from(store.expanded),
 select,toggle,expandAll,collapseAll,render,handleClick:clicked,
 getHierarchy:()=>({items:catalog()?.workItems||[],byId:nodes(),children:children()})
});
if(typeof window!=='undefined')window.CascadeExampleExplorer=api;
if(typeof module!=='undefined'&&module.exports)module.exports=api;
})();
