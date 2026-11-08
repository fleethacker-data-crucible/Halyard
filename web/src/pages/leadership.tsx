import { useState } from 'react';
import { LuDownload, LuExternalLink } from 'react-icons/lu';
import { navigate } from '../app';
import { WebhookPanel } from '../components/fleet-actions';
import { ReadAloud } from '../components/voice';
import { CapacityMeters, StatusOverTime, ThemeBars, VelocityChart } from '../components/delivery-charts';
import { Button, Empty, Mono, Notice, Page, Pill, Section, Stat, cn, useToast } from '../components/ui';
import { describeError, getText } from '../lib/api';
import { downloadText, relativeTime, shortDate, itemTitle } from '../lib/format';
import { Leadership, type Status } from '../lib/schemas';
import { useResource } from '../lib/useResource';
import { Skeleton } from './overview';

const STATUS: Record<Leadership['features'][number]['status'], { label: string; tone: 'ok' | 'warn' | 'neutral' | 'info'; color: string }> = {
  OnTrack: { label: 'On Track', tone: 'ok', color: 'var(--ok)' },
  AtRisk: { label: 'At Risk', tone: 'warn', color: 'var(--warn)' },
  Parked: { label: 'Parked', tone: 'neutral', color: 'var(--ink-3)' },
  Done: { label: 'Done', tone: 'info', color: 'var(--lv-epic)' },
};

/** The one-page leadership view: feature % complete first, then velocity, readiness, capacity and blockers. */
export function LeadershipPage({ status }: { status: Status | undefined }) {
  const data = useResource(status?.snapshot ? '/api/leadership' : null, Leadership);
  const toast = useToast();
  const [downloading, setDownloading] = useState(false);
  const d = data.data;

  const download = async () => {
    setDownloading(true);
    try {
      const html = await getText('/api/leadership/report.html');
      downloadText(`halyard-leadership-${d?.currentSprint ?? 'report'}.html`, html, 'text/html');
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setDownloading(false);
    }
  };

  if (status && !status.snapshot) {
    return <Page title="Leadership" lede="Feature progress, velocity, capacity and blockers on one page."><Section><Empty title="No snapshot yet" action={<Button variant="primary" onClick={() => navigate('connection')}>Connect and synchronize</Button>}>The leadership view reads the snapshot.</Empty></Section></Page>;
  }

  const velocityText = d ? d.velocity.filter((v) => v.isPast).slice(-3).map((v) => v.pointsDone).join(' → ') : '';
  const escalated = d?.blockers.filter((b) => b.escalated).length ?? 0;
  const avgAge = d && d.blockers.length ? (d.blockers.reduce((n, b) => n + b.ageBusinessDays, 0) / d.blockers.length).toFixed(1) : '0';

  return (
    <Page
      title="Leadership"
      lede={d ? <>Sprint <span className="font-medium text-ink">{d.currentSprint}</span> ({shortDate(d.sprintStart).split(',')[0]} – {shortDate(d.sprintEnd).split(',')[0]}) · snapshot of <Mono>{d.snapshot.repository}</Mono> {relativeTime(d.snapshot.takenAt)}. Feature progress • velocity • capacity • blockers.</> : 'Feature progress, velocity, capacity and blockers on one page.'}
      actions={<span className="flex items-center gap-2"><ReadAloud data={d} /><Button onClick={() => void download()} busy={downloading} disabled={!d}><LuDownload size={14} />Download HTML report</Button></span>}
    >
      {data.error ? <Notice tone="err">{data.error}</Notice> : null}
      {d?.notes.map((n, i) => <Notice key={i} tone="info">{n}</Notice>)}
      {d ? <WebhookPanel /> : null}
      <div className="grid gap-3 md:grid-cols-3 xl:grid-cols-5">
        <Section><Stat label="Features on track" value={d ? `${d.featuresOnTrack} / ${d.features.length}` : '…'} hint={d ? `${d.featuresActive} active` : ''} accent={d && d.features.length && d.featuresOnTrack / d.features.length < 0.6 ? 'var(--warn)' : 'var(--ok)'} /></Section>
        <Section><Stat label="Velocity (last 3)" value={velocityText || '—'} hint={d ? `pts / sprint · avg ${d.averageVelocity}` : ''} /></Section>
        <Section><Stat label="Ready backlog" value={d ? `${d.ready.stories} stories` : '…'} hint={d ? `${d.ready.points} pts · ${d.ready.healthy ? 'healthy (≥ 1.5× velocity)' : 'thin (< 1.5× velocity)'}` : ''} accent={d?.ready.healthy ? 'var(--ok)' : 'var(--warn)'} /></Section>
        <Section><Stat label="Capacity used" value={d ? (d.capacity.configured ? `${d.capacity.usedPercent}%` : 'n/a') : '…'} hint={d?.capacity.configured ? `${d.capacity.plannedPoints} of ${d.capacity.teamCapacity} pts · ${Math.round(d.capacity.committed * 100)}/${Math.round(d.capacity.allocated * 100)}/${Math.round(d.capacity.experiment * 100)} commit/alloc/exp` : 'no team config'} accent={d && d.capacity.usedPercent > 100 ? 'var(--err)' : undefined} /></Section>
        <Section><Stat label="Open blockers" value={d ? String(d.blockers.length) : '…'} hint={d ? (escalated ? `${escalated} escalated (> ${d.escalationBusinessDays} business days) · avg ${avgAge}d` : d.blockers.length ? `avg ${avgAge} business days` : 'none') : ''} accent={d ? (d.blockers.length === 0 ? 'var(--ok)' : escalated ? 'var(--err)' : 'var(--warn)') : undefined} /></Section>
      </div>

      <Section title="Feature % complete" aside={<span className="text-[12px] text-ink-3">primary leadership view</span>}>
        {!d ? <Skeleton rows={6} /> : d.features.length === 0 ? <Empty title="No features in the snapshot" /> : (
          <table className="table-dense w-full">
            <thead><tr><th>Feature</th><th className="w-48">Epic</th><th className="w-52">% done</th><th className="w-40">Status</th><th className="w-20">Target</th><th className="w-32">Owner</th></tr></thead>
            <tbody>
              {d.features.map((f) => {
                const st = STATUS[f.status];
                return (
                  <tr key={f.key} className="cursor-pointer" onClick={() => navigate('hierarchy', { item: f.key })}>
                    <td><div className="font-medium">{itemTitle(f.title)}</div><div className="text-[11.5px] text-ink-3">{f.storiesDone}/{f.stories} stories · {f.pointsDone}/{f.points} pts{f.blocked ? ` · ${f.blocked} blocked` : ''}</div></td>
                    <td className="text-ink-2">{f.epic ?? '—'}</td>
                    <td><div className="flex items-center gap-2"><span className="h-2 w-32 overflow-hidden rounded-full bg-surface-2"><span className="block h-full rounded-full" style={{ width: `${f.percent}%`, background: st.color }} /></span><span className="num text-ink">{f.percent}%</span></div></td>
                    <td><Pill tone={st.tone}>{st.label}</Pill><div className="text-[11px] text-ink-3">{f.reason}</div></td>
                    <td className="num text-ink-2">{f.target ?? '—'}</td>
                    <td className="text-ink-2">{f.owner ?? '—'}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </Section>

      <div className="grid gap-5 lg:grid-cols-2">
        <Section title="Feature status by sprint" aside={<span className="text-[12px] text-ink-3">past sprints reconstructed from the backlog; the current one is the table above</span>}>
          {!d ? <Skeleton rows={6} /> : <StatusOverTime points={d.statusHistory} />}
        </Section>
        <Section title="Velocity (story points closed)" aside={d ? <span className="text-[12px] text-ink-3">avg of the last 3 completed: {d.averageVelocity}</span> : null}>
          {!d ? <Skeleton rows={6} /> : <VelocityChart points={d.velocity} average={d.averageVelocity} />}
        </Section>
        <Section title={d ? `Sprint ${d.currentSprint} capacity by team` : 'Capacity by team'} aside={<span className="text-[12px] text-ink-3">from <Mono>config/team.json</Mono></span>}>
          {!d ? <Skeleton rows={3} /> : d.capacity.configured ? <CapacityMeters teams={d.teamCapacity} /> : <p className="px-4 py-3 text-ink-3">Add teams to <Mono>config/team.json</Mono> to assess capacity.</p>}
        </Section>
        <Section title="Points by theme">
          {!d ? <Skeleton rows={3} /> : <ThemeBars themes={d.themes} />}
        </Section>
      </div>

      <Section title="Open blockers (OPS / dependencies)" aside={d ? <span className="text-[12px] text-ink-3">escalated when age &gt; {d.escalationBusinessDays} business days</span> : null}>
        {!d ? <Skeleton rows={3} /> : d.blockers.length === 0 ? <p className="px-4 py-3 text-ok">No open blockers.</p> : (
          <table className="table-dense w-full">
            <thead><tr><th className="w-20">Item</th><th>Description</th><th className="w-28">Type</th><th className="w-20">Age</th><th className="w-32">Owner</th><th>Impact</th></tr></thead>
            <tbody>
              {d.blockers.map((b) => (
                <tr key={b.key} className="cursor-pointer" onClick={() => navigate('hierarchy', { item: b.key })}>
                  <td className="mono text-ink-3">#{b.number}{b.url ? <a href={b.url} target="_blank" rel="noreferrer" onClick={(e) => e.stopPropagation()} className="ml-1 inline-flex align-middle"><LuExternalLink size={11} /></a> : null}</td>
                  <td><div className="font-medium">{itemTitle(b.title)}</div>{b.reason ? <div className="text-[11.5px] text-ink-3">{b.reason}</div> : null}</td>
                  <td><Pill tone={b.type === 'ops' ? 'warn' : 'neutral'}>{b.type.toUpperCase()}</Pill></td>
                  <td className={cn('num', b.escalated ? 'font-semibold text-err' : 'text-ink')}>{b.ageBusinessDays}d</td>
                  <td className="text-ink-2">{b.owner ?? '—'}</td>
                  <td className="text-ink-2">{b.impact ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Section>
      <p className="text-[11.5px] text-ink-3">Primary object: feature % complete. Secondary: epic status, velocity trend, ready-backlog health, capacity split, blocker age. Story checklists and full proposals for work are deliberately not shown. Weekly email: <Mono>scripts\Register-HalyardReportSchedule.ps1</Mono>.</p>
    </Page>
  );
}
