import { useEffect, useState } from 'react';
import { LuDownload, LuSparkles, LuWrench } from 'react-icons/lu';
import { navigate } from '../app';
import { Button, Empty, Mono, Notice, Page, Pill, Progress, Section, cn, useToast } from '../components/ui';
import { downloadText, relativeTime } from '../lib/format';
import { useJob } from '../lib/jobs';
import { VisionDraft, VisionSummary, type Status } from '../lib/schemas';
import { invalidate, useResource } from '../lib/useResource';
import { Markdownish } from './hierarchy';
import { Skeleton } from './overview';
import { z } from 'zod';

/**
 * The upward half of the product owner: a portfolio vision drafted from the backlog itself (themes, epics,
 * progress, blockers, skill gaps), kept as a document with history, plus theme-membership proposals.
 */
export function VisionPage({ status }: { status: Status | undefined }) {
  const drafts = useResource('/api/vision', z.array(VisionSummary));
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const toast = useToast();
  const job = useJob((j) => {
    if (j.state === 'succeeded') {
      const r = j.result as { id?: string; proposalBatchId?: string | null } | null;
      toast.push('ok', r?.proposalBatchId ? 'Vision drafted, with theme proposals to review.' : 'Vision drafted.');
      invalidate('/api/vision'); invalidate('/api/proposals');
      if (r?.id) setSelectedId(r.id);
    } else if (j.state === 'failed') toast.push('err', j.error ?? 'Drafting failed');
  });
  useEffect(() => { if (!selectedId && drafts.data && drafts.data.length > 0) setSelectedId(drafts.data[0]!.id); }, [drafts.data, selectedId]);
  const noSnapshot = status ? !status.snapshot : true;
  const hasModel = !!status?.config.hasLlmToken;

  return (
    <Page
      title="Vision"
      lede="A portfolio vision drafted from the backlog: strategic themes, priorities, risks and the next quarter, with the facts it rests on. Each draft is kept; theme changes it implies become proposals."
      actions={<>
        <Button onClick={() => void job.start('/api/vision/draft', { useModel: false })} busy={job.running} disabled={noSnapshot}><LuWrench size={14} />Summarize (no model)</Button>
        <Button variant="primary" onClick={() => void job.start('/api/vision/draft', { useModel: true })} busy={job.running} disabled={noSnapshot || !hasModel} title={hasModel ? undefined : 'Add a model key in Settings'}><LuSparkles size={14} />Draft with the model</Button>
      </>}
    >
      {noSnapshot ? <Notice tone="warn">Synchronize a repository first; the vision is drafted from the snapshot.</Notice> : null}
      {job.job && job.running ? <Progress done={job.job.progressDone} total={job.job.progressTotal} label={job.job.message ?? job.job.state} /> : null}
      {job.error ? <Notice tone="err">{job.error}</Notice> : null}
      <div className="grid gap-5 xl:grid-cols-[280px_minmax(0,1fr)]">
        <Section title="Drafts">
          {!drafts.data ? <Skeleton rows={3} /> : drafts.data.length === 0 ? <Empty title="No drafts yet">Summarize the backlog without a model, or draft with the model for judgement on priorities and risks.</Empty> : (
            <ul>
              {drafts.data.map((d) => (
                <li key={d.id}>
                  <button type="button" onClick={() => setSelectedId(d.id)} className={cn('flex w-full flex-col gap-0.5 px-4 py-2.5 text-left hover:bg-surface-2', d.id === selectedId && 'bg-surface-2')}>
                    <span className="flex items-center justify-between gap-2"><span className="font-medium">{relativeTime(d.createdAt)}</span>{d.warnings ? <Pill tone="warn">{d.warnings}</Pill> : null}</span>
                    <span className="line-clamp-2 text-[12px] text-ink-2">{d.vision}</span>
                    <span className="mono text-[11px] text-ink-3">{d.source}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </Section>
        {selectedId ? <DraftView id={selectedId} /> : <Section><Empty title="Select a draft" /></Section>}
      </div>
    </Page>
  );
}

function DraftView({ id }: { id: string }) {
  const draft = useResource(`/api/vision/${encodeURIComponent(id)}`, VisionDraft);
  const d = draft.data;
  return (
    <Section
      title={d ? `Portfolio vision · ${new Date(d.createdAt).toLocaleDateString()}` : 'Draft'}
      aside={d ? <>
        {d.proposalBatchId ? <Button size="sm" onClick={() => navigate('proposals', { batch: d.proposalBatchId! })}>Theme proposals</Button> : null}
        <Button size="sm" variant="quiet" onClick={() => downloadText(`halyard-vision-${d.id}.md`, d.markdown, 'text/markdown')}><LuDownload size={13} />Markdown</Button>
      </> : null}
    >
      {draft.error ? <Notice tone="err" className="m-4">{draft.error}</Notice> : !d ? <Skeleton rows={10} /> : (
        <div className="grid gap-0 lg:grid-cols-[minmax(0,1fr)_300px]">
          <div className="px-5 py-4"><Markdownish text={d.markdown} /></div>
          <div className="flex flex-col gap-3 border-l border-line-2 px-4 py-4 text-[12.5px]">
            <div><div className="mb-1 text-ink-3">Source</div><Mono>{d.source}</Mono> · <Mono>{d.repository}</Mono></div>
            <div><div className="mb-1 text-ink-3">Themes</div><ul className="flex flex-col gap-1">{d.themes.map((t) => <li key={t.id + t.title}><span className="font-medium">{t.title}</span>{t.isNew ? <Pill tone="info" className="ml-1">proposed</Pill> : null}<div className="text-ink-3">{t.epics.length} epics</div></li>)}</ul></div>
            <div><div className="mb-1 text-ink-3">Priorities</div><ol className="flex flex-col gap-1 pl-4">{d.priorities.map((p) => <li key={p.epic}><span className="font-medium">{p.title}</span><div className="text-ink-2">{p.why}</div></li>)}</ol></div>
            {d.warnings.length > 0 ? <div className="flex flex-col gap-1">{d.warnings.map((w, i) => <Notice key={i} tone="warn">{w}</Notice>)}</div> : null}
          </div>
        </div>
      )}
    </Section>
  );
}
