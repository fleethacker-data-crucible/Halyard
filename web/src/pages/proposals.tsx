import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { LuArrowRight, LuCheck, LuChevronDown, LuChevronRight, LuPlay, LuSparkles, LuTrash2, LuWrench, LuX } from 'react-icons/lu';
import { navigate } from '../app';
import { DiffView } from '../components/diff';
import { Button, Checkbox, ConfirmDialog, Empty, Field, LevelChip, Mono, Notice, Page, Pill, Progress, Section, Select, cn, useToast } from '../components/ui';
import { describeError, post } from '../lib/api';
import { humanizeCode, relativeTime } from '../lib/format';
import { isLevel } from '../lib/levels';
import { useJob } from '../lib/jobs';
import { Batch, BatchSummary, ProposalPreview, type Job, type Proposal, type ProposalKind, type Status } from '../lib/schemas';
import { invalidate, useResource } from '../lib/useResource';
import { z } from 'zod';
import { Skeleton } from './overview';

function levelOf(p: Proposal): string {
  if (p.kind === 'featureEffort') return 'FEATURE';
  if (p.kind === 'storyPoints') return 'STORY';
  const prefix = p.itemCanonicalId.split('-')[0]?.toUpperCase() ?? p.itemTitle.split('::')[0]?.trim().toUpperCase() ?? '';
  return isLevel(prefix) ? prefix : 'UNCLASSIFIED';
}

export const KIND_LABEL: Record<ProposalKind, string> = { storyPoints: 'Story points', featureEffort: 'Feature effort', parentLink: 'Parent links', refinement: 'Drafted sections', metadata: 'Metadata repair', skills: 'Skills', sprintPlan: 'Sprint plan', themeLink: 'Theme membership' };
const KIND_LEDE: Record<ProposalKind, string> = {
  storyPoints: 'Sizes suggested by the model for stories; values above 8 are clamped to policy.',
  featureEffort: 'Effort bands suggested by the model for features.',
  parentLink: 'Parents inferred from title words, theme, area, labels and body references.',
  refinement: 'Missing playbook sections drafted by the model; existing text is kept. Read the diff before approving.',
  metadata: 'halyard-meta blocks built or completed from labels, titles and TBD placeholders. No model involved.',
  skills: 'Skills (shared taxonomy ids) each item needs, so work can be matched to people. From the parent and the text, or from the model.',
  sprintPlan: 'Sprint placement and owner for ready stories, within the committed share of team capacity, matched on skills. Approve to set sprint and owner.',
  themeLink: 'Which theme each epic belongs to, from the vision draft.',
};

export function ProposalsPage({ params, status }: { params: URLSearchParams; status: Status | undefined }) {
  const batches = useResource('/api/proposals', z.array(BatchSummary), { pollMs: 8000 });
  const [selectedId, setSelectedId] = useState<string | null>(params.get('batch'));
  const toast = useToast();
  const [confirmClear, setConfirmClear] = useState(false);
  const [clearing, setClearing] = useState(false);

  const fromUrl = params.get('batch');
  useEffect(() => {
    if (fromUrl) setSelectedId(fromUrl);
  }, [fromUrl]);
  useEffect(() => {
    // Keep the selection on an existing batch: newest when nothing is selected or the selected one is gone.
    if (!batches.data || clearing) return;
    if (selectedId && batches.data.some((b) => b.id === selectedId)) return;
    const next = batches.data[0]?.id ?? null;
    if (next !== selectedId) setSelectedId(next);
    if (!next || (fromUrl && fromUrl !== next)) navigate('proposals');
  }, [batches.data, selectedId, fromUrl, clearing]);

  const afterDelete = () => { setSelectedId(null); navigate('proposals'); invalidate('/api/proposals'); };
  const finished = (batches.data ?? []).filter((b) => b.pending === 0 && b.approved === 0);
  const clearFinished = async () => {
    setClearing(true);
    if (selectedId && finished.some((b) => b.id === selectedId)) { setSelectedId(null); navigate('proposals'); }
    try {
      for (const b of finished) await post(`/api/proposals/${encodeURIComponent(b.id)}/delete`);
      toast.push('ok', `Cleared ${finished.length} finished ${finished.length === 1 ? 'batch' : 'batches'}.`);
      afterDelete();
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setClearing(false);
      setConfirmClear(false);
    }
  };

  return (
    <Page
      title="Proposals"
      lede="Suggested changes waiting for a human decision. Nothing is written until you approve and apply, and applying only ever targets the sandbox."
    >
      <div className="grid gap-5 xl:grid-cols-[minmax(300px,1fr)_minmax(0,2.6fr)]">
        <div className="flex flex-col gap-5">
          <NewAnalysis status={status} onBatch={(id) => { invalidate('/api/proposals'); setSelectedId(id); }} />
          <Section title="Batches" aside={<span className="flex items-center gap-1"><ApplyAll batches={batches.data} status={status} onDone={() => { invalidate('/api/proposals'); invalidate('/api/snapshot'); invalidate('/api/review'); invalidate('/api/hierarchy'); }} /><Button size="sm" variant="quiet" onClick={() => setConfirmClear(true)} busy={clearing} disabled={finished.length === 0} title={finished.length === 0 ? 'Nothing to clear: every batch still has pending or approved proposals. Delete a batch with its trash icon.' : 'Delete every batch with nothing pending or approved'}>Clear finished ({finished.length})</Button></span>}>
            <ConfirmDialog open={confirmClear} title={`Clear ${finished.length} finished ${finished.length === 1 ? 'batch' : 'batches'}?`} confirmLabel="Clear" danger onCancel={() => setConfirmClear(false)} onConfirm={() => void clearFinished()}>
              Finished means nothing left pending or approved: everything was applied, rejected or failed. Applied changes stay in the sandbox; only the batch files on this machine are removed.
            </ConfirmDialog>
            {!batches.data ? <Skeleton rows={4} /> : batches.data.length === 0 ? <Empty title="No proposals yet">Run an analysis above. Each run becomes a batch you can review, approve and apply.</Empty> : (
              <ul className="scroll-y max-h-[50vh]">
                {batches.data.map((b) => (
                  <li key={b.id} className={cn('group relative', b.id === selectedId && 'bg-surface-2')}>
                    <button type="button" onClick={() => setSelectedId(b.id)} className="flex w-full flex-col gap-0.5 px-4 py-2.5 pr-10 text-left hover:bg-surface-2">
                      <span className="flex items-center justify-between gap-2"><span className="font-medium">{KIND_LABEL[b.kind]}</span><span className="text-[12px] text-ink-3">{relativeTime(b.createdAt)}</span></span>
                      <span className="flex flex-wrap gap-1 text-[12px] text-ink-2">
                        <span>{b.total} proposals</span>
                        <Pill tone={b.pending ? 'info' : 'neutral'}>{b.pending} pending</Pill>
                        <Pill tone={b.approved ? 'ok' : 'neutral'}>{b.approved} approved</Pill>
                        <Pill tone="neutral">{b.rejected} rejected</Pill>
                        <Pill tone="neutral">{b.applied} applied</Pill>
                        {b.failed ? <Pill tone="err">{b.failed} failed</Pill> : null}
                      </span>
                      <span className="mono text-[11px] text-ink-3">{b.source}</span>
                    </button>
                    <BatchRowDelete id={b.id} label={KIND_LABEL[b.kind]} onDeleted={afterDelete} />
                  </li>
                ))}
              </ul>
            )}
          </Section>
        </div>
        {selectedId ? <BatchView key={selectedId} batchId={selectedId} status={status} onChanged={() => invalidate('/api/proposals')} onDeleted={afterDelete} /> : <Section><Empty title="Select a batch">{batches.data && batches.data.length === 0 ? 'Run an analysis to create one.' : 'Pick a batch on the left.'}</Empty></Section>}
      </div>
      {batches.error ? <Notice tone="err">{batches.error}</Notice> : null}
    </Page>
  );
}

function ApplyAll({ batches, status, onDone }: { batches: BatchSummary[] | undefined; status: Status | undefined; onDone: () => void }) {
  const toast = useToast();
  const sandboxKey = status ? `${status.config.provider}:${status.config.sandboxRepository}`.toLowerCase() : '';
  const approved = (batches ?? []).filter((b) => b.target.toLowerCase() === sandboxKey).reduce((n, b) => n + b.approved, 0);
  const job = useJob((j) => {
    if (j.state === 'succeeded') {
      const r = j.result as { applied?: number; failed?: number; skipped?: number; batches?: number } | null;
      toast.push(r?.failed ? 'err' : 'ok', `Applied ${r?.applied ?? 0}, skipped ${r?.skipped ?? 0}, failed ${r?.failed ?? 0} across ${r?.batches ?? 0} batches.`);
    } else if (j.state === 'failed') toast.push('err', j.error ?? 'Apply failed');
    onDone();
  });
  if (approved === 0 && !job.running) return null;
  return (
    <span className="flex items-center gap-2">
      {job.job && job.running ? <span className="text-[11.5px] text-ink-3">{job.job.message}</span> : null}
      <Button size="sm" variant="primary" busy={job.running} onClick={() => void job.start('/api/proposals/apply-all')} title="Apply every approved proposal in every batch, oldest first"><LuPlay size={13} />Apply all {approved} approved</Button>
    </span>
  );
}

function NewAnalysis({ status, onBatch }: { status: Status | undefined; onBatch: (id: string) => void }) {
  const [kind, setKind] = useState<'storyPoints' | 'featureEffort'>('storyPoints');
  const [onlyUnsized, setOnlyUnsized] = useState(true);
  const [requireDefinition, setRequireDefinition] = useState(true);
  const [maxItems, setMaxItems] = useState(40);
  const [refineMax, setRefineMax] = useState(10);
  const [profile, setProfile] = useState('auto');
  const toast = useToast();
  const onModelJob = (what: string) => (j: Job) => {
    if (j.state === 'succeeded') {
      const result = j.result as { id?: string; total?: number } | null;
      toast.push('ok', `${result?.total ?? 0} ${what} saved.`);
      if (result?.id) onBatch(result.id);
    } else if (j.state === 'failed') toast.push('err', j.error ?? 'Analysis failed');
  };
  const sizeJob = useJob(onModelJob('size proposals'));
  const refineJob = useJob(onModelJob('drafts'));
  const skillsJob = useJob(onModelJob('skill tags'));
  const [busy, setBusy] = useState<string | null>(null);
  const noSnapshot = status ? !status.snapshot : true;
  const hasModel = !!status?.config.hasLlmToken;

  const runDeterministic = async (path: string, what: string) => {
    setBusy(path);
    try {
      const batch = Batch.parse(await post(path, {}));
      toast.push('ok', `${batch.proposals.length} ${what}${batch.proposals.length === 0 && batch.skipReasons.length ? ` (${batch.skipReasons[0]})` : ''}.`);
      onBatch(batch.id);
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setBusy(null);
    }
  };

  return (
    <Section title="New analysis">
      {noSnapshot ? <Notice tone="warn" className="m-4">Synchronize a repository first; proposals are made from the snapshot.</Notice> : null}
      <div className="flex flex-col divide-y divide-line-2">
        <div className="px-4 py-3">
          <h3 className="mb-1 flex items-center gap-1.5"><LuWrench size={14} className="text-ink-3" />Without a model</h3>
          <p className="mb-2 text-[12.5px] text-ink-2">Deterministic repairs from what the items already say. Instant.</p>
          <div className="flex flex-wrap gap-2">
            <Button onClick={() => void runDeterministic('/api/intelligence/metadata', 'metadata repairs')} busy={busy === '/api/intelligence/metadata'} disabled={noSnapshot}>Repair metadata</Button>
            <Button onClick={() => void runDeterministic('/api/intelligence/parents', 'parent proposals from evidence')} busy={busy === '/api/intelligence/parents'} disabled={noSnapshot}><LuArrowRight size={14} />Propose parents</Button>
            <Button onClick={() => void runDeterministic('/api/intelligence/skills', 'skill tags from parents and names')} busy={busy === '/api/intelligence/skills'} disabled={noSnapshot}>Tag skills</Button>
            <Button onClick={() => navigate('plan')} disabled={noSnapshot}>Plan sprints…</Button>
          </div>
        </div>
        <div className="px-4 py-3">
          <h3 className="mb-1 flex items-center gap-1.5"><LuSparkles size={14} className="text-ink-3" />With the model</h3>
          {!hasModel ? <Notice tone="warn" className="mb-2">No model key is loaded. <a href="#/settings" className="underline">Add an API key in Settings</a>.</Notice> : null}
          <Field label="Model profile" className="mb-3"><Select value={profile} onChange={(e) => setProfile(e.target.value)}>
            <option value="auto">Auto ({status?.config.llmResolvedProfile ?? '…'} · {status?.config.llmModel})</option>
            {status?.config.llmProfiles.map((p) => <option key={p.profile} value={p.profile} disabled={!p.configured}>{p.profile} · {p.model || '(no model)'}{p.configured ? '' : ' (not configured)'}</option>)}
          </Select></Field>
          <div className="rounded-ctl border border-line-2 p-3">
            <div className="mb-1 font-medium">Draft missing sections</div>
            <p className="mb-2 text-[12.5px] text-ink-2">Acceptance criteria, user need, tasks, done-when, outcome, success picture, scope. Existing text is kept; every draft is re-checked against the definition of ready.</p>
            <div className="flex items-end gap-3">
              <Field label="At most" className="w-32"><Select value={String(refineMax)} onChange={(e) => setRefineMax(Number(e.target.value))}>{[5, 10, 20, 40].map((n) => <option key={n} value={n}>{n} items</option>)}</Select></Field>
              <Button variant="primary" busy={refineJob.running} disabled={noSnapshot || !hasModel} onClick={() => void refineJob.start('/api/intelligence/refine', { maxItems: refineMax, profile })}><LuSparkles size={14} />Draft</Button>
            </div>
            {refineJob.job ? <div className="mt-3"><Progress done={refineJob.job.progressDone} total={refineJob.job.progressTotal} label={refineJob.job.message ?? refineJob.job.state} />{refineJob.job.error ? <p className="mt-1 text-err">{refineJob.job.error}</p> : null}</div> : null}
            {refineJob.error ? <p className="mt-2 text-err">{refineJob.error}</p> : null}
          </div>
          <div className="mt-3 rounded-ctl border border-line-2 p-3">
            <div className="mb-1 font-medium">Tag skills</div>
            <p className="mb-2 text-[12.5px] text-ink-2">The model picks 1–3 skills per story or feature from the shared taxonomy (the ids Shellback uses); answers outside it are dropped.</p>
            <Button variant="primary" busy={skillsJob.running} disabled={noSnapshot || !hasModel} onClick={() => void skillsJob.start('/api/intelligence/skills', { useModel: true, maxItems: 60, profile })}><LuSparkles size={14} />Tag with the model</Button>
            {skillsJob.job ? <div className="mt-3"><Progress done={skillsJob.job.progressDone} total={skillsJob.job.progressTotal} label={skillsJob.job.message ?? skillsJob.job.state} />{skillsJob.job.error ? <p className="mt-1 text-err">{skillsJob.job.error}</p> : null}</div> : null}
            {skillsJob.error ? <p className="mt-2 text-err">{skillsJob.error}</p> : null}
          </div>
          <div className="mt-3 rounded-ctl border border-line-2 p-3">
            <div className="mb-1 font-medium">Propose sizes</div>
            <p className="mb-2 text-[12.5px] text-ink-2">Stories without acceptance criteria are left TBD rather than guessed. Values above 8 are clamped to policy.</p>
            <div className="grid grid-cols-2 gap-3">
              <Field label="What to size"><Select value={kind} onChange={(e) => setKind(e.target.value as typeof kind)}><option value="storyPoints">Story points</option><option value="featureEffort">Feature effort</option></Select></Field>
              <Field label="At most"><Select value={String(maxItems)} onChange={(e) => setMaxItems(Number(e.target.value))}>{[10, 20, 40, 80, 150].map((n) => <option key={n} value={n}>{n} items</option>)}</Select></Field>
              <div className="col-span-2 flex flex-col gap-1.5">
                <Checkbox label="Only unsized items (missing or TBD)" checked={onlyUnsized} onChange={(e) => setOnlyUnsized(e.target.checked)} />
                <Checkbox label="Skip items without a definition" checked={requireDefinition} onChange={(e) => setRequireDefinition(e.target.checked)} />
              </div>
            </div>
            <div className="mt-3 flex items-center gap-3">
              <Button variant="primary" busy={sizeJob.running} disabled={noSnapshot || !hasModel} onClick={() => void sizeJob.start('/api/intelligence/estimate', { kind, onlyUnsized, requireDefinition, maxItems, profile })}><LuSparkles size={14} />Propose sizes</Button>
            </div>
            {sizeJob.job ? <div className="mt-3"><Progress done={sizeJob.job.progressDone} total={sizeJob.job.progressTotal} label={sizeJob.job.message ?? sizeJob.job.state} />{sizeJob.job.error ? <p className="mt-1 text-err">{sizeJob.job.error}</p> : null}</div> : null}
            {sizeJob.error ? <p className="mt-2 text-err">{sizeJob.error}</p> : null}
          </div>
        </div>
      </div>
    </Section>
  );
}

function BatchRowDelete({ id, label, onDeleted }: { id: string; label: string; onDeleted: () => void }) {
  const toast = useToast();
  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState(false);
  const remove = async () => {
    setBusy(true);
    try {
      await post(`/api/proposals/${encodeURIComponent(id)}/delete`);
      toast.push('ok', 'Batch deleted. Anything already applied stays in the sandbox.');
      onDeleted();
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setBusy(false);
      setConfirm(false);
    }
  };
  return (
    <>
      <Button size="sm" variant="quiet" className="absolute right-2 top-2 text-ink-3 hover:text-err" onClick={() => setConfirm(true)} busy={busy} aria-label={`Delete ${label} batch`} title="Delete this batch"><LuTrash2 size={13} /></Button>
      <ConfirmDialog open={confirm} title={`Delete this ${label.toLowerCase()} batch?`} confirmLabel="Delete batch" danger onCancel={() => setConfirm(false)} onConfirm={() => void remove()}>
        Removes the batch file and its decisions from this machine. Proposals already applied stay in the sandbox; pending and approved ones are simply forgotten.
      </ConfirmDialog>
    </>
  );
}

function BatchView({ batchId, status, onChanged, onDeleted }: { batchId: string; status: Status | undefined; onChanged: () => void; onDeleted: () => void }) {
  const batch = useResource(`/api/proposals/${encodeURIComponent(batchId)}`, Batch);
  const toast = useToast();
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const remove = async () => {
    setDeleting(true);
    try {
      await post(`/api/proposals/${encodeURIComponent(batchId)}/delete`);
      toast.push('ok', 'Batch deleted. Anything already applied stays in the sandbox.');
      onDeleted();
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setDeleting(false);
      setConfirmDelete(false);
    }
  };
  const [filter, setFilter] = useState<'all' | Proposal['decision']>('all');
  const [confidence, setConfidence] = useState<'all' | 'high' | 'medium' | 'low'>('all');
  const [busyId, setBusyId] = useState<string | null>(null);
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const apply = useJob((j) => {
    if (j.state === 'succeeded') {
      const r = j.result as { applied?: number; failed?: number; skipped?: number } | null;
      toast.push(r?.failed ? 'err' : 'ok', `Applied ${r?.applied ?? 0}, skipped ${r?.skipped ?? 0}, failed ${r?.failed ?? 0}.`);
      invalidate('/api/snapshot'); invalidate('/api/review'); invalidate('/api/hierarchy');
    } else if (j.state === 'failed') toast.push('err', j.error ?? 'Apply failed');
    void batch.refetch();
    onChanged();
  });
  const b = batch.data;
  useEffect(() => setOpen({}), [batchId]);

  const decide = async (proposalId: string | null, decision: 'approved' | 'rejected' | 'pending', minimumConfidence?: string) => {
    setBusyId(proposalId ?? '__all');
    try {
      await post(`/api/proposals/${encodeURIComponent(batchId)}/decide`, proposalId ? { proposalId, decision } : { all: true, decision, minimumConfidence });
      await batch.refetch();
      onChanged();
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setBusyId(null);
    }
  };

  const rows = useMemo(() => (b ? b.proposals.filter((p) => (filter === 'all' || p.decision === filter) && (confidence === 'all' || p.confidence === confidence)) : []), [b, filter, confidence]);
  const approved = b?.proposals.filter((p) => p.decision === 'approved').length ?? 0;
  const pending = b?.proposals.filter((p) => p.decision === 'pending').length ?? 0;
  const sandboxKey = status ? `${status.config.provider}:${status.config.sandboxRepository}` : '';
  const targetIsSandbox = b ? b.target.toLowerCase() === sandboxKey.toLowerCase() : false;
  const bodyKind = b ? b.kind === 'refinement' || b.kind === 'metadata' : false;

  return (
    <Section
      title={b ? `${KIND_LABEL[b.kind]} · ${b.proposals.length} proposals` : 'Batch'}
      aside={b ? <><span className="mono text-[11.5px]">{b.id}</span><Button size="sm" variant="quiet" className="text-err" onClick={() => setConfirmDelete(true)} busy={deleting} aria-label="Delete batch" title="Delete this batch"><LuTrash2 size={13} /></Button></> : null}
    >
      <ConfirmDialog open={confirmDelete} title="Delete this batch?" confirmLabel="Delete batch" danger onCancel={() => setConfirmDelete(false)} onConfirm={() => void remove()}>
        Removes the batch file and its decisions from this machine. Proposals already applied stay in the sandbox; pending and approved ones are simply forgotten.
      </ConfirmDialog>
      {!b && batch.error ? <Empty title="This batch is gone">{batch.error}</Empty> : !b ? <Skeleton rows={8} /> : (
        <>
          <div className="flex flex-col gap-1 px-4 py-2.5 text-[12.5px] text-ink-2">
            <span>{KIND_LEDE[b.kind]}</span>
            <span>From <Mono>{b.target}</Mono> via {b.source}; {b.considered} considered, {b.skipped} skipped, {relativeTime(b.createdAt)}.</span>
            {b.warnings.length > 0 ? <span className="flex flex-wrap gap-1">{b.warnings.map((w, i) => <Pill key={i} tone="warn">{w}</Pill>)}</span> : null}
          </div>
          {!targetIsSandbox ? <Notice tone="warn" className="mx-4 mb-2">This batch was analysed from <Mono>{b.target}</Mono>, which is not the configured sandbox. Approving is fine; applying will be refused.</Notice> : null}
          <div className="hairline flex flex-wrap items-center gap-2 px-4 py-2.5">
            <Select value={filter} onChange={(e) => setFilter(e.target.value as typeof filter)} className="w-36" aria-label="Decision filter">
              <option value="all">All decisions</option><option value="pending">Pending</option><option value="approved">Approved</option><option value="rejected">Rejected</option><option value="applied">Applied</option><option value="failed">Failed</option>
            </Select>
            <Select value={confidence} onChange={(e) => setConfidence(e.target.value as typeof confidence)} className="w-36" aria-label="Confidence filter">
              <option value="all">Any confidence</option><option value="high">High</option><option value="medium">Medium</option><option value="low">Low</option>
            </Select>
            {pending > 0 ? (
              <>
                <Button size="sm" onClick={() => void decide(null, 'approved', 'high')} busy={busyId === '__all'}>Approve all high-confidence</Button>
                <Button size="sm" onClick={() => void decide(null, 'approved')} busy={busyId === '__all'}>Approve all pending</Button>
                <Button size="sm" variant="quiet" onClick={() => void decide(null, 'rejected')} busy={busyId === '__all'}>Reject all pending</Button>
              </>
            ) : null}
            <span className="ml-auto flex items-center gap-2">
              <Button variant="primary" size="sm" disabled={approved === 0 || !targetIsSandbox || apply.running} busy={apply.running} onClick={() => void apply.start(`/api/proposals/${encodeURIComponent(batchId)}/apply`)}><LuPlay size={13} />Apply {approved} approved to sandbox</Button>
            </span>
          </div>
          {apply.job ? <div className="px-4 pb-2"><Progress done={apply.job.progressDone} total={apply.job.progressTotal} label={apply.job.message ?? apply.job.state} /></div> : null}
          {apply.error ? <Notice tone="err" className="mx-4 mb-2">{apply.error}</Notice> : null}
          {rows.length === 0 ? <Empty title="No proposals in this view" /> : (
            <div className="scroll-y max-h-[calc(100vh-380px)]">
              <table className="table-dense w-full">
                <thead><tr><th className="w-6" /><th className="w-12">Level</th><th className="min-w-[200px]">Work item</th><th className={bodyKind ? 'w-56' : 'w-40'}>{bodyKind ? 'Closes' : 'Change'}</th><th className="w-20">Conf.</th><th>Reason</th><th className="w-28">Decision</th></tr></thead>
                <tbody>
                  {rows.map((p) => {
                    const isOpen = !!open[p.id];
                    return (
                      <ProposalRow key={p.id} p={p} batchId={batchId} open={isOpen} onToggle={() => setOpen((o) => ({ ...o, [p.id]: !o[p.id] }))} busy={busyId === p.id} onDecide={(d) => void decide(p.id, d)} />
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
          {b.skipReasons.length > 0 ? (
            <details className="hairline px-4 py-2 text-[12.5px] text-ink-2">
              <summary className="cursor-pointer">Skipped {b.skipReasons.length} items</summary>
              <ul className="mt-1 flex flex-col gap-0.5 pl-4">{b.skipReasons.map((s, i) => <li key={i}>{s}</li>)}</ul>
            </details>
          ) : null}
        </>
      )}
      {batch.error ? <Notice tone="err" className="m-4">{batch.error}</Notice> : null}
    </Section>
  );
}

function ProposalRow({ p, batchId, open, onToggle, busy, onDecide }: { p: Proposal; batchId: string; open: boolean; onToggle: () => void; busy: boolean; onDecide: (d: 'approved' | 'rejected' | 'pending') => void }) {
  const bodyKind = p.kind === 'refinement' || p.kind === 'metadata';
  const title = p.itemTitle.replace(/^\s*(THEME|EPIC|FEATURE|STORY|TASK|BUG)\s*::\s*/i, '');
  return (
    <>
      <tr className={cn('cursor-pointer', open && 'bg-surface-2')} onClick={onToggle}>
        <td className="text-ink-3">{open ? <LuChevronDown size={14} /> : <LuChevronRight size={14} />}</td>
        <td><LevelChip type={levelOf(p)} compact /></td>
        <td>
          <div className="font-medium">{title}</div>
          <div className="mono text-[11.5px] text-ink-3">#{p.itemNumber}{p.itemCanonicalId.includes(':') ? '' : ` · ${p.itemCanonicalId}`}</div>
        </td>
        <td className={bodyKind ? '' : 'num break-all'}>
          {bodyKind ? (
            <div className="flex flex-wrap gap-1">
              {p.addresses.map((a) => <Pill key={a} tone={p.remaining.includes(a) ? 'warn' : 'neutral'}>{humanizeCode(a.replace(/^missing_required_meta:/, 'meta '))}</Pill>)}
            </div>
          ) : (
            <><span className="text-ink-3">{p.currentValue || 'none'}</span> <span className="text-ink-3">→</span> <span className="font-semibold">{p.proposedValue}</span></>
          )}
        </td>
        <td><Pill tone={p.confidence === 'high' ? 'ok' : p.confidence === 'low' ? 'warn' : 'neutral'}>{p.confidence}</Pill></td>
        <td className="text-ink-2">{p.reason}{p.error ? <div className="text-err">{p.error}</div> : null}</td>
        <td onClick={(e) => e.stopPropagation()}>
          <Decision p={p} busy={busy} onDecide={onDecide} />
        </td>
      </tr>
      {open ? (
        <tr className="bg-surface-2/60">
          <td colSpan={7} className="px-4 pb-4 pt-1">
            <ProposalDetail p={p} batchId={batchId} />
          </td>
        </tr>
      ) : null}
    </>
  );
}

function Decision({ p, busy, onDecide }: { p: Proposal; busy: boolean; onDecide: (d: 'approved' | 'rejected' | 'pending') => void }) {
  if (p.decision === 'applied') return <Pill tone="ok"><LuCheck size={11} />applied</Pill>;
  if (p.decision === 'failed') return <Pill tone="err">failed</Pill>;
  return (
    <div className="flex items-center gap-1">
      <Button size="sm" variant={p.decision === 'approved' ? 'primary' : 'default'} busy={busy} onClick={() => onDecide(p.decision === 'approved' ? 'pending' : 'approved')} aria-label="Approve" title="Approve"><LuCheck size={13} /></Button>
      <Button size="sm" variant={p.decision === 'rejected' ? 'danger' : 'default'} busy={busy} onClick={() => onDecide(p.decision === 'rejected' ? 'pending' : 'rejected')} aria-label="Reject" title="Reject"><LuX size={13} /></Button>
    </div>
  );
}

function ProposalDetail({ p, batchId }: { p: Proposal; batchId: string }) {
  const preview = useResource(`/api/proposals/${encodeURIComponent(batchId)}/${encodeURIComponent(p.id)}/preview`, ProposalPreview);
  const d = preview.data;
  return (
    <div className="grid gap-3 lg:grid-cols-[minmax(0,1fr)_280px]">
      <div className="min-w-0">
        {preview.error ? <Notice tone="warn">{preview.error}</Notice> : !d ? <Skeleton rows={4} /> : (
          <>
            {d.snapshotStale ? <Notice tone="info" className="mb-2">The snapshot is over 30 minutes old; applying re-reads the live item first.</Notice> : null}
            <DiffView before={d.current.body} after={d.proposed.body} />
          </>
        )}
      </div>
      <div className="flex flex-col gap-2 text-[12.5px]">
        <Facts label="Source"><Mono>{p.source}</Mono></Facts>
        {p.proposedMeta ? (
          <Facts label="Meta changes">
            <table className="w-full"><tbody>{Object.entries(p.proposedMeta).map(([k, v]) => <tr key={k}><td className="mono pr-2 text-ink-3">{k}</td><td className={v === null ? 'text-err line-through' : ''}>{v === null ? 'removed' : v || <span className="text-ink-3">(empty)</span>}</td></tr>)}</tbody></table>
          </Facts>
        ) : null}
        {d ? (
          <Facts label="Definition of ready after applying">
            {d.proposed.deficiencies.length === 0 ? <span className="text-ok">Complete for its level.</span> : <ul className="flex flex-col gap-0.5">{d.proposed.deficiencies.map((c) => <li key={c} className={p.addresses.includes(c) ? 'text-warn' : 'text-ink-2'}>{humanizeCode(c)}</li>)}</ul>}
            {d.current.deficiencies.length > d.proposed.deficiencies.length ? <div className="mt-1 text-ink-3">{d.current.deficiencies.length - d.proposed.deficiencies.length} gap{d.current.deficiencies.length - d.proposed.deficiencies.length === 1 ? '' : 's'} closed.</div> : null}
          </Facts>
        ) : null}
        <Facts label="Open the item"><button type="button" className="underline hover:text-accent" onClick={() => navigate('hierarchy', { item: p.itemKey })}>#{p.itemNumber} in the hierarchy</button></Facts>
        {p.decisionNote ? <Facts label="Note">{p.decisionNote}</Facts> : null}
      </div>
    </div>
  );
}

function Facts({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="rounded-ctl border border-line-2 bg-surface px-3 py-2">
      <div className="mb-0.5 text-[11.5px] text-ink-3">{label}</div>
      <div>{children}</div>
    </div>
  );
}
