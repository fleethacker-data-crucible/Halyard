import type { ReactNode } from 'react';
import { arc, pie, type PieArcDatum } from 'd3-shape';

type Slice = { label: string; value: number; color: string };
import { LEVELS, LEVEL_PLURAL, levelVar } from '../lib/levels';
import type { LevelToken } from '../lib/schemas';

/**
 * The signature chart: hierarchy levels as a cascade. Each level sits one step further right and
 * one hue lighter than its parent, and the bar length is the count. Read top to bottom it says
 * "two lanes, six initiatives, eighteen capabilities, fifty sprint slices, twenty-four tasks".
 */
export function CascadeBars({ counts, onSelect }: { counts: Record<string, number>; onSelect?: (level: LevelToken) => void }) {
  const max = Math.max(1, ...LEVELS.map((l) => counts[l] ?? 0));
  const unclassified = counts['UNCLASSIFIED'] ?? 0;
  const bugs = counts['BUG'] ?? 0;
  return (
    <div className="flex flex-col gap-1.5 px-4 py-3">
      {LEVELS.map((level, i) => {
        const n = counts[level] ?? 0;
        const width = Math.max(n > 0 ? 2 : 0, (n / max) * 100);
        return (
          <button
            key={level}
            type="button"
            onClick={onSelect ? () => onSelect(level) : undefined}
            className="group grid grid-cols-[88px_1fr_48px] items-center gap-3 text-left"
            style={{ paddingLeft: i * 14 }}
          >
            <span className="text-ink-2 group-hover:text-ink">{LEVEL_PLURAL[level]}</span>
            <span className="h-[18px] w-full overflow-hidden rounded-[3px] bg-surface-2">
              <span className="cascade-reveal block h-full origin-left rounded-[3px]" style={{ width: `${width}%`, background: levelVar(level), animationDelay: `${i * 60}ms` }} />
            </span>
            <span className="num text-right font-semibold">{n}</span>
          </button>
        );
      })}
      {bugs > 0 || unclassified > 0 ? (
        <div className="mt-1 flex gap-4 text-[12px] text-ink-3">
          {bugs > 0 ? <span>{bugs} bugs (outside the chain)</span> : null}
          {unclassified > 0 ? <span>{unclassified} unclassified</span> : null}
        </div>
      ) : null}
    </div>
  );
}

export function Donut({ slices, size = 120, thickness = 16, centre }: { slices: Slice[]; size?: number; thickness?: number; centre?: ReactNode }) {
  const total = slices.reduce((a, s) => a + s.value, 0);
  const r = size / 2;
  const arcs = pie<Slice>().value((d) => d.value).sort(null)(slices.filter((s) => s.value > 0));
  const path = arc<PieArcDatum<Slice>>().innerRadius(r - thickness).outerRadius(r).padAngle(0.02).cornerRadius(2);
  return (
    <div className="flex items-center gap-4">
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} role="img" aria-label={slices.map((s) => `${s.label} ${s.value}`).join(', ')}>
        <g transform={`translate(${r},${r})`}>
          {total === 0 ? <circle r={r - thickness / 2} fill="none" stroke="var(--line-2)" strokeWidth={thickness} /> : null}
          {arcs.map((a) => <path key={a.data.label} d={path(a) ?? undefined} fill={a.data.color} />)}
        </g>
        <foreignObject x={thickness} y={thickness} width={size - thickness * 2} height={size - thickness * 2}>
          <div className="flex h-full w-full items-center justify-center text-center">{centre}</div>
        </foreignObject>
      </svg>
      <ul className="flex flex-col gap-1 text-[12.5px]">
        {slices.map((s) => (
          <li key={s.label} className="flex items-center gap-2">
            <span className="inline-block h-2.5 w-2.5 rounded-[2px]" style={{ background: s.color }} aria-hidden />
            <span className="text-ink-2">{s.label}</span>
            <span className="num ml-auto pl-3 font-medium">{s.value}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

export function HBars({ rows, color = 'var(--warn)', onSelect }: { rows: Array<{ key: string; label: string; value: number }>; color?: string; onSelect?: (key: string) => void }) {
  const max = Math.max(1, ...rows.map((r) => r.value));
  if (rows.length === 0) return <p className="px-4 py-3 text-ink-3">Nothing to show.</p>;
  return (
    <ul className="flex flex-col gap-1 px-4 py-3">
      {rows.map((r) => (
        <li key={r.key}>
          <button type="button" onClick={onSelect ? () => onSelect(r.key) : undefined} className="grid w-full grid-cols-[minmax(0,1fr)_120px_40px] items-center gap-3 text-left hover:text-ink">
            <span className="truncate text-ink-2" title={r.label}>{r.label}</span>
            <span className="h-2 w-full overflow-hidden rounded-full bg-surface-2"><span className="block h-full rounded-full" style={{ width: `${(r.value / max) * 100}%`, background: color }} /></span>
            <span className="num text-right font-medium">{r.value}</span>
          </button>
        </li>
      ))}
    </ul>
  );
}

export function Coverage({ label, part, whole, color }: { label: string; part: number; whole: number; color: string }) {
  const pctValue = whole > 0 ? Math.round((part / whole) * 100) : 0;
  return (
    <div className="flex flex-col gap-1 px-4 py-3">
      <div className="flex items-baseline justify-between">
        <span className="text-ink-2">{label}</span>
        <span className="num font-semibold">{part}<span className="text-ink-3 font-normal"> / {whole}</span></span>
      </div>
      <div className="h-2 w-full overflow-hidden rounded-full bg-surface-2"><div className="h-full rounded-full" style={{ width: `${pctValue}%`, background: color }} /></div>
      <span className="text-[12px] text-ink-3">{pctValue}% sized</span>
    </div>
  );
}
