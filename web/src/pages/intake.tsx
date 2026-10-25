import { useMemo, useState } from 'react';
import { LuArrowRightToLine, LuExternalLink, LuSparkles } from 'react-icons/lu';
import { navigate } from '../app';
import { DiffView } from '../components/diff';
import { Button, Checkbox, Empty, Input, LevelChip, Mono, Notice, Page, Pill, Progress, Section, Select, cn, useToast } from '../components/ui';
import { describeError, post } from '../lib/api';
import { humanizeCode, relativeTime } from '../lib/format';
import { resyncedBy, useJob } from '../lib/jobs';
import { LEVELS, LEVEL_LABEL } from '../lib/levels';
import { IntakeManifest, IntakePreview, IntakeSources, type IntakeDraft, type LevelToken, type Status } from '../lib/schemas';
import { invalidate, useResource } from '../lib/useResource';
import { z } from 'zod';
import { Skeleton } from './overview';

/**
 * Intake: raw tickets from the source repositories become structured copies in the sandbox, each carrying
 * its source reference. From there the normal review (repair, draft, size, parents) takes over. The source
 * is never written.
 */
export function IntakePage({ status }: { status: Status | undefined }) {
  const toast = useToast();
  const sources = useResource(status?.snapshot ? '/api/intake/sources' : null, IntakeSources);
  const runs = useResource('/api/intake/runs', z.array(IntakeManifest), { pollMs: 15000 });
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [query, setQuery] = useState('');
  const [type, setType] = useState<string>('ALL');
  const [hideCopied, setHideCopied] = useState(true);
  const [preview, setPreview] = useState<z.infer<typeof IntakePreview> | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [useModel, setUseModel] = useState(false);
  const [busy, setBusy] = useState(false);
  const job = useJob((j) => {
    if (j.state === 'succeeded') {
      const r = j.result as { items?: unknown[]; errors?: string[]; drafts?: unknown } | null;
      if (r?.drafts) { const p = IntakePreview.safeParse(r); if (p.success) setPreview(p.data); return; }
      toast.push(r?.errors?.length ? 'err' : 'ok', `Copied ${r?.items?.length ?? 0} tickets into the sandbox${r?.errors?.length ? ` with ${r.errors.length} errors` : ''}${resyncedBy(j) ? '; snapshot refreshed, they are on Review now.' : '. Synchronize the sandbox to review them.'}`);
      invalidate('/api/intake'); setPreview(null); setSelected(new Set());
    } else if (j.state === 'failed') toast.push('err', j.error ?? 'Intake failed');
  });
  const s = sources.data;
  const hasModel = !!status?.config.hasLlmToken;

  const items = useMemo(() => {
    if (!s) return [];
    let list = s.items;
    if (hideCopied) list = list.filter((i) => !i.copiedAs);
    if (type !== 'ALL') list = list.filter((i) => i.guessedType === type);
    if (query) { const q = query.toLowerCase(); list = list.filter((i) => i.title.toLowerCase().includes(q) || String(i.number) === q || i.labels.some((l) => l.toLowerCase().includes(q))); }
    return list;
  }, [s, hideCopied, type, query]);
  const allSelected = items.length > 0 && items.every((i) => selected.has(i.key));
  const toggle = (k: string) => setSelected((cur) => { const n = new Set(cur); if (n.has(k)) n.delete(k); else n.add(k); return n; });

  const runPreview = async () => {
    setBusy(true);
    try {
      if (useModel) { await job.start('/api/intake/preview', { keys: [...selected], useModel: true }); }
      else setPreview(IntakePreview.parse(await post('/api/intake/preview', { keys: [...selected] })));
    } catch (e) { toast.push('err', describeError(e)); } finally { setBusy(false); }
  };

  if (status && !status.snapshot) {
    return <Page title="Intake" lede="Raw tickets from the source backlog, copied into the sandbox as structured work items."><Section><Empty title="No snapshot yet" action={<Button variant="primary" onClick={() => navigate('connection')}>Connect and synchronize</Button>}>Synchronize the source repositories and the sandbox together ("Sources and sandbox together" on Connection).</Empty></Section></Page>;
  }

  return (
    <Page
      title="Intake"
      lede={s ? <>{s.items.length} tickets from {s.repositories.length === 0 ? 'the source' : <Mono>{s.repositories.join(', ')}</Mono>}, {s.items.filter((i) => i.copiedAs).length} already copied into <Mono>{s.sandbox}</Mono>. Pick tickets, check the proposed level and id, then copy. The source repository is never written.</> : 'Raw tickets from the source backlog, copied into the sandbox as structured work items.'}
    >
      {sources.error ? <Notice tone="err">{sources.error}</Notice> : null}
      {s && s.items.length === 0 ? <Notice tone="info">No raw tickets. A raw ticket is any issue in a source repository, or an issue in the sandbox with no cascade-meta and no Cascade labels. To bring in the real backlog, set <Mono>CASCADE_SOURCE_REPO</Mono> to it and synchronize "Sources and sandbox together".</Notice> : null}
      {selected.size > 0 && !preview ? (
        <div className="panel flex flex-wrap items-center gap-2 px-4 py-2.5">
          <span className="font-medium">{selected.size} selected</span>
          <Checkbox label="Let the model classify level, theme and title" checked={useModel} onChange={(e) => setUseModel(e.target.checked)} disabled={!hasModel} />
          <span className="ml-auto flex items-center gap-2">
            <Button variant="primary" busy={busy || job.running} onClick={() => void runPreview()}>{useModel ? <LuSparkles size={14} /> : <LuArrowRightToLine size={14} />}Prepare {selected.size} for the sandbox</Button>
            <Button variant="quiet" onClick={() => setSelected(new Set())}>Clear</Button>
          </span>
          {job.job && job.running ? <div className="w-full"><Progress done={job.job.progressDone} total={job.job.progressTotal} label={job.job.message ?? job.job.state} /></div> : null}
        </div>
      ) : null}
      {preview ? <PreviewPanel preview={preview} onBack={() => setPreview(null)} onRun={() => { void job.start('/api/intake/run', { keys: preview.drafts.map((d) => d.sourceKey), useModel }); }} running={job.running} job={job.job} open={open} setOpen={setOpen} /> : null}
      <Section title="Source tickets">
        <div className="flex flex-wrap items-center gap-3 px-4 py-2.5">
          <Select value={type} onChange={(e) => setType(e.target.value)} className="w-44" aria-label="Guessed level"><option value="ALL">All guessed levels</option>{[...LEVELS, 'BUG'].map((l) => <option key={l} value={l}>{LEVEL_LABEL[l as LevelToken]}</option>)}</Select>
          <Input placeholder="Find by title, #number or label" value={query} onChange={(e) => setQuery(e.target.value)} className="w-64" aria-label="Find" />
          <Checkbox label="Hide tickets already copied" checked={hideCopied} onChange={(e) => setHideCopied(e.target.checked)} />
          <span className="ml-auto text-ink-3">{items.length} shown</span>
        </div>
        {!s ? <Skeleton rows={8} /> : items.length === 0 ? <Empty title="No source tickets in this view" /> : (
          <div className="scroll-y max-h-[60vh]">
            <table className="table-dense w-full">
              <thead><tr><th className="w-8"><input type="checkbox" checked={allSelected} onChange={() => setSelected(allSelected ? new Set() : new Set(items.map((i) => i.key)))} aria-label="Select all" /></th><th>Ticket</th><th className="w-28">Guessed level</th><th>Why</th><th className="w-36">Labels</th><th className="w-28">Updated</th></tr></thead>
              <tbody>
                {items.map((i) => (
                  <tr key={i.key} className={cn(selected.has(i.key) && 'bg-surface-2', i.copiedAs && 'text-ink-3')}>
                    <td><input type="checkbox" checked={selected.has(i.key)} onChange={() => toggle(i.key)} disabled={!!i.copiedAs} aria-label={`Select #${i.number}`} /></td>
                    <td><div className="font-medium">{i.title}</div><div className="mono text-[11.5px] text-ink-3">{i.repository} #{i.number}{i.url ? <a href={i.url} target="_blank" rel="noreferrer" className="ml-1 inline-flex align-middle"><LuExternalLink size={11} /></a> : null}{i.copiedAs ? <span className="ml-2 text-ok">copied → {i.copiedAs.split(':').pop()}</span> : null}{i.bodyLength === 0 ? <span className="ml-2 text-warn">no description</span> : null}</div></td>
                    <td><LevelChip type={i.guessedType} /></td>
                    <td className="text-[12px] text-ink-2">{i.guessReason}</td>
                    <td className="text-[11.5px] text-ink-3">{i.labels.slice(0, 4).join(', ')}</td>
                    <td className="text-[12px] text-ink-3">{relativeTime(i.updatedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Section>
      <Section title="Intake runs">
        {!runs.data ? <Skeleton rows={2} /> : runs.data.length === 0 ? <p className="px-4 py-3 text-ink-3">No intake runs on this machine yet.</p> : (
          <table className="table-dense w-full"><thead><tr><th>Run</th><th className="w-32">When</th><th className="w-40">Copied</th><th>Notes</th></tr></thead><tbody>
            {runs.data.map((m) => <tr key={m.id}><td><Mono>{m.id}</Mono> <span className="text-ink-3">{m.source}</span></td><td className="text-ink-2">{relativeTime(m.startedAt)}</td><td className="num">{m.items.length} of {m.requested}</td><td className="text-[12px] text-ink-2">{m.skipped.length ? `${m.skipped.length} already present` : ''}{m.errors.length ? <span className="text-err"> · {m.errors.length} errors</span> : ''}</td></tr>)}
          </tbody></table>
        )}
      </Section>
    </Page>
  );
}

function PreviewPanel({ preview, onBack, onRun, running, job, open, setOpen }: { preview: z.infer<typeof IntakePreview>; onBack: () => void; onRun: () => void; running: boolean; job: { progressDone: number; progressTotal: number; message?: string | null; state: string } | null; open: string | null; setOpen: (k: string | null) => void }) {
  const todo = preview.drafts.filter((d) => !d.existingKey);
  return (
    <Section title={`Ready for the sandbox · ${todo.length} to copy${preview.alreadyCopied ? `, ${preview.alreadyCopied} already there` : ''}`} aside={<><Button size="sm" variant="quiet" onClick={onBack}>Back</Button><Button size="sm" variant="primary" busy={running} disabled={todo.length === 0} onClick={onRun}><LuArrowRightToLine size={13} />Copy {todo.length} into the sandbox</Button></>}>
      {job && running ? <div className="px-4 py-2"><Progress done={job.progressDone} total={job.progressTotal} label={job.message ?? job.state} /></div> : null}
      <table className="table-dense w-full">
        <thead><tr><th className="w-6" /><th>Becomes</th><th className="w-28">Level</th><th className="w-24">Confidence</th><th>Why</th><th>Gaps to fix next</th></tr></thead>
        <tbody>
          {preview.drafts.map((d) => (
            <DraftRow key={d.sourceKey} d={d} open={open === d.sourceKey} onToggle={() => setOpen(open === d.sourceKey ? null : d.sourceKey)} />
          ))}
        </tbody>
      </table>
    </Section>
  );
}

function DraftRow({ d, open, onToggle }: { d: IntakeDraft; open: boolean; onToggle: () => void }) {
  return (
    <>
      <tr className={cn('cursor-pointer', open && 'bg-surface-2', d.existingKey && 'text-ink-3')} onClick={onToggle}>
        <td className="text-ink-3">{open ? '▾' : '▸'}</td>
        <td><div className="font-medium">{d.title}</div><div className="mono text-[11.5px] text-ink-3">{d.canonicalId} · from {d.sourceRepository} #{d.sourceNumber}{d.theme ? ` · ${d.theme}` : ''}{d.existingKey ? ` · already in the sandbox as ${d.existingKey.split(':').pop()}` : ''}</div></td>
        <td><LevelChip type={d.typeToken} /></td>
        <td><Pill tone={d.confidence === 'high' ? 'ok' : d.confidence === 'low' ? 'warn' : 'neutral'}>{d.confidence}</Pill></td>
        <td className="text-[12px] text-ink-2">{d.reason}</td>
        <td className="flex flex-wrap gap-1">{d.gaps.map((g) => <Pill key={g} tone="neutral">{humanizeCode(g)}</Pill>)}{d.gaps.length === 0 ? <span className="text-ok">ready</span> : null}</td>
      </tr>
      {open ? <tr className="bg-surface-2/60"><td colSpan={6} className="px-4 pb-4 pt-1"><DiffView before={d.narrative} after={d.body} /></td></tr> : null}
    </>
  );
}
