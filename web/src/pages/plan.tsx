import { useState } from 'react';
import { LuCalendarRange, LuSparkles, LuUsers } from 'react-icons/lu';
import { navigate } from '../app';
import { Button, Checkbox, Empty, Field, Mono, Notice, Page, Pill, Section, Select, cn, useToast } from '../components/ui';
import { describeError, post } from '../lib/api';
import { shortDate } from '../lib/format';
import { PlanResult, TeamInfo, type PlannedStory, type Status } from '../lib/schemas';
import { invalidate, useResource } from '../lib/useResource';
import { Skeleton } from './overview';

/**
 * Sprint planning: preview what the planner would place in the next sprints and who would take it, then turn
 * the plan into proposals. Capacity comes from config/team.json, skills from Shellback peer cards.
 */
export function PlanPage({ status }: { status: Status | undefined }) {
  const team = useResource('/api/team', TeamInfo);
  const toast = useToast();
  const [sprintsAhead, setSprintsAhead] = useState(2);
  const [includeCurrent, setIncludeCurrent] = useState(true);
  const [requireReady, setRequireReady] = useState(true);
  const [commitShare, setCommitShare] = useState<string>('');
  const [plan, setPlan] = useState<PlanResult | null>(null);
  const [busy, setBusy] = useState<'preview' | 'propose' | null>(null);
  const noSnapshot = status ? !status.snapshot : true;

  const run = async (save: boolean) => {
    setBusy(save ? 'propose' : 'preview');
    try {
      const body: Record<string, unknown> = { sprintsAhead, includeCurrent, requireReady };
      if (commitShare) body.commitShare = Number(commitShare) / 100;
      const r = PlanResult.parse(await post(save ? '/api/plan/propose' : '/api/plan/preview', body));
      setPlan(r);
      if (save && r.batch) {
        invalidate('/api/proposals');
        toast.push('ok', `${r.batch.total} sprint-plan proposals ready.`);
        navigate('proposals', { batch: r.batch.id });
      }
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setBusy(null);
    }
  };

  const t = team.data;
  const p = plan?.plan;
  return (
    <Page
      title="Plan"
      lede="Which ready stories go into the next sprints and who takes them: priority by feature target, capacity from the team, owners by skill match. The plan becomes proposals you approve; nothing is written here."
      actions={<><Button onClick={() => void run(false)} busy={busy === 'preview'} disabled={noSnapshot}><LuCalendarRange size={14} />Preview plan</Button><Button variant="primary" onClick={() => void run(true)} busy={busy === 'propose'} disabled={noSnapshot || !p || p.placed === 0}><LuSparkles size={14} />Propose this plan</Button></>}
    >
      {noSnapshot ? <Notice tone="warn">Synchronize the sandbox first.</Notice> : null}
      <div className="grid gap-5 xl:grid-cols-[minmax(0,2fr)_minmax(300px,1fr)]">
        <Section title="Planning window">
          <div className="grid grid-cols-2 gap-3 px-4 py-3 md:grid-cols-4">
            <Field label="Sprints ahead"><Select value={String(sprintsAhead)} onChange={(e) => setSprintsAhead(Number(e.target.value))}>{[1, 2, 3, 4].map((n) => <option key={n} value={n}>{n}</option>)}</Select></Field>
            <Field label="Commit share" hint="% of capacity to plan; blank = team default"><Select value={commitShare} onChange={(e) => setCommitShare(e.target.value)}><option value="">team default</option>{[50, 60, 70, 75, 80, 90, 100].map((n) => <option key={n} value={n}>{n}%</option>)}</Select></Field>
            <div className="col-span-2 flex flex-col justify-end gap-1.5 pb-5">
              <Checkbox label="Top up the current sprint first" checked={includeCurrent} onChange={(e) => setIncludeCurrent(e.target.checked)} />
              <Checkbox label="Only stories that meet the definition of ready" checked={requireReady} onChange={(e) => setRequireReady(e.target.checked)} />
            </div>
          </div>
        </Section>
        <Section title="Team" aside={t ? <span className="text-[12px] text-ink-3">{t.cardsImported} Shellback cards</span> : null}>
          {!t ? <Skeleton rows={3} /> : t.teams.length === 0 ? <Empty title="No team configured">Add teams and people to <Mono>config/team.json</Mono>; drop Shellback peer cards in <Mono>config/shellback/</Mono> for skills.</Empty> : (
            <div className="px-4 py-3 text-[12.5px]">
              {t.teams.map((tm) => <div key={tm.id} className="mb-2 flex items-center gap-2"><LuUsers size={14} className="text-ink-3" /><span className="font-medium">{tm.name}</span><span className="text-ink-3">{tm.pointsPerSprint} pts/sprint · {tm.members} people · {Math.round(tm.committed * 100)}% committed</span></div>)}
              <ul className="flex flex-col gap-1">
                {t.people.map((person) => (
                  <li key={person.id} className="flex flex-wrap items-center gap-x-2 gap-y-0.5">
                    <span className="w-32 font-medium">{person.name}</span>
                    <span className="num text-ink-2">{person.pointsPerSprint} pts</span>
                    <span className="text-ink-3">{Object.keys(person.skills).length} skills{person.fromShellback ? ' · Shellback' : ''}{person.availability !== 'unknown' ? ` · ${person.availability}` : ''}</span>
                  </li>
                ))}
              </ul>
              <div className="mt-2 text-ink-3">Sprint {t.sprint.current} now · {t.sprint.lengthDays}-day sprints from {t.sprint.firstStart}.</div>
              {t.warnings.map((w, i) => <Notice key={i} tone="warn" className="mt-2">{w}</Notice>)}
            </div>
          )}
        </Section>
      </div>

      {!p ? <Section><Empty title="No plan yet">Click <strong>Preview plan</strong> to see what the planner would do.</Empty></Section> : (
        <>
          {p.notes.map((n, i) => <Notice key={i} tone={n.includes('nobody') ? 'warn' : 'info'}>{n}</Notice>)}
          <div className="grid gap-4" style={{ gridTemplateColumns: `repeat(${Math.max(1, p.sprints.length)}, minmax(0, 1fr))` }}>
            {p.sprints.map((s) => (
              <Section key={s.sprint} title={<span>{s.sprint}{s.isCurrent ? <span className="ml-1 text-ink-3">(now)</span> : ''}</span>} aside={<span className="text-[11.5px] text-ink-3">{shortDate(s.start).split(',')[0]} – {shortDate(s.end).split(',')[0]}</span>}>
                <div className="px-4 py-2.5">
                  <div className="flex items-baseline justify-between text-[12.5px]"><span className="text-ink-2">Committed capacity</span><span className="num">{s.existing + s.proposed} / {s.commitCapacity}<span className="text-ink-3"> of {s.capacity}</span></span></div>
                  <div className="mt-1 flex h-2 w-full overflow-hidden rounded-full bg-surface-2">
                    <span className="h-full" style={{ width: `${Math.min(100, (s.existing / Math.max(1, s.commitCapacity)) * 100)}%`, background: 'var(--ink-3)' }} title="already planned" />
                    <span className="h-full" style={{ width: `${Math.min(100, (s.proposed / Math.max(1, s.commitCapacity)) * 100)}%`, background: 'var(--accent)' }} title="proposed" />
                  </div>
                  <ul className="mt-2 flex flex-col gap-0.5 text-[11.5px] text-ink-2">
                    {s.people.map((pl) => <li key={pl.id} className="flex justify-between"><span>{pl.name}{pl.availability === 'limited' ? <span className="text-ink-3"> (limited)</span> : ''}</span><span className="num">{pl.existing + pl.proposed}/{pl.capacity}{pl.proposed ? <span className="text-accent"> (+{pl.proposed})</span> : ''}</span></li>)}
                  </ul>
                </div>
                <ul className="hairline">
                  {s.stories.map((st) => <StoryRow key={st.key} s={st} />)}
                  {s.stories.length === 0 ? <li className="px-4 py-3 text-ink-3">Nothing planned.</li> : null}
                </ul>
              </Section>
            ))}
          </div>
          <div className="grid gap-5 lg:grid-cols-3">
            <Section title="Skill gaps" aside={<span className="text-[11.5px] text-ink-3">nobody on the team</span>}>
              {Object.keys(p.skillGaps).length === 0 ? <p className="px-4 py-3 text-ok">Every planned story has someone with its skills.</p> : (
                <ul className="flex flex-col gap-1 px-4 py-3 text-[12.5px]">{Object.entries(p.skillGaps).sort((a, b) => b[1] - a[1]).map(([k, n]) => <li key={k} className="flex justify-between"><Mono>{k}</Mono><span className="num text-ink-2">×{n}</span></li>)}</ul>
              )}
              <p className="hairline px-4 py-2 text-[11.5px] text-ink-3">These are the skills to grow or hire for; Shellback growth plans are the place to take them.</p>
            </Section>
            <Section title={`Does not fit (${p.unplaced.length})`}>
              {p.unplaced.length === 0 ? <p className="px-4 py-3 text-ok">Everything ready fits in the window.</p> : <ul className="flex flex-col gap-1 px-4 py-3 text-[12.5px]">{p.unplaced.map((u) => <li key={u.key}><span className="font-medium">{u.title}</span> <span className="num text-ink-3">{u.points} pts</span><div className="text-[11.5px] text-ink-3">{u.reason}</div></li>)}</ul>}
            </Section>
            <Section title={`Skipped (${p.skipReasons.length})`}>
              {p.skipReasons.length === 0 ? <p className="px-4 py-3 text-ink-3">Nothing skipped.</p> : <ul className="scroll-y max-h-64 flex flex-col gap-0.5 px-4 py-3 text-[12px] text-ink-2">{p.skipReasons.map((r, i) => <li key={i}>{r}</li>)}</ul>}
            </Section>
          </div>
        </>
      )}
    </Page>
  );
}

function StoryRow({ s }: { s: PlannedStory }) {
  return (
    <li className={cn('flex flex-col gap-0.5 px-4 py-2 text-[12.5px]', s.alreadyPlanned ? 'text-ink-3' : '')} title={s.reason}>
      <div className="flex items-center gap-2">
        <button type="button" className={cn('min-w-0 flex-1 truncate text-left', !s.alreadyPlanned && 'font-medium text-ink hover:text-accent')} onClick={() => navigate('hierarchy', { item: s.key })}>{s.title}</button>
        <span className="num shrink-0">{s.points}</span>
      </div>
      <div className="flex flex-wrap items-center gap-1.5 text-[11.5px]">
        {s.owner ? <span className={s.alreadyPlanned ? '' : 'text-ink-2'}>{s.owner}</span> : <Pill tone="warn">unassigned</Pill>}
        {s.missingSkills.length > 0 ? <Pill tone="warn">gap: {s.missingSkills.join(', ')}</Pill> : null}
        {!s.alreadyPlanned && s.target ? <span className="text-ink-3">target {s.target}</span> : null}
        {s.alreadyPlanned ? <span className="text-ink-3">planned</span> : <span className="text-accent">proposed</span>}
      </div>
    </li>
  );
}
