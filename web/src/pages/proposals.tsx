import { useEffect, useMemo, useState } from 'react';
import { LuArrowRight, LuCheck, LuPlay, LuSparkles, LuX } from 'react-icons/lu';
import { navigate } from '../app';
import { Button, Checkbox, Empty, Field, LevelChip, Mono, Notice, Page, Pill, Progress, Section, Select, useToast } from '../components/ui';
import { describeError, post } from '../lib/api';
import { humanizeCode, relativeTime } from '../lib/format';
import { isLevel } from '../lib/levels';
import { useJob } from '../lib/jobs';
import { Batch, BatchSummary, type Proposal, type Status } from '../lib/schemas';
import { invalidate, useResource } from '../lib/useResource';
import { z } from 'zod';
import { Skeleton } from './overview';

function levelOf(p: Proposal): string {
  if (p.kind === 'featureEffort') return 'FEATURE';
  if (p.kind === 'storyPoints') return 'STORY';
  const prefix = p.itemCanonicalId.split('-')[0]?.toUpperCase() ?? '';
  return isLevel(prefix) ? prefix : 'UNCLASSIFIED';
}

const KIND_LABEL: Record<Proposal['kind'], string> = { storyPoints: 'Story points', featureEffort: 'Feature effort', parentLink: 'Parent links' };

export function ProposalsPage({ params, status }: { params: URLSearchParams; status: Status | undefined }) {
  const batches = useResource('/api/proposals', z.array(BatchSummary), { pollMs: 8000 });
  const [selectedId, setSelectedId] = useState<string | null>(params.get('batch'));

  useEffect(() => {
    if (!selectedId && batches.data && batches.data.length > 0) setSelectedId(batches.data[0]!.id);
  }, [batches.data, selectedId]);

  return (
    <Page
      title="Proposals"
      lede="Suggested sizes and parent links, waiting for a human decision. Nothing is written until you approve and apply, and applying only ever targets the sandbox."
    >
      <div className="grid gap-5 xl:grid-cols-[minmax(300px,1fr)_minmax(0,2.4fr)]">
        <div className="flex flex-col gap-5">
          <NewAnalysis status={status} onBatch={(id) => { invalidate('/api/proposals'); setSelectedId(id); }} />
          <Section title="Batches">
            {!batches.data ? <Skeleton rows={4} /> : batches.data.length === 0 ? <Empty title="No proposals yet">Run an analysis above. Each run becomes a batch you can review, approve and apply.</Empty> : (
              <ul>
                {batches.data.map((b) => (
                  <li key={b.id}>
                    <button type="button" onClick={() => setSelectedId(b.id)} className={`flex w-full flex-col gap-0.5 px-4 py-2.5 text-left hover:bg-surface-2 ${b.id === selectedId ? 'bg-surface-2' : ''}`}>
                      <span className="flex items-center justify-between gap-2"><span className="font-medium">{KIND_LABEL[b.kind]}</span><span className="text-[12px] text-ink-3">{relativeTime(b.createdAt)}</span></span>
                      <span className="text-[12px] text-ink-2">{b.total} proposals · {b.pending} pending · {b.approved} approved · {b.applied} applied{b.failed ? ` · ${b.failed} failed` : ''}</span>
                      <span className="mono text-[11px] text-ink-3">{b.target} · {b.source}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </Section>
        </div>
        {selectedId ? <BatchView batchId={selectedId} status={status} onChanged={() => invalidate('/api/proposals')} /> : <Section><Empty title="Select a batch" /></Section>}
      </div>
      {batches.error ? <Notice tone="err">{batches.error}</Notice> : null}
    </Page>
  );
}

function NewAnalysis({ status, onBatch }: { status: Status | undefined; onBatch: (id: string) => void }) {
  const [kind, setKind] = useState<'storyPoints' | 'featureEffort'>('storyPoints');
  const [onlyUnsized, setOnlyUnsized] = useState(true);
  const [requireDefinition, setRequireDefinition] = useState(true);
  const [maxItems, setMaxItems] = useState(40);
  const [profile, setProfile] = useState('auto');
  const toast = useToast();
  const job = useJob((j) => {
    if (j.state === 'succeeded') {
      const result = j.result as { id?: string } | null;
      toast.push('ok', 'Proposals saved.');
      if (result?.id) onBatch(result.id);
    } else if (j.state === 'failed') toast.push('err', j.error ?? 'Analysis failed');
  });
  const [parentsBusy, setParentsBusy] = useState(false);
  const noSnapshot = status ? !status.snapshot : true;

  const runParents = async () => {
    setParentsBusy(true);
    try {
      const batch = Batch.parse(await post('/api/intelligence/parents', {}));
      toast.push('ok', `${batch.proposals.length} parent proposals from evidence.`);
      onBatch(batch.id);
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setParentsBusy(false);
    }
  };

  return (
    <Section title="New analysis">
      {noSnapshot ? <Notice tone="warn" className="m-4">Synchronize a repository first; proposals are made from the snapshot.</Notice> : null}
      <div className="flex flex-col gap-3 px-4 py-3">
        <div>
          <h3 className="mb-1">Parent links from evidence</h3>
          <p className="mb-2 text-[12.5px] text-ink-2">Deterministic: shared title words, theme, area, labels and body references. No model involved.</p>
          <Button onClick={runParents} busy={parentsBusy} disabled={noSnapshot}><LuArrowRight size={14} />Propose parents</Button>
        </div>
        <div className="hairline pt-3">
          <h3 className="mb-1">Sizes from a model</h3>
          <p className="mb-2 text-[12.5px] text-ink-2">Stories without acceptance criteria are left TBD rather than guessed. Values above 8 are clamped to policy.</p>
          <div className="grid grid-cols-2 gap-3">
            <Field label="What to size"><Select value={kind} onChange={(e) => setKind(e.target.value as typeof kind)}><option value="storyPoints">Story points</option><option value="featureEffort">Feature effort</option></Select></Field>
            <Field label="Model profile"><Select value={profile} onChange={(e) => setProfile(e.target.value)}>
              <option value="auto">Auto ({status?.config.llmResolvedProfile ?? '…'})</option>
              {status?.config.llmProfiles.map((p) => <option key={p.profile} value={p.profile} disabled={!p.configured}>{p.profile} · {p.model}{p.configured ? '' : ' (no token)'}</option>)}
            </Select></Field>
            <Field label="At most"><Select value={String(maxItems)} onChange={(e) => setMaxItems(Number(e.target.value))}>{[10, 20, 40, 80, 150].map((n) => <option key={n} value={n}>{n} items</option>)}</Select></Field>
            <div className="flex flex-col justify-end gap-1.5">
              <Checkbox label="Only unsized items" checked={onlyUnsized} onChange={(e) => setOnlyUnsized(e.target.checked)} />
              <Checkbox label="Skip undefined items" checked={requireDefinition} onChange={(e) => setRequireDefinition(e.target.checked)} />
            </div>
          </div>
          <div className="mt-3 flex items-center gap-3">
            <Button variant="primary" busy={job.running} disabled={noSnapshot || !status?.config.hasLlmToken} onClick={() => void job.start('/api/intelligence/estimate', { kind, onlyUnsized, requireDefinition, maxItems, profile })}><LuSparkles size={14} />Propose sizes</Button>
            {!status?.config.hasLlmToken ? <span className="text-[12px] text-ink-3">No model token is loaded. See Settings.</span> : null}
          </div>
          {job.job ? <div className="mt-3"><Progress done={job.job.progressDone} total={job.job.progressTotal} label={job.job.message ?? job.job.state} />{job.job.error ? <p className="mt-1 text-err">{job.job.error}</p> : null}</div> : null}
          {job.error ? <p className="mt-2 text-err">{job.error}</p> : null}
        </div>
      </div>
    </Section>
  );
}

function BatchView({ batchId, status, onChanged }: { batchId: string; status: Status | undefined; onChanged: () => void }) {
  const batch = useResource(`/api/proposals/${encodeURIComponent(batchId)}`, Batch);
  const toast = useToast();
  const [filter, setFilter] = useState<'all' | Proposal['decision']>('all');
  const [busyId, setBusyId] = useState<string | null>(null);
  const apply = useJob((j) => {
    if (j.state === 'succeeded') {
      const r = j.result as { applied?: number; failed?: number; skipped?: number } | null;
      toast.push(r?.failed ? 'err' : 'ok', `Applied ${r?.applied ?? 0}, skipped ${r?.skipped ?? 0}, failed ${r?.failed ?? 0}.`);
    } else if (j.state === 'failed') toast.push('err', j.error ?? 'Apply failed');
    void batch.refetch();
    onChanged();
  });
  const b = batch.data;

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

  const rows = useMemo(() => (b ? b.proposals.filter((p) => filter === 'all' || p.decision === filter) : []), [b, filter]);
  const approved = b?.proposals.filter((p) => p.decision === 'approved').length ?? 0;
  const sandboxKey = status ? `${status.config.provider}:${status.config.sandboxRepository}` : '';
  const targetIsSandbox = b ? b.target.toLowerCase() === sandboxKey.toLowerCase() : false;

  return (
    <Section
      title={b ? `${KIND_LABEL[b.kind]} · ${b.proposals.length} proposals` : 'Batch'}
      aside={b ? <span className="mono text-[11.5px]">{b.id}</span> : null}
    >
      {!b ? <Skeleton rows={8} /> : (
        <>
          <div className="flex flex-wrap items-center gap-2 px-4 py-2.5 text-[12.5px] text-ink-2">
            <span>From <Mono>{b.target}</Mono> via {b.source}; {b.considered} considered, {b.skipped} skipped.</span>
            {b.warnings.map((w, i) => <Pill key={i} tone="warn">{w}</Pill>)}
          </div>
          {!targetIsSandbox ? <Notice tone="warn" className="mx-4 mb-2">This batch was analysed from <Mono>{b.target}</Mono>, which is not the configured sandbox. Approving is fine; applying will be refused.</Notice> : null}
          <div className="hairline flex flex-wrap items-center gap-2 px-4 py-2.5">
            <Select value={filter} onChange={(e) => setFilter(e.target.value as typeof filter)} className="w-40" aria-label="Decision filter">
              <option value="all">All decisions</option><option value="pending">Pending</option><option value="approved">Approved</option><option value="rejected">Rejected</option><option value="applied">Applied</option><option value="failed">Failed</option>
            </Select>
            <Button size="sm" onClick={() => void decide(null, 'approved', 'high')} busy={busyId === '__all'}>Approve all high-confidence</Button>
            <Button size="sm" onClick={() => void decide(null, 'approved')} busy={busyId === '__all'}>Approve all pending</Button>
            <Button size="sm" variant="quiet" onClick={() => void decide(null, 'rejected')} busy={busyId === '__all'}>Reject all pending</Button>
            <span className="ml-auto flex items-center gap-2">
              <Button variant="primary" size="sm" disabled={approved === 0 || !targetIsSandbox || apply.running} busy={apply.running} onClick={() => void apply.start(`/api/proposals/${encodeURIComponent(batchId)}/apply`)}><LuPlay size={13} />Apply {approved} approved to sandbox</Button>
            </span>
          </div>
          {apply.job ? <div className="px-4 pb-2"><Progress done={apply.job.progressDone} total={apply.job.progressTotal} label={apply.job.message ?? apply.job.state} /></div> : null}
          {apply.error ? <Notice tone="err" className="mx-4 mb-2">{apply.error}</Notice> : null}
          {rows.length === 0 ? <Empty title="No proposals in this view" /> : (
            <div className="scroll-y max-h-[calc(100vh-380px)]">
              <table className="table-dense w-full">
                <thead><tr><th className="w-14">Level</th><th className="min-w-[220px]">Work item</th><th className="w-44">Change</th><th className="w-24">Confidence</th><th>Reason</th><th className="w-44">Decision</th></tr></thead>
                <tbody>
                  {rows.map((p) => (
                    <tr key={p.id}>
                      <td><LevelChip type={levelOf(p)} compact /></td>
                      <td><button type="button" className="text-left hover:text-accent" onClick={() => navigate('hierarchy', { item: String(p.itemNumber) })}><div className="font-medium">{p.itemTitle.replace(/^\s*(THEME|EPIC|FEATURE|STORY|TASK|BUG)\s*::\s*/i, '')}</div><div className="mono text-[11.5px] text-ink-3">#{p.itemNumber} · {p.itemCanonicalId}</div></button></td>
                      <td className="num break-all"><span className="text-ink-3">{p.currentValue || 'none'}</span> <span className="text-ink-3">→</span> <span className="font-semibold">{p.proposedValue}</span></td>
                      <td><Pill tone={p.confidence === 'high' ? 'ok' : p.confidence === 'low' ? 'warn' : 'neutral'}>{p.confidence}</Pill></td>
                      <td className="text-ink-2">{p.reason}{p.error ? <div className="text-err">{p.error}</div> : null}</td>
                      <td>
                        {p.decision === 'applied' ? <Pill tone="ok"><LuCheck size={11} />applied</Pill>
                          : p.decision === 'failed' ? <Pill tone="err">failed</Pill>
                          : (
                            <div className="flex items-center gap-1">
                              <Button size="sm" variant={p.decision === 'approved' ? 'primary' : 'default'} busy={busyId === p.id} onClick={() => void decide(p.id, p.decision === 'approved' ? 'pending' : 'approved')} aria-label="Approve"><LuCheck size={13} /></Button>
                              <Button size="sm" variant={p.decision === 'rejected' ? 'danger' : 'default'} busy={busyId === p.id} onClick={() => void decide(p.id, p.decision === 'rejected' ? 'pending' : 'rejected')} aria-label="Reject"><LuX size={13} /></Button>
                              <span className="text-[12px] text-ink-3">{p.decision === 'pending' ? '' : humanizeCode(p.decision)}</span>
                            </div>
                          )}
                      </td>
                    </tr>
                  ))}
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
