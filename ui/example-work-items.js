/* Cascade illustrative work-item hierarchy. Entirely local/static; NO Git.mil writes.
 * Conceptual Cascade Theme > Epic > Feature > Story > Task model. Native GitLab
 * issue/epic types and hierarchy support may differ by server version/tier.
 */
(function(){
 'use strict';
 var teamLabel='team::Data-Crucible'; // EXAMPLE ONLY; not assumed to exist in Git.mil.
 var themeId='THEME-example-data-crucible';
 var required={
  THEME:['type','id','state','status','area'],
  EPIC:['type','id','theme','state','area'],
  FEATURE:['type','id','parent','theme','state','area','effort'],
  STORY:['type','id','parent','theme','state','area','points','sprint'],
  TASK:['type','id','parent','theme','state','area']
 };
 var rows=[];
 var timestamp='Illustrative only — not a live Git.mil issue or recorded milestone.';
 function fence(meta) {
  return ['~~~cascade-meta'].concat(Object.keys(meta).map(function(k){return k+': '+meta[k]})).concat(['~~~']).join('\n');
 }
 function add(type,id,parent,title,attrs,body,extra){
  var meta={type:type,id:id};
  if(parent){meta.parent=parent;}
  if(type!=='THEME'){meta.theme=themeId;}
  meta.state=(type==='THEME'?'active':type==='TASK'?'ready':'ready');
  meta.area='data-crucible';
  Object.keys(attrs||{}).forEach(function(k){meta[k]=attrs[k];});
  var labels=[teamLabel,'type::'+type.toLowerCase(),'workflow::ready'];
  var metadataBlock=fence(meta);
  var entry={
   id:id,parentId:parent||'',type:type,title:title,
   labels:labels,meta:meta,metadataBlock:metadataBlock,
   bodyMarkdown:body,description:metadataBlock+'\n\n'+body,
   depth:{THEME:0,EPIC:1,FEATURE:2,STORY:3,TASK:4}[type],
   note:type==='TASK'?'Standalone Task is optional; a Story checklist is the preferred default.':timestamp
  };
  if(extra){Object.keys(extra).forEach(function(k){entry[k]=extra[k]});}
  rows.push(entry);
 }
 var themeTitle='Data-Crucible Demonstration Portfolio';
 add('THEME',themeId,'',themeTitle,
  {status:'Active',owner:'Demo Portfolio Lead',review_cadence:'monthly'},
  [
   '# Portfolio purpose',
   'Deliver secure, maintainable data services that let authorized analysts discover, reconcile and use trustworthy operational data.',
   '',
   '## Outcome and beneficiaries',
   'Authorized analysts receive understandable, traceable datasets with defined ownership and a repeatable delivery process.',
   '',
   '## Success measures',
   '- All sample data sources have documented ownership and freshness expectations.',
   '- Critical ingestion paths expose validation failures before data is promoted.',
   '- Each demonstration delivery has a linked issue, a test record and acceptance evidence.',
   '',
   '## Boundaries and dependencies',
   'Only synthetic, approved sample data is used. Access and security reviews precede a hypothetical production release.',
   '',
   '## Review cadence',
   'Portfolio lead reviews Epic status monthly; Teams review Features at each planning cycle.',
   '',
   '## Definition of success',
   'Each child Epic has measurable outcomes, and its Features demonstrate the outcomes with approved evidence.'
  ].join('\n'));
 var epics=[
  {slug:'ingestion',title:'Reliable Data Ingestion',outcome:'Ingest verified sample records into repeatable Bronze landing workflows.',
   measures:['Every accepted feed includes a schema version and owner.','Schema failures are quarantined with actionable diagnostics.'],
   risks:'Unannounced upstream changes; mitigate with contract checks and visible quarantine metrics.',
   features:[
    {slug:'feed-contract',title:'Source Feed Contracts',purpose:'Document and validate each approved sample source before ingest.',
     done:['Schema contract rejects malformed sample fields with clear reasons.','Owners can review successful and failed feed validations.'],
     stories:[
      {slug:'required-fields',title:'Validate Required Source Fields',role:'data steward',goal:'reject records that lack mandatory identifiers',benefit:'invalid records cannot enter the Bronze demonstration feed',
       criteria:['Samples lacking source_id are rejected with a deterministic reason code.','Valid records preserve schema_version and source_id in the validation output.']},
      {slug:'schema-change',title:'Detect Schema Version Changes',role:'pipeline operator',goal:'see when a feed schema changes',benefit:'upstream changes are reviewed before promotion',
       criteria:['Changing schema_version triggers a recorded compatibility check.','A breaking change marks the sample batch as requiring review.']}
     ]},
    {slug:'bronze-ledger',title:'Bronze Ingestion Ledger',purpose:'Trace sample ingestion batches and their record counts.',
     done:['A batch record tracks start, finish and validation outcomes.','A reviewer can follow each rejected record to its source batch.'],
     stories:[
      {slug:'batch-audit',title:'Record Batch Ingestion Outcomes',role:'watchstanding analyst',goal:'find the status of a sample ingestion batch',benefit:'recovery work uses a reproducible audit trail',
       criteria:['Every batch receives a unique correlation ID and outcome.','Successful and rejected counts reconcile to the input count.']},
      {slug:'retry-idempotency',title:'Retry Failed Sample Batches Safely',role:'pipeline operator',goal:'retry failed demonstrations without duplicates',benefit:'the Bronze ledger remains consistent',
       criteria:['A repeated request for a completed batch does not duplicate output records.','A failed batch retry records a new attempt linked to its original ID.']}
     ]}
   ]},
  {slug:'platform',title:'Secure Platform Foundations',outcome:'Provide a repeatable, least-privilege environment for the demonstration data services.',
   measures:['Every sample workload has an accountable service identity.','Configuration changes can be reproduced and reviewed.'],
   risks:'Overly broad permissions; mitigate through explicit test cases and review gates.',
   features:[
    {slug:'service-access',title:'Service Identity and Access',purpose:'Define service roles and verify sample permissions.',
     done:['Approved example roles provide only the documented operations.','Denied operations produce auditable explanations.'],
     stories:[
      {slug:'read-policy',title:'Limit Sample Reader Permissions',role:'platform engineer',goal:'grant a reader access only to published sample objects',benefit:'unauthorized modifications are denied',
       criteria:['The reader role may list and read approved test objects.','The reader role cannot update or delete any sample object.']},
      {slug:'audit-denials',title:'Audit Access Denials',role:'security reviewer',goal:'review denied example access attempts',benefit:'the expected enforcement path is visible',
       criteria:['Each denied test action records the attempted operation and test role.','Audit output excludes synthetic credential material and secret values.']}
     ]},
    {slug:'environment-repeat',title:'Reproducible Test Environments',purpose:'Provision and validate the same approved example configuration repeatedly.',
     done:['A clean test environment reproduces the configuration without manual tuning.','A validation report flags configuration drift and rollback options.'],
     stories:[
      {slug:'config-manifest',title:'Define a Reproducible Configuration Manifest',role:'release engineer',goal:'capture approved sample environment settings',benefit:'re-creation is auditable',
       criteria:['The manifest declares example service settings and their version.','A schema check rejects unknown or contradictory configuration fields.']},
      {slug:'drift-report',title:'Report Sample Environment Drift',role:'platform operator',goal:'compare test settings with a versioned manifest',benefit:'unexpected changes receive timely review',
       criteria:['The report identifies which declared settings differ.','The report includes the manifest version and timestamp used for comparison.']}
     ]}
   ]},
  {slug:'delivery',title:'Delivery Assurance and Traceability',outcome:'Ensure demonstration capabilities are planned, verified and accepted with traceable evidence.',
   measures:['Each Story maps to a Feature and has observable acceptance criteria.','Release decisions reference a test result and reviewer evidence.'],
   risks:'Ticket metadata drift; mitigate through a preflight schema audit and human approvals.',
   features:[
    {slug:'work-trace',title:'Work-Item Traceability',purpose:'Present sample Theme to Task links with stable identifiers and reviewable coverage.',
     done:['Every sample work item can be traced to its declared parent.','Topology validation detects missing and circular relationships.'],
     stories:[
      {slug:'parent-chain',title:'Navigate the Declared Parent Chain',role:'delivery lead',goal:'inspect each Story parent and Epic lineage',benefit:'progress reports stay traceable',
       criteria:['Following any Story parent reaches its Feature, Epic and Theme.','A parent reference outside the selected label is marked as context, not silently reassigned.']},
      {slug:'schema-readiness',title:'Review Required Metadata Before Planning',role:'product owner',goal:'review incomplete sample work-item fields',benefit:'planning uses complete and understandable requirements',
       criteria:['Missing required metadata is reported by work-item type.','A user can inspect the original description without modifying the source.']}
     ]},
    {slug:'release-evidence',title:'Release Evidence Desk',purpose:'Package test and review evidence for an illustrative release decision.',
     done:['A release packet links the approved Stories and test outcomes.','Unverified delivery claims are identified as unverified.'],
     stories:[
      {slug:'test-packet',title:'Assemble a Test Evidence Packet',role:'release reviewer',goal:'collect the evidence for a sample release',benefit:'approval does not depend on unsupported claims',
       criteria:['The packet lists related Story IDs and pass/fail outcomes.','Missing tests are shown as not verified, not automatically marked passed.']},
      {slug:'acceptance-record',title:'Record Example Acceptance Decisions',role:'product owner',goal:'record human approval for a sample capability',benefit:'completion reflects an explicit decision',
       criteria:['Each approval records a reviewer role, timestamp and outcome.','A rejected capability remains unaccepted until a new review occurs.']}
     ]}
   ]}
 ];
 epics.forEach(function(epic,ei){
  var eid='EPIC-example-'+epic.slug;
  add('EPIC',eid,themeId,epic.title,
   {status:'On track',owner:'Demo Epic Steward',target_window:'Demonstration quarter'},
   [
    '## Outcome Statement',epic.outcome,'',
    '## Target Window','Illustrative planning quarter; no real commitment or scheduled release.','',
    '## Success Picture',epic.measures.map(function(x){return '- '+x}).join('\n'),'',
    '## Major Risks and Mitigations',epic.risks,'',
    '## Feature ownership','A demo Feature owner maintains the work package and accepts Story-level evidence.','',
    '## Review and dependencies','Review feature completeness at each planning cycle; approval remains a human decision.'
   ].join('\n'));
  epic.features.forEach(function(feature,fi){
   var fid='FEATURE-example-'+epic.slug+'-'+feature.slug;
   add('FEATURE',fid,eid,feature.title,
    {effort:'Medium',artifact_level:'Proposal for Work',owner:'Demo Feature Owner',pfw_reference:'EXAMPLE-PFW-'+(ei+1)+'-'+(fi+1)},
    [
     '## Short Description',feature.purpose,'',
     '## Proposal for Work (PfW) — illustrative summary',
     '**Business purpose:** '+feature.purpose,
     '**In scope:** Sample configurations, sample test records and documented acceptance evidence.',
     '**Out of scope:** Real systems, credentials, production updates and confidential data.','',
     '## Feature Done-When',
     feature.done.map(function(x){return '- [ ] '+x}).join('\n'),'',
     '## Delivery approach','Implement the linked Stories in a controlled test environment and review each acceptance check.','',
     '## Dependencies','Depends on the parent Epic outcome, approved demonstration data and reviewer availability.','',
     '## Validation and evidence','Collect synthetic test outputs, peer-review evidence and an explicit Feature-owner sign-off.','',
     '## Risks and rollback','Sample data or configuration changes are reversible by restoring the approved demonstration fixture.'
    ].join('\n'));
   feature.stories.forEach(function(story,si){
    var sid='STORY-example-'+epic.slug+'-'+feature.slug+'-'+story.slug;
    var labelTitle=story.title;
    add('STORY',sid,fid,labelTitle,
     {points:'3',sprint:'DEMO-SPRINT-01',owner:'Demo Story Owner'},
     [
      '## Story Text','As a '+story.role+', I want to '+story.goal+' so that '+story.benefit+'.','',
      '## Acceptance Criteria',
      story.criteria.map(function(x){return '- [ ] '+x}).join('\n'),'',
      '## Constraints','Use only public/synthetic input. The example must run without storing any CAC PIN, token or production secret.','',
      '## Recommended Tasks','- [ ] Implement a reproducible example path.','- [ ] Execute positive and negative tests.',
      '- [ ] Obtain a second-person review.','- [ ] Record evidence and confirm the human acceptance decision.','',
      '## Definition of Done','The two acceptance checks are demonstrated, test results are attached and the Story owner approves the outcome.','',
      '## Notes','This is a sample user Story. Its points and sprint designation are illustrative, not AI estimates.'
     ].join('\n'));
    var taskDefs=[
     {key:'build',title:'Implement '+story.title,scope:'Implement the narrow demonstration path in a test fixture.',
      done:'The sample produces the intended output using only synthetic data.'},
     {key:'verify',title:'Verify '+story.title,scope:'Execute the Story acceptance checks, capture results and request review.',
      done:'Recorded results demonstrate both acceptance checks with one positive and one negative example.'}
    ];
    taskDefs.forEach(function(task,ti){
     var tid='TASK-example-'+epic.slug+'-'+feature.slug+'-'+story.slug+'-'+task.key;
     add('TASK',tid,sid,task.title,
      {owner:'Demo Task Assignee',estimate:'illustrative-single-day'},
      [
       '## Objective',task.scope,'',
       '## Parent Story',sid+' — '+story.title,'',
       '## Procedure',
       '- Confirm the acceptance criteria in the parent Story before starting.',
       '- Use an isolated synthetic fixture and record the inputs used.',
       '- Complete '+(ti===0?'the smallest working implementation and a reproducible example.':'both acceptance checks and attach a peer-reviewed result.') ,'',
       '## Completion and Verification',task.done,'',
       '## Evidence','Provide a sample test output, a short reviewer note and a traceable reference to the parent Story.','',
       '## Constraints','Do not connect to real Git.mil or ARC to run this example. Human approval is still required.'
      ].join('\n'));
    });
   });
  });
 });
 var typeCounts={};
 rows.forEach(function(n){typeCounts[n.type]=(typeCounts[n.type]||0)+1});
 var artifact={
  version:'1.0',
  title:'Data-Crucible sample delivery hierarchy',
  synthetic:true,
  warning:'EXAMPLE DATA ONLY — no tickets, dates, credentials or actual team labels are claimed to exist in Git.mil.',
  sampleLabel:teamLabel,
  schemaRules:{
   required:required,
   typeOrder:['THEME','EPIC','FEATURE','STORY','TASK'],
   metadataFence:'~~~cascade-meta',
   parentPolicy:'Each child declares its stable parent ID; parent must resolve uniquely.',
   labelPolicy:'Each displayed sample item has an actual labels array containing '+teamLabel+'. A metadata area field alone never satisfies a GitLab issue-label filter.',
   taskPolicy:'Tasks are normally checklists inside Stories; standalone Task records here deliberately demonstrate the full conceptual hierarchy.',
   gitlabBoundary:'Cascade Theme/Epic are conceptual types in this example; native GitLab Epic/Work Items APIs may store those separately from project issues.'
  },
  counts:typeCounts,
  workItems:rows
 };
 if(typeof window!=='undefined'){window.CascadeExampleCatalog=artifact;}
 if(typeof module!=='undefined'&&module.exports){module.exports=artifact;}
})();
