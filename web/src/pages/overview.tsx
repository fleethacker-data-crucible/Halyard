import { LuArrowRight } from 'react-icons/lu';
import { navigate } from '../app';
import { CascadeBars, Coverage, Donut, HBars } from '../components/charts';
import { Button, Empty, LevelChip, Page, Section, SeverityIcon, Stat, cn } from '../components/ui';
import { humanizeCode, relativeTime } from '../lib/format';
import { Review, type Status } from '../lib/schemas';
import { useResource } from '../lib/useResource';

export function OverviewPage({ status }: { status: Status | undefined }) {
  const review = useResource(status?.snapshot ? '/api/review' : null, Review);
  const r = review.data;

  if (status && !status.snapshot) {
    return (
      <Page title="Overview" lede="Health of the backlog hierarchy, from the latest snapshot.">
        <Section>
          <Empty title="Nothing to show yet" action={<Button variant="primary" onClick={() => navigate('connection')}>Connect and synchronize<LuArrowRight size={14} /></Button>}>
            Connect to {status.config.provider === 'gitlab' ? 'GitLab' : 'GitHub'} and synchronize a repository. Every view works from that snapshot, never from live calls, so you can read without anyone noticing.
            {!status.provider.configured ? <p className="mt-2">No token is loaded yet; the launcher usually provides one from the vault or the environment.</p> : null}
          </Empty>
        </Section>
      </Page>
    );
  }

  const summary = r?.summary;
  const needsReview = r?.items.filter((i) => i.needsReview).sort((a, b) => b.findings.length + b.deficiencies.length - (a.findings.length + a.deficiencies.length)).slice(0, 8) ?? [];
  const deficiencyRows = Object.entries(summary?.deficiencyCounts ?? {}).map(([key, value]) => ({ key, label: humanizeCode(key), value })).sort((a, b) => b.value - a.value).slice(0, 8);
  const findingRows = Object.entries(summary?.findingCounts ?? {}).map(([key, value]) => ({ key, label: humanizeCode(key), value })).sort((a, b) => b.value - a.value).slice(0, 8);

  return (
    <Page
      title="Overview"
      lede={status?.snapshot ? <>Snapshot of <span className="mono text-ink">{status.snapshot.repository}</span> taken {relativeTime(status.snapshot.takenAt)}{r?.labelFilter ? <>, filtered to <span className="mono text-ink">{r.labelFilter}</span></> : null}.</> : 'Health of the backlog hierarchy, from the latest snapshot.'}
      actions={<Button onClick={() => navigate('connection')}>Synchronize again</Button>}
    >
      {review.error ? <p className="text-err">{review.error}</p> : null}
      <div className="grid gap-5 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <Section title="The cascade" aside={summary ? <span>{summary.total} work items</span> : null}>
          {summary ? <CascadeBars counts={summary.byType} onSelect={(level) => navigate('review', { type: level })} /> : <Skeleton rows={5} />}
        </Section>
        <Section title="Needs attention">
          {r ? (
            <div className="grid grid-cols-3 divide-x divide-line-2">
              <Stat label="Items to review" value={summary!.needsReview} hint={`of ${summary!.total}`} accent={summary!.needsReview > 0 ? 'var(--warn)' : 'var(--ok)'} />
              <Stat label="Without a parent" value={summary!.missingParents} hint="epics to tasks" accent={summary!.missingParents > 0 ? 'var(--warn)' : undefined} />
              <Stat label="Topology errors" value={r.audit.errorCount} hint={`${r.audit.warningCount} warnings`} accent={r.audit.errorCount > 0 ? 'var(--err)' : undefined} />
            </div>
          ) : <Skeleton rows={2} />}
          <div className="hairline grid grid-cols-2 divide-x divide-line-2">
            {summary ? <Coverage label="Stories with points" part={summary.storiesWithPoints} whole={summary.stories} color="var(--lv-story)" /> : null}
            {summary ? <Coverage label="Features with effort" part={summary.featuresWithEffort} whole={summary.features} color="var(--lv-feature)" /> : null}
          </div>
        </Section>
      </div>

      <div className="grid gap-5 lg:grid-cols-3">
        <Section title="Findings by severity">
          {r ? (
            <div className="px-4 py-3">
              <Donut
                slices={[
                  { label: 'Errors', value: r.audit.errorCount, color: 'var(--err)' },
                  { label: 'Warnings', value: r.audit.warningCount, color: 'var(--warn)' },
                  { label: 'Notes', value: r.audit.infoCount, color: 'var(--info)' },
                ]}
                centre={<div><div className="num text-[20px] font-semibold leading-none">{r.audit.affectedCount}</div><div className="text-[11px] text-ink-3">items</div></div>}
              />
            </div>
          ) : <Skeleton rows={3} />}
        </Section>
        <Section title="Definition-of-ready gaps" aside={<a href="#/review?tab=gaps" className="text-[12px]">All gaps</a>}>
          {r ? <HBars rows={deficiencyRows} color="var(--warn)" onSelect={(code) => navigate('review', { tab: 'gaps', code })} /> : <Skeleton rows={4} />}
          {r && deficiencyRows.length === 0 ? <p className="px-4 pb-3 text-ok">Every item meets its definition of ready.</p> : null}
        </Section>
        <Section title="Topology findings" aside={<a href="#/review?tab=findings" className="text-[12px]">All findings</a>}>
          {r ? <HBars rows={findingRows} color="var(--warn)" onSelect={(code) => navigate('review', { tab: 'findings', code })} /> : <Skeleton rows={4} />}
          {r && findingRows.length === 0 ? <p className="px-4 pb-3 text-ok">Every declared parent resolves to the right level.</p> : null}
        </Section>
      </div>

      <Section title="Most in need of review" aside={<a href="#/review" className="text-[12px]">Open review</a>}>
        {r && needsReview.length === 0 ? <Empty title="Nothing needs review">Every item in the snapshot has complete metadata, a resolved parent and the sections its level requires.</Empty> : null}
        {needsReview.length > 0 ? (
          <table className="table-dense w-full">
            <thead><tr><th className="w-24">Level</th><th>Work item</th><th>What is missing</th></tr></thead>
            <tbody>
              {needsReview.map((i) => (
                <tr key={i.key} className="cursor-pointer" onClick={() => navigate('hierarchy', { item: String(i.number) })}>
                  <td><LevelChip type={i.type} /></td>
                  <td><span className="font-medium">{i.title}</span> <span className="mono text-ink-3">#{i.number}</span></td>
                  <td className="text-ink-2">
                    <span className="flex flex-wrap items-center gap-x-3 gap-y-1">
                      {i.findings.slice(0, 2).map((f) => <span key={f.code} className="inline-flex items-center gap-1"><SeverityIcon severity={f.severity} size={12} />{humanizeCode(f.code)}</span>)}
                      {i.deficiencies.slice(0, 3).map((d) => <span key={d}>{humanizeCode(d)}</span>)}
                      {i.missingMeta.length > 0 ? <span className="text-ink-3">meta: {i.missingMeta.join(', ')}</span> : null}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : null}
      </Section>
    </Page>
  );
}

export function Skeleton({ rows }: { rows: number }) {
  return (
    <div className="flex flex-col gap-2 px-4 py-3" aria-busy>
      {Array.from({ length: rows }).map((_, i) => <div key={i} className={cn('h-3 animate-pulse rounded bg-surface-2', i % 2 ? 'w-2/3' : 'w-full')} />)}
    </div>
  );
}
