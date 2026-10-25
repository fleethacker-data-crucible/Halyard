import { useEffect, useMemo, useState } from 'react';
import { LuDownload, LuSparkles, LuWrench, LuX } from 'react-icons/lu';
import { navigate } from '../app';
import { Button, Empty, Input, LevelChip, Mono, Notice, Page, Pill, Progress, Section, Select, SeverityIcon, Tabs, cn, severityTone, useToast } from '../components/ui';
import { describeError, post } from '../lib/api';
import { downloadText, humanizeCode, toCsv } from '../lib/format';
import { useJob } from '../lib/jobs';
import { LEVELS, LEVEL_LABEL } from '../lib/levels';
import { Batch, Review, type LevelToken, type ReviewItem, type Status } from '../lib/schemas';
import { invalidate, useResource } from '../lib/useResource';
import { Skeleton } from './overview';

const CONTENT_GAPS = new Set(['story_missing_acceptance_criteria', 'story_missing_user_need', 'story_missing_tasks', 'feature_missing_done_when', 'feature_missing_short_description', 'epic_missing_outcome_statement', 'epic_missing_success_picture', 'theme_missing_purpose', 'theme_missing_scope']);
const META_GAPS = new Set(['missing_halyard_metadata', 'invalid_halyard_metadata', 'story_missing_points', 'story_invalid_points', 'story_missing_sprint', 'story_has_effort_band', 'feature_has_story_points', 'feature_missing_effort', 'feature_invalid_effort', 'epic_missing_theme']);

/** Which proposal paths can act on an item, from its open gaps. Drives the bulk-action bar. */
export function remedies(i: ReviewItem): Array<'refine' | 'metadata' | 'parents' | 'sizes'> {
  const out: Array<'refine' | 'metadata' | 'parents' | 'sizes'> = [];
  if (i.deficiencies.some((d) => CONTENT_GAPS.has(d))) out.push('refine');
  if (i.deficiencies.some((d) => META_GAPS.has(d)) || i.missingMeta.length > 0) out.push('metadata');
  if (!i.resolvedParentKey && (i.type === 'EPIC' || i.type === 'FEATURE' || i.type === 'STORY' || i.type === 'TASK')) out.push('parents');
  if ((i.type === 'STORY' && (!i.points || i.points === 'TBD')) || (i.type === 'FEATURE' && (!i.effort || i.effort === 'TBD'))) out.push('sizes');
  return out;
}

type Tab = 'items' | 'gaps' | 'findings' | 'parents';

export function ReviewPage({ params, status }: { params: URLSearchParams; status: Status | undefined }) {
  const label = params.get('label') ?? status?.config.labelFilter ?? '';
  const review = useResource(status?.snapshot ? `/api/review${label ? `?label=${encodeURIComponent(label)}` : ''}` : null, Review);
  const [tab, setTab] = useState<Tab>((params.get('tab') as Tab) || 'items');
  const [type, setType] = useState<string>(params.get('type') ?? 'ALL');
  const [code, setCode] = useState<string>(params.get('code') ?? '');
  const [onlyProblems, setOnlyProblems] = useState(true);
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const r = review.data;
  useEffect(() => setSelected(new Set()), [r?.snapshot?.id]);

  const items = useMemo(() => {
    if (!r) return [];
    let list = r.items;
    if (type !== 'ALL') list = list.filter((i) => i.type === type);
    if (onlyProblems && tab === 'items') list = list.filter((i) => i.needsReview);
    if (code) list = list.filter((i) => i.deficiencies.includes(code) || i.findings.some((f) => f.code === code) || i.missingMeta.includes(code));
    if (query) {
      const q = query.toLowerCase();
      list = list.filter((i) => i.title.toLowerCase().includes(q) || i.canonicalId.toLowerCase().includes(q) || String(i.number) === q);
    }
    return [...list].sort((a, b) => (b.findings.length + b.deficiencies.length + b.missingMeta.length) - (a.findings.length + a.deficiencies.length + a.missingMeta.length));
  }, [r, type, onlyProblems, code, query, tab]);

  const exportCsv = () => {
    if (!r) return;
    const rows = items.map((i) => ({ number: i.number, id: i.canonicalId, type: i.type, title: i.title, parent: i.parentId ?? '', points: i.points ?? '', effort: i.effort ?? '', sprint: i.sprint ?? '', state: i.state, missingMeta: i.missingMeta, gaps: i.deficiencies, findings: i.findings.map((f) => f.code), url: i.url ?? '' }));
    downloadText(`halyard-review-${r.snapshot?.id ?? 'snapshot'}.csv`, `# snapshot ${r.snapshot?.id ?? ''} taken ${r.snapshot?.takenAt ?? ''}; exported ${new Date().toISOString()}\r\n` + toCsv(rows, ['number', 'id', 'type', 'title', 'parent', 'points', 'effort', 'sprint', 'state', 'missingMeta', 'gaps', 'findings', 'url']), 'text/csv');
  };
  const exportMarkdown = () => {
    if (!r) return;
    downloadText(`halyard-review-${r.snapshot?.id ?? 'snapshot'}.md`, reviewPackMarkdown(r, items), 'text/markdown');
  };
  const visibleNumbers = items.map((i) => i.number);
  const allSelected = visibleNumbers.length > 0 && visibleNumbers.every((n) => selected.has(n));
  const toggleAll = () => setSelected(allSelected ? new Set() : new Set(visibleNumbers));
  const toggle = (n: number) => setSelected((s) => { const next = new Set(s); if (next.has(n)) next.delete(n); else next.add(n); return next; });

  if (status && !status.snapshot) {
    return <Page title="Review" lede="Every item checked against its level's definition of ready and the declared topology."><Section><Empty title="No snapshot yet" action={<Button variant="primary" onClick={() => navigate('connection')}>Connect and synchronize</Button>}>Synchronize a repository to review it.</Empty></Section></Page>;
  }

  return (
    <Page
      title="Review"
      lede={r ? <>{r.summary.needsReview} of {r.summary.total} items need attention{r.labelFilter ? <>, within <Mono>{r.labelFilter}</Mono></> : null}. Nothing here changes the source; it is a reading of the snapshot.</> : 'Every item checked against its level\'s definition of ready and the declared topology.'}
      actions={<><Button onClick={exportMarkdown} disabled={!r}><LuDownload size={14} />Review pack (Markdown)</Button><Button variant="quiet" onClick={exportCsv} disabled={!r}>CSV</Button></>}
    >
      {review.error ? <p className="text-err">{review.error}</p> : null}
      {r && selected.size > 0 ? <BulkActions items={r.items.filter((i) => selected.has(i.number))} status={status} onClear={() => setSelected(new Set())} /> : null}
      <Section>
        <Tabs
          value={tab}
          onChange={(t) => { setTab(t); setCode(''); }}
          items={[
            { value: 'items', label: 'Work items', count: r?.summary.total },
            { value: 'gaps', label: 'Definition-of-ready gaps', count: r ? Object.values(r.summary.deficiencyCounts).reduce((a, b) => a + b, 0) : undefined },
            { value: 'findings', label: 'Topology findings', count: r?.audit.findings.length },
            { value: 'parents', label: 'Parent candidates', count: r?.parentReviews.length },
          ]}
        />
        {tab === 'items' ? (
          <>
            <div className="flex flex-wrap items-center gap-3 px-4 py-2.5">
              <Select value={type} onChange={(e) => setType(e.target.value)} className="w-40" aria-label="Level">
                <option value="ALL">All levels</option>
                {[...LEVELS, 'BUG', 'UNCLASSIFIED'].map((l) => <option key={l} value={l}>{LEVEL_LABEL[l as LevelToken]}</option>)}
              </Select>
              <Input placeholder="Find by title, id or #number" value={query} onChange={(e) => setQuery(e.target.value)} className="w-64" aria-label="Find" />
              <label className="inline-flex items-center gap-2 text-ink-2"><input type="checkbox" checked={onlyProblems} onChange={(e) => setOnlyProblems(e.target.checked)} />Only items that need attention</label>
              {code ? <Pill tone="info">filter: {humanizeCode(code)} <button type="button" className="ml-1 underline" onClick={() => setCode('')}>clear</button></Pill> : null}
              <span className="ml-auto text-ink-3">{items.length} shown{selected.size ? ` · ${selected.size} selected` : ''}</span>
            </div>
            {!r ? <Skeleton rows={8} /> : items.length === 0 ? <Empty title="No items match">Loosen the filters or untick "only items that need attention".</Empty> : (
              <div className="scroll-y max-h-[calc(100vh-300px)]">
                <table className="table-dense w-full">
                  <thead><tr><th className="w-8"><input type="checkbox" checked={allSelected} onChange={toggleAll} aria-label="Select all shown" /></th><th className="w-24">Level</th><th>Work item</th><th className="w-40">Parent</th><th className="w-20">Size</th><th>Gaps and findings</th><th className="w-36">Fix with</th></tr></thead>
                  <tbody>{items.map((i) => <ItemRow key={i.key} item={i} selected={selected.has(i.number)} onToggle={() => toggle(i.number)} showRepo={(r?.snapshot?.repositories.length ?? 1) > 1} />)}</tbody>
                </table>
              </div>
            )}
          </>
        ) : null}
        {tab === 'gaps' && r ? <CodeTable rows={Object.entries(r.summary.deficiencyCounts).map(([c, n]) => ({ code: c, count: n }))} onPick={(c) => { setCode(c); setTab('items'); setOnlyProblems(false); }} explain={(c) => explainDeficiency(c)} empty="Every item meets its definition of ready." /> : null}
        {tab === 'findings' && r ? <FindingsTable review={r} /> : null}
        {tab === 'parents' && r ? <ParentsTable review={r} /> : null}
      </Section>
    </Page>
  );
}

const REMEDY_LABEL = { refine: 'Draft sections', metadata: 'Repair meta', parents: 'Find parent', sizes: 'Size' } as const;

function ItemRow({ item: i, selected, onToggle, showRepo }: { item: ReviewItem; selected: boolean; onToggle: () => void; showRepo: boolean }) {
  const fixes = remedies(i);
  return (
    <tr className={cn('cursor-pointer', selected && 'bg-surface-2')} onClick={() => navigate('hierarchy', { item: i.key })}>
      <td onClick={(e) => e.stopPropagation()}><input type="checkbox" checked={selected} onChange={onToggle} aria-label={`Select #${i.number}`} /></td>
      <td><LevelChip type={i.type} /></td>
      <td><div className="font-medium">{i.title}</div><div className="mono text-[11.5px] text-ink-3">{showRepo ? i.repository + ' ' : ''}#{i.number} · {i.canonicalId}</div></td>
      <td className="text-ink-2">{i.resolvedParentKey ? <span className="text-ok">resolved</span> : i.parentId ? <span className="text-warn" title={i.parentId}>declared, unresolved</span> : <span className="text-ink-3">none</span>}</td>
      <td className="num text-ink-2">{i.points ?? i.effort ?? <span className="text-ink-3">—</span>}</td>
      <td>
        <div className="flex flex-wrap gap-1.5">
          {i.findings.map((f) => <Pill key={f.code} tone={severityTone(f.severity)}><SeverityIcon severity={f.severity} size={11} />{humanizeCode(f.code)}</Pill>)}
          {i.deficiencies.map((d) => <Pill key={d} tone="neutral">{humanizeCode(d)}</Pill>)}
          {i.missingMeta.length > 0 ? <Pill tone="neutral">meta: {i.missingMeta.join(', ')}</Pill> : null}
          {!i.needsReview ? <span className="text-ok">complete</span> : null}
        </div>
      </td>
      <td className="text-[11.5px] text-ink-3">{fixes.length ? fixes.map((f) => REMEDY_LABEL[f]).join(' · ') : i.needsReview ? 'human' : ''}</td>
    </tr>
  );
}

/** Floating bar for the selected items: each proposal path runs on just those numbers and lands in Proposals. */
function BulkActions({ items, status, onClear }: { items: ReviewItem[]; status: Status | undefined; onClear: () => void }) {
  const toast = useToast();
  const numbers = items.map((i) => i.number);
  const counts = { refine: 0, metadata: 0, parents: 0, sizes: 0 };
  for (const i of items) for (const f of remedies(i)) counts[f]++;
  const hasModel = !!status?.config.hasLlmToken;
  const [busy, setBusy] = useState<string | null>(null);
  const job = useJob((j) => {
    if (j.state === 'succeeded') {
      const r = j.result as { id?: string; total?: number } | null;
      toast.push('ok', `${r?.total ?? 0} proposals ready.`);
      invalidate('/api/proposals');
      if (r?.id) navigate('proposals', { batch: r.id });
    } else if (j.state === 'failed') toast.push('err', j.error ?? 'Analysis failed');
  });
  const deterministic = async (path: string) => {
    setBusy(path);
    try {
      const b = Batch.parse(await post(path, { numbers }));
      toast.push('ok', `${b.proposals.length} proposals ready.`);
      invalidate('/api/proposals');
      navigate('proposals', { batch: b.id });
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setBusy(null);
    }
  };
  const stories = items.filter((i) => i.type === 'STORY').length;
  const features = items.filter((i) => i.type === 'FEATURE').length;
  return (
    <div className="panel flex flex-wrap items-center gap-2 px-4 py-2.5">
      <span className="font-medium">{items.length} selected</span>
      <span className="text-[12.5px] text-ink-3">Proposals are made for these items only; you approve them on the Proposals page.</span>
      <span className="ml-auto flex flex-wrap items-center gap-2">
        <Button size="sm" onClick={() => void deterministic('/api/intelligence/metadata')} busy={busy === '/api/intelligence/metadata'} disabled={counts.metadata === 0}><LuWrench size={13} />Repair metadata ({counts.metadata})</Button>
        <Button size="sm" onClick={() => void deterministic('/api/intelligence/parents')} busy={busy === '/api/intelligence/parents'} disabled={counts.parents === 0}>Find parents ({counts.parents})</Button>
        <Button size="sm" variant="primary" onClick={() => void job.start('/api/intelligence/refine', { numbers, maxItems: 100 })} busy={job.running} disabled={!hasModel || counts.refine === 0} title={hasModel ? undefined : 'Add a model key in Settings'}><LuSparkles size={13} />Draft sections ({counts.refine})</Button>
        <Button size="sm" variant="primary" onClick={() => void job.start('/api/intelligence/estimate', { numbers, kind: stories >= features ? 'storyPoints' : 'featureEffort', onlyUnsized: false, maxItems: 200 })} busy={job.running} disabled={!hasModel || (stories === 0 && features === 0)} title={hasModel ? undefined : 'Add a model key in Settings'}><LuSparkles size={13} />Propose {stories >= features ? 'points' : 'effort'} ({stories >= features ? stories : features})</Button>
        <Button size="sm" variant="quiet" onClick={onClear} aria-label="Clear selection"><LuX size={13} /></Button>
      </span>
      {job.job && job.running ? <div className="w-full"><Progress done={job.job.progressDone} total={job.job.progressTotal} label={job.job.message ?? job.job.state} /></div> : null}
      {job.error ? <Notice tone="err" className="w-full">{job.error}</Notice> : null}
    </div>
  );
}

/** A readable review pack for a refinement meeting: summary, gaps by code, then every item that needs attention. */
export function reviewPackMarkdown(r: Review, items: ReviewItem[]): string {
  const lines: string[] = [];
  lines.push(`# Halyard review pack`);
  lines.push('');
  lines.push(`Snapshot ${r.snapshot?.id ?? ''} of ${r.snapshot?.repository ?? ''} taken ${r.snapshot?.takenAt ?? ''}${r.labelFilter ? ` (filter ${r.labelFilter})` : ''}. Exported ${new Date().toISOString()}.`);
  lines.push('');
  lines.push('## Summary');
  lines.push('');
  lines.push(`| | |`); lines.push(`|---|---|`);
  lines.push(`| Work items | ${r.summary.total} |`);
  lines.push(`| Need attention | ${r.summary.needsReview} |`);
  lines.push(`| Without a resolved parent | ${r.summary.missingParents} |`);
  lines.push(`| Stories with points | ${r.summary.storiesWithPoints} of ${r.summary.stories} (${r.summary.totalPoints} points) |`);
  lines.push(`| Features with effort | ${r.summary.featuresWithEffort} of ${r.summary.features} |`);
  lines.push(`| Topology errors / warnings | ${r.audit.errorCount} / ${r.audit.warningCount} |`);
  lines.push('');
  const gaps = Object.entries(r.summary.deficiencyCounts).sort((a, b) => b[1] - a[1]);
  if (gaps.length) {
    lines.push('## Definition-of-ready gaps');
    lines.push('');
    lines.push('| Gap | Items |'); lines.push('|---|---|');
    for (const [code, n] of gaps) lines.push(`| ${humanizeCode(code)} (\`${code}\`) | ${n} |`);
    lines.push('');
  }
  const attention = items.filter((i) => i.needsReview);
  lines.push(`## Items needing attention (${attention.length})`);
  lines.push('');
  for (const i of attention) {
    lines.push(`### ${i.type} :: ${i.title.replace(/^\s*(THEME|EPIC|FEATURE|STORY|TASK|BUG)\s*::\s*/i, '')} (#${i.number})`);
    lines.push('');
    lines.push(`- Id: \`${i.canonicalId}\`${i.url ? ` · ${i.url}` : ''}`);
    lines.push(`- Parent: ${i.resolvedParentKey ? 'resolved' : i.parentId ? `declared \`${i.parentId}\`, unresolved` : 'none'}${i.points ? ` · Points: ${i.points}` : ''}${i.effort ? ` · Effort: ${i.effort}` : ''}${i.sprint ? ` · Sprint: ${i.sprint}` : ''}`);
    for (const f of i.findings) lines.push(`- **${f.severity}** ${humanizeCode(f.code)}: ${f.explanation}`);
    for (const d of i.deficiencies) lines.push(`- ${humanizeCode(d)}: ${explainDeficiency(d)}`);
    if (i.missingMeta.length) lines.push(`- Required meta missing: ${i.missingMeta.join(', ')}`);
    const fixes = remedies(i);
    if (fixes.length) lines.push(`- Suggested path: ${fixes.map((f) => REMEDY_LABEL[f]).join(', ')}`);
    lines.push('');
  }
  return lines.join('\n');
}

function CodeTable({ rows, onPick, explain, empty }: { rows: Array<{ code: string; count: number }>; onPick: (code: string) => void; explain: (code: string) => string; empty: string }) {
  if (rows.length === 0) return <Empty title={empty} />;
  return (
    <table className="table-dense w-full">
      <thead><tr><th className="w-80">Gap</th><th className="w-20">Items</th><th>What to do</th></tr></thead>
      <tbody>
        {rows.sort((a, b) => b.count - a.count).map((row) => (
          <tr key={row.code} className="cursor-pointer" onClick={() => onPick(row.code)}>
            <td className="font-medium">{humanizeCode(row.code)}<div className="mono text-[11px] text-ink-3">{row.code}</div></td>
            <td className="num">{row.count}</td>
            <td className="text-ink-2">{explain(row.code)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function FindingsTable({ review }: { review: Review }) {
  const [sev, setSev] = useState<'all' | 'error' | 'warning' | 'info'>('all');
  const rows = review.audit.findings.filter((f) => sev === 'all' || f.severity === sev);
  return (
    <>
      <div className="flex items-center gap-3 px-4 py-2.5">
        <Select value={sev} onChange={(e) => setSev(e.target.value as typeof sev)} className="w-40" aria-label="Severity">
          <option value="all">All severities</option><option value="error">Errors</option><option value="warning">Warnings</option><option value="info">Notes</option>
        </Select>
        <span className="text-ink-3">{rows.length} findings across {review.audit.affectedCount} items</span>
      </div>
      {rows.length === 0 ? <Empty title="No topology findings">Every declared parent resolves to the expected level and no chain loops.</Empty> : (
        <div className="scroll-y max-h-[calc(100vh-320px)]">
          <table className="table-dense w-full">
            <thead><tr><th className="w-8" /><th className="w-24">Level</th><th>Work item</th><th className="w-56">Finding</th><th>Explanation</th></tr></thead>
            <tbody>
              {rows.map((f, idx) => (
                <tr key={f.key + f.code + idx} className="cursor-pointer" onClick={() => navigate('hierarchy', { item: f.key })}>
                  <td><SeverityIcon severity={f.severity} /></td>
                  <td><LevelChip type={f.type} /></td>
                  <td><span className="font-medium">{f.title}</span> <span className="mono text-ink-3">#{f.number}</span></td>
                  <td><Pill tone={severityTone(f.severity)}>{humanizeCode(f.code)}</Pill></td>
                  <td className="text-ink-2">{f.explanation}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

function ParentsTable({ review }: { review: Review }) {
  if (review.parentReviews.length === 0) return <Empty title="Every item that needs a parent has one">Parent candidates appear here for epics, features, stories and tasks whose declared parent is missing or does not resolve.</Empty>;
  return (
    <div className="scroll-y max-h-[calc(100vh-300px)]">
      <table className="table-dense w-full">
        <thead><tr><th className="w-24">Level</th><th>Orphan</th><th>Best candidates (evidence)</th></tr></thead>
        <tbody>
          {review.parentReviews.map((p) => (
            <tr key={p.key}>
              <td><LevelChip type={p.type} /></td>
              <td><div className="font-medium">{p.title}</div><div className="mono text-[11.5px] text-ink-3">{p.canonicalId}{p.declaredParent ? ` · declared ${p.declaredParent}` : ''}</div></td>
              <td>
                {p.candidates.length === 0 ? <span className="text-ink-3">No candidate with evidence. Needs a human.</span> : (
                  <ol className="flex flex-col gap-1">
                    {p.candidates.slice(0, 3).map((c, i) => (
                      <li key={c.key} className="flex flex-wrap items-baseline gap-x-2">
                        <span className="num w-9 shrink-0 text-ink-3">{(c.score).toFixed(1)}</span>
                        <LevelChip type={c.type} compact />
                        <span className={i === 0 ? 'font-medium' : ''}>{c.title}</span>
                        <span className="text-[12px] text-ink-3">{c.evidence.join('; ')}</span>
                      </li>
                    ))}
                  </ol>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="px-4 py-3 text-[12.5px] text-ink-3">Candidates are proposals only. Turn them into reviewable changes from the Proposals page.</p>
    </div>
  );
}

function explainDeficiency(code: string): string {
  const map: Record<string, string> = {
    missing_halyard_metadata: 'Add a ~~~halyard-meta block with at least type and id.',
    invalid_halyard_metadata: 'The block exists but type or id is missing or unknown.',
    story_missing_feature_parent: 'Set parent: FEATURE-… in the story\'s meta.',
    story_missing_acceptance_criteria: 'Add an "Acceptance criteria" section with testable bullets.',
    story_missing_user_need: 'State who needs this, what and why.',
    story_missing_tasks: 'Add a task checklist inside the story.',
    story_missing_points: 'Set points to 1, 2, 3, 5, 8 or TBD.',
    story_invalid_points: 'Points must be 1, 2, 3, 5, 8 or TBD; 13 means split.',
    story_missing_sprint: 'Set sprint, or TBD until planned.',
    task_missing_story_parent: 'Set parent: STORY-… or fold the task into the story\'s checklist.',
    feature_missing_epic_parent: 'Set parent: EPIC-… in the feature\'s meta.',
    feature_missing_done_when: 'Add a Definition of done / Done-when checklist.',
    feature_missing_short_description: 'Describe the capability in a few sentences.',
    feature_missing_effort: 'Set effort to Small, Medium, Large or TBD.',
    feature_invalid_effort: 'Effort must be Small, Medium, Large or TBD.',
    epic_missing_outcome_statement: 'State the observable outcome when the epic is done.',
    epic_missing_success_picture: 'Add a success picture of three to seven bullets.',
    epic_missing_theme: 'Set theme: THEME-… in the epic\'s meta.',
    theme_missing_purpose: 'Say why the lane exists.',
    theme_missing_scope: 'Add in-scope and out-of-scope sections.',
    feature_has_story_points: 'Features use effort bands; move the points to stories.',
    story_has_effort_band: 'Stories use points; remove the effort band.',
  };
  return map[code] ?? 'Review the item against the playbook for its level.';
}

