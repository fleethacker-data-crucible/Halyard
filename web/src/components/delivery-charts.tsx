/**
 * Inline-SVG charts for the Leadership page. No chart library: thin marks, a 2px surface gap between
 * stacked segments, direct labels where there is room, a legend for anything with more than one series,
 * text in ink tokens (never the series colour), and a hover tooltip. Drawn in pixels against the measured
 * container width so text is never stretched. The emailed report draws the same four charts as HTML
 * tables (Analysis/DeliveryHtml.cs) because Outlook does not render SVG; keep the two in step.
 * Overview's own small charts live in charts.tsx.
 */
import { useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { cn } from './ui';
import { itemTitle } from '../lib/format';

/** Feature status palette: fixed, shared with the email renderer. */
export const STATUS_SERIES = [
  { key: 'done', label: 'Done', color: '#1e3a5f' },
  { key: 'onTrack', label: 'On track', color: '#0ca30c' },
  { key: 'atRisk', label: 'At risk', color: '#fab219' },
  { key: 'parked', label: 'Parked', color: '#9aa0a6' },
  { key: 'notStarted', label: 'Not started', color: '#d7dadf' },
] as const;
export type StatusKey = (typeof STATUS_SERIES)[number]['key'];
const SERIES_1 = 'var(--chart-1, #2a78d6)';
const INK = 'var(--ink)';
const INK3 = 'var(--ink-3)';
const GRID = 'var(--line-2)';

function useWidth<T extends HTMLElement>(): [React.RefObject<T | null>, number] {
  const ref = useRef<T | null>(null);
  const [w, setW] = useState(0);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => setW(el.getBoundingClientRect().width);
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  return [ref, w];
}

function statusColor(key: StatusKey): string {
  const s = STATUS_SERIES.find((x) => x.key === key)!;
  return key === 'done' ? `var(--chart-done, ${s.color})` : s.color;
}

export function Legend({ items, className }: { items: { label: string; color: string; outline?: boolean; dashed?: boolean }[]; className?: string }) {
  return (
    <div className={cn('flex flex-wrap gap-x-3 gap-y-1 text-[11.5px] text-ink-2', className)}>
      {items.map((i) => (
        <span key={i.label} className="inline-flex items-center gap-1.5">
          {i.dashed ? <span className="inline-block w-3 border-t border-dashed" style={{ borderColor: i.color }} /> : <span className="inline-block h-2.5 w-2.5 rounded-[3px]" style={i.outline ? { border: `1.5px dashed ${i.color}` } : { background: i.color }} />}
          {i.label}
        </span>
      ))}
    </div>
  );
}

function Tooltip({ x, y, children }: { x: number; y: number; children: ReactNode }) {
  return <div className="pointer-events-none absolute z-10 whitespace-nowrap rounded-ctl border border-line bg-surface px-2 py-1 text-[11.5px] text-ink shadow-sm" style={{ left: x, top: y, transform: 'translate(-50%, calc(-100% - 8px))' }}>{children}</div>;
}

function pos(e: React.MouseEvent<SVGElement>): { x: number; y: number } {
  const svg = (e.currentTarget as SVGElement).ownerSVGElement ?? (e.currentTarget as unknown as SVGSVGElement);
  const r = svg.getBoundingClientRect();
  const b = (e.currentTarget as SVGElement).getBoundingClientRect();
  return { x: b.left - r.left + b.width / 2, y: b.top - r.top };
}

// ---- stacked columns: feature status over time ----

export type StatusPoint = { sprint: string; isCurrent: boolean } & Record<StatusKey, number> & { total: number };

export function StatusOverTime({ points, height = 190 }: { points: StatusPoint[]; height?: number }) {
  const [ref, width] = useWidth<HTMLDivElement>();
  const [hover, setHover] = useState<{ i: number; key: StatusKey; x: number; y: number } | null>(null);
  if (points.length === 0) return <p className="px-4 py-3 text-[12.5px] text-ink-3">No sprints to show.</p>;
  const max = Math.max(1, ...points.map((p) => p.total));
  const padL = 30, padR = 10, padT = 14, padB = 24;
  const innerH = height - padT - padB;
  const innerW = Math.max(0, width - padL - padR);
  const slot = points.length ? innerW / points.length : 0;
  const barW = Math.max(10, Math.min(slot * 0.6, 44));
  const y = (v: number) => padT + innerH - (v / max) * innerH;
  return (
    <div ref={ref} className="relative px-2 pt-2">
      {width > 0 ? (
        <svg width={width} height={height} role="img" aria-label="Feature status by sprint" className="block">
          {niceTicks(max, 4).map((t) => <g key={t}><line x1={padL} x2={width - padR} y1={y(t)} y2={y(t)} stroke={GRID} /><text x={padL - 6} y={y(t)} textAnchor="end" dominantBaseline="middle" fill={INK3} fontSize={11}>{t}</text></g>)}
          {points.map((p, i) => {
            const cx = padL + slot * i + slot / 2;
            let acc = 0;
            return (
              <g key={p.sprint}>
                {STATUS_SERIES.map((s) => {
                  const v = p[s.key];
                  if (!v) return null;
                  const top = y(acc + v), bottom = y(acc);
                  acc += v;
                  const h = Math.max(1, bottom - top - 2);
                  const dim = hover !== null && (hover.i !== i || hover.key !== s.key);
                  return (
                    <g key={s.key}>
                      <rect x={cx - barW / 2} y={top} width={barW} height={h} fill={statusColor(s.key)} rx={2} opacity={dim ? 0.45 : 1}
                        onMouseEnter={(e) => setHover({ i, key: s.key, ...pos(e) })} onMouseLeave={() => setHover(null)}>
                        <title>{`${p.sprint} · ${s.label}: ${v} of ${p.total}`}</title>
                      </rect>
                      {h >= 14 && barW >= 18 ? <text x={cx} y={top + h / 2} textAnchor="middle" dominantBaseline="middle" fontSize={11} fill={s.key === 'done' ? '#ffffff' : s.key === 'onTrack' ? '#ffffff' : '#16202a'} pointerEvents="none">{v}</text> : null}
                    </g>
                  );
                })}
                <text x={cx} y={height - 7} textAnchor="middle" fill={p.isCurrent ? INK : INK3} fontSize={11} fontWeight={p.isCurrent ? 600 : 400}>{shortSprint(p.sprint)}{p.isCurrent ? ' (now)' : ''}</text>
              </g>
            );
          })}
        </svg>
      ) : <div style={{ height }} />}
      {hover ? <Tooltip x={hover.x} y={hover.y}>{points[hover.i]!.sprint} · {STATUS_SERIES.find((s) => s.key === hover.key)!.label}: <b>{points[hover.i]![hover.key]}</b> of {points[hover.i]!.total} features</Tooltip> : null}
      <Legend className="px-2 pb-3 pt-1" items={STATUS_SERIES.map((s) => ({ label: s.label, color: statusColor(s.key) }))} />
    </div>
  );
}

// ---- columns: velocity ----

export type VelocityPoint = { sprint: string; pointsDone: number; pointsPlanned: number; isCurrent: boolean; isPast: boolean };

export function VelocityChart({ points, average, height = 170 }: { points: VelocityPoint[]; average: number; height?: number }) {
  const [ref, width] = useWidth<HTMLDivElement>();
  const [hover, setHover] = useState<{ i: number; x: number; y: number } | null>(null);
  if (points.length === 0) return <p className="px-4 py-3 text-[12.5px] text-ink-3">No sprints to show.</p>;
  const max = Math.max(1, average, ...points.map((p) => Math.max(p.pointsDone, p.pointsPlanned)));
  const padL = 30, padR = 10, padT = 16, padB = 24;
  const innerH = height - padT - padB;
  const innerW = Math.max(0, width - padL - padR);
  const slot = innerW / points.length;
  const barW = Math.max(10, Math.min(slot * 0.6, 44));
  const y = (v: number) => padT + innerH - (v / max) * innerH;
  return (
    <div ref={ref} className="relative px-2 pt-2">
      {width > 0 ? (
        <svg width={width} height={height} role="img" aria-label="Points closed per sprint" className="block">
          {niceTicks(max, 4).map((t) => <g key={t}><line x1={padL} x2={width - padR} y1={y(t)} y2={y(t)} stroke={GRID} /><text x={padL - 6} y={y(t)} textAnchor="end" dominantBaseline="middle" fill={INK3} fontSize={11}>{t}</text></g>)}
          {points.map((p, i) => {
            const cx = padL + slot * i + slot / 2;
            const dim = hover !== null && hover.i !== i;
            return (
              <g key={p.sprint} onMouseEnter={(e) => setHover({ i, ...pos(e) })} onMouseLeave={() => setHover(null)}>
                {p.isCurrent && p.pointsPlanned > p.pointsDone ? <rect x={cx - barW / 2} y={y(p.pointsPlanned)} width={barW} height={Math.max(0, y(0) - y(p.pointsPlanned))} fill="none" stroke={SERIES_1} strokeWidth={1.5} strokeDasharray="4 3" rx={2} /> : null}
                <rect x={cx - barW / 2} y={y(p.pointsDone)} width={barW} height={Math.max(0, y(0) - y(p.pointsDone))} fill={SERIES_1} rx={2} opacity={dim ? 0.45 : 1}><title>{`${p.sprint}: ${p.pointsDone} pts closed${p.isCurrent ? ` of ${p.pointsPlanned} planned` : ''}`}</title></rect>
                <text x={cx} y={y(Math.max(p.pointsDone, p.isCurrent ? p.pointsPlanned : 0)) - 4} textAnchor="middle" fill={INK3} fontSize={11} paintOrder="stroke" stroke="var(--surface)" strokeWidth={3}>{p.pointsDone}{p.isCurrent && p.pointsPlanned > p.pointsDone ? `/${p.pointsPlanned}` : ''}</text>
                <text x={cx} y={height - 7} textAnchor="middle" fill={p.isCurrent ? INK : INK3} fontSize={11} fontWeight={p.isCurrent ? 600 : 400}>{shortSprint(p.sprint)}{p.isCurrent ? ' (now)' : ''}</text>
              </g>
            );
          })}
          {average > 0 ? <g><line x1={padL} x2={width - padR} y1={y(average)} y2={y(average)} stroke={INK3} strokeWidth={1.5} strokeDasharray="5 4" /><text x={width - padR} y={y(average) - 5} textAnchor="end" fill={INK3} fontSize={11} paintOrder="stroke" stroke="var(--surface)" strokeWidth={3}>avg {average}</text></g> : null}
        </svg>
      ) : <div style={{ height }} />}
      {hover ? <Tooltip x={hover.x} y={hover.y}>{points[hover.i]!.sprint}: <b>{points[hover.i]!.pointsDone}</b> pts closed{points[hover.i]!.isCurrent ? <> of {points[hover.i]!.pointsPlanned} planned</> : null}</Tooltip> : null}
      <Legend className="px-2 pb-3 pt-1" items={[{ label: 'Points closed', color: SERIES_1 }, { label: 'Planned this sprint', color: SERIES_1, outline: true }, { label: '3-sprint average', color: INK3, dashed: true }]} />
    </div>
  );
}

// ---- meters: capacity per team ----

export type TeamMeter = { id: string; name: string; capacity: number; planned: number; done: number; committed: number; allocated: number; experiment: number; people: number; usedPercent: number };

export function CapacityMeters({ teams }: { teams: TeamMeter[] }) {
  if (teams.length === 0) return <p className="px-4 py-3 text-[12.5px] text-ink-3">No teams configured.</p>;
  const max = Math.max(1, ...teams.map((t) => Math.max(t.capacity, t.planned)));
  return (
    <div className="flex flex-col gap-3 px-4 py-3">
      {teams.map((t) => {
        const pct = (v: number) => `${(v / max) * 100}%`;
        const over = t.capacity > 0 && t.planned > t.capacity;
        const fill = over ? '#e34948' : SERIES_1;
        return (
          <div key={t.id}>
            <div className="mb-1 flex items-baseline justify-between text-[12.5px]">
              <span className="font-medium text-ink">{t.name}{t.people ? <span className="ml-1.5 font-normal text-ink-3">{t.people} people</span> : null}</span>
              <span className={cn('num', over ? 'text-err' : 'text-ink-2')}>{t.planned}{t.capacity ? ` / ${t.capacity}` : ''} pts{t.capacity ? ` · ${t.usedPercent}%` : ''}</span>
            </div>
            <div className="relative h-[14px]" role="img" aria-label={`${t.name}: ${t.planned} of ${t.capacity} points planned, ${t.done} done`}>
              {t.capacity > 0 ? <div className="absolute inset-y-0 left-0 rounded-[4px] border border-line bg-surface-2" style={{ width: pct(t.capacity) }} /> : null}
              <div className="absolute inset-y-0 left-0 rounded-[4px]" style={{ width: pct(t.planned), background: fill, opacity: 0.35 }} title={`${t.planned} pts planned`} />
              <div className="absolute inset-y-0 left-0 rounded-[4px]" style={{ width: pct(t.done), background: fill }} title={`${t.done} pts done`} />
              {t.capacity > 0 ? <>
                <div className="absolute inset-y-[-2px] w-px bg-ink-2" style={{ left: pct(t.capacity * t.committed) }} title={`committed share ${Math.round(t.committed * 100)}%`} />
                <div className="absolute inset-y-[-2px] w-px border-l border-dashed border-ink-3" style={{ left: pct(t.capacity * (t.committed + t.allocated)) }} title={`allocated share ends at ${Math.round((t.committed + t.allocated) * 100)}%`} />
              </> : null}
            </div>
            <div className="mt-1 flex justify-between text-[11px] text-ink-3">
              {t.capacity > 0 ? <span>committed {Math.round(t.committed * 100)}% · allocated {Math.round(t.allocated * 100)}% · experiment {Math.round(t.experiment * 100)}%</span> : <span>owners not on any team; add them to <span className="mono">config/team.json</span></span>}
              <span>{t.done} done</span>
            </div>
          </div>
        );
      })}
      <Legend items={[{ label: 'Done', color: SERIES_1 }, { label: 'Planned', color: 'rgba(42,120,214,0.35)' }, { label: 'Capacity', color: 'var(--surface-2)', outline: true }, { label: 'Committed | allocated boundaries', color: INK3, dashed: true }]} />
    </div>
  );
}

// ---- horizontal bars: points by theme ----

export type ThemeBar = { key: string; title: string; points: number; pointsDone: number; features: number; featuresDone: number; percent: number };

export function ThemeBars({ themes }: { themes: ThemeBar[] }) {
  if (themes.length === 0) return <p className="px-4 py-3 text-[12.5px] text-ink-3">No themes in the snapshot.</p>;
  const sorted = [...themes].sort((a, b) => b.points - a.points);
  const max = Math.max(1, ...sorted.map((t) => t.points));
  return (
    <div className="flex flex-col gap-2.5 px-4 py-3">
      {sorted.map((t) => (
        <div key={t.key}>
          <div className="mb-0.5 flex items-baseline justify-between gap-3 text-[12.5px]"><span className="truncate text-ink">{itemTitle(t.title)}</span><span className="num shrink-0 text-ink-2">{t.pointsDone}/{t.points} pts · {t.percent}%</span></div>
          <div className="relative h-[10px]" role="img" aria-label={`${itemTitle(t.title)}: ${t.pointsDone} of ${t.points} points done`}>
            <div className="absolute inset-y-0 left-0 rounded-[3px]" style={{ width: `${(t.points / max) * 100}%`, background: SERIES_1, opacity: 0.3 }} title={`${t.points} pts in total`} />
            <div className="absolute inset-y-0 left-0 rounded-[3px]" style={{ width: `${(t.pointsDone / max) * 100}%`, background: SERIES_1 }} title={`${t.pointsDone} pts done`} />
          </div>
          <div className="mt-0.5 text-[11px] text-ink-3">{t.featuresDone} of {t.features} feature{t.features === 1 ? '' : 's'} done</div>
        </div>
      ))}
      <Legend items={[{ label: 'Done', color: SERIES_1 }, { label: 'Remaining', color: 'rgba(42,120,214,0.3)' }]} />
    </div>
  );
}

// ---- helpers ----

export function shortSprint(s: string): string {
  const m = /S(\d+)$/i.exec(s);
  return m ? `S${m[1]}` : s;
}

function niceTicks(max: number, count: number): number[] {
  const raw = max / count;
  const mag = Math.pow(10, Math.floor(Math.log10(Math.max(raw, 1e-9))));
  const step = [1, 2, 5, 10].map((m) => m * mag).find((s) => s >= raw) ?? mag * 10;
  const out: number[] = [];
  for (let v = step; v <= max; v += step) out.push(v);
  return out;
}

