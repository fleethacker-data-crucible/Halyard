'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const root=path.resolve(__dirname,'..');
const source=fs.readFileSync(path.join(root,'ui/example-work-items.js'),'utf8');
const html=fs.readFileSync(path.join(root,'ui/index.html'),'utf8');
assert.ok(html.includes('data-page="examples"'),'Example tab must be in navigation');
assert.ok(html.includes('id="example-rows"'),'Example hierarchy table must exist');
assert.match(html,/example-work-items\.js/);
const context={window:{},module:{exports:{}}};
vm.runInNewContext(source,context,{filename:'ui/example-work-items.js'});
const catalog=context.module.exports;
assert.equal(catalog.synthetic,true);
assert.equal(catalog.workItems.length,46);
assert.deepEqual(JSON.parse(JSON.stringify(catalog.counts)),{THEME:1,EPIC:3,FEATURE:6,STORY:12,TASK:24});
const nodes=new Map(catalog.workItems.map(x=>[x.id,x]));
assert.equal(nodes.size,catalog.workItems.length,'Every example needs unique stable metadata ID');
const parents={THEME:null,EPIC:'THEME',FEATURE:'EPIC',STORY:'FEATURE',TASK:'STORY'};
for(const item of catalog.workItems){
 assert.ok(item.id&&item.title&&item.description&&item.metadataBlock);
 assert.match(item.description,/^~~~cascade-meta\n/);
 assert.match(item.description,/\n~~~\n\n/);
 assert.match(item.bodyMarkdown,/## /,'Every item should have defined body sections');
 assert.ok(item.bodyMarkdown.length>280,'No skeletal example descriptions');
 assert.ok(item.labels.includes(catalog.sampleLabel),'Every example must carry the same actual labels-array team label');
 assert.ok(item.labels.includes('type::'+item.type.toLowerCase()));
 assert.equal(item.meta.id,item.id);
 assert.equal(item.meta.type,item.type);
 assert.equal(item.meta.area,'data-crucible');
 const required=catalog.schemaRules.required[item.type];
 assert.ok(required?.length>0,'Schema must define mandatory fields for '+item.type);
 for(const field of required){
  assert.ok(Object.prototype.hasOwnProperty.call(item.meta,field),'Missing '+field+' on '+item.id);
  assert.ok(String(item.meta[field]).trim(),'Blank '+field+' on '+item.id);
  assert.ok(item.metadataBlock.includes(field+': '+item.meta[field]));
 }
 if(item.type==='THEME')assert.equal(item.parentId,'');
 else {
  const parent=nodes.get(item.parentId);
  assert.ok(parent,'Missing parent '+item.parentId+' on '+item.id);
  assert.equal(parent.type,parents[item.type],'Invalid parent type for '+item.id);
  assert.equal(item.meta.parent,item.parentId);
  assert.equal(item.meta.theme,'THEME-example-data-crucible');
 }
 if(item.type==='EPIC'){
  assert.match(item.bodyMarkdown,/## Outcome Statement/);
  assert.match(item.bodyMarkdown,/## Success Picture/);
 }
 if(item.type==='FEATURE'){
  assert.match(item.bodyMarkdown,/## Feature Done-When/);
  assert.match(item.bodyMarkdown,/## Proposal for Work/);
 }
 if(item.type==='STORY'){
  assert.match(item.bodyMarkdown,/## Story Text/);
  assert.match(item.bodyMarkdown,/## Acceptance Criteria/);
  assert.match(item.bodyMarkdown,/## Definition of Done/);
 }
 if(item.type==='TASK'){
  assert.match(item.bodyMarkdown,/## Verification|## Completion and Verification/);
 }
 let count=0,at=item;
 while(at.parentId){
  at=nodes.get(at.parentId);
  if(!at||++count>4)throw Error('Missing parent or hierarchy cycle');
 }
}
assert.equal(context.window.CascadeExampleCatalog,catalog);
assert.ok(!/https:\/\/web\.git\.mil\/api\/v4/.test(source),'Sample source must not invoke Git.mil');
console.log('PASS: 46 complete synthetic records, explicit labels, all type-specific mandatory metadata, full descriptions and valid 5-level parent graph.');
