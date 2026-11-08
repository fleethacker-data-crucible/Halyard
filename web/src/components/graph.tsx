import { hierarchy, tree, type HierarchyPointNode } from 'd3-hierarchy';
import { select } from 'd3-selection';
import { zoom, zoomIdentity, type D3ZoomEvent, type ZoomBehavior } from 'd3-zoom';
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { LuFocus, LuMaximize, LuMinus, LuPlus } from 'react-icons/lu';
import { LEVELS, LEVEL_LABEL, LEVEL_PLURAL, levelDepth, levelVar } from '../lib/levels';
import type { HierarchyNode } from '../lib/schemas';
import { itemTitle } from '../lib/format';
import { cn } from './ui';

type GNode = HierarchyNode & { synthetic?: boolean };

// Geometry: one column per level (Theme ... Task, then anything else), one row per visible leaf.
const CARD_W = 216;
const CARD_H = 38;
const COL_W = 268;
const ROW_H = 46;
const HEADER = 44;
const COLUMNS = [...LEVELS, 'OTHER'] as const;

function column(type: string): number {
  return Math.min(levelDepth(type), COLUMNS.length - 1);
}

/**
 * The hierarchy as a left-to-right chart: each level has its own column with a heading, so a Theme always
 * sits in the Theme column and a Story in the Story column, whatever its tree depth. Cards are tinted in the
 * level's hue, branches fold (the +n badge), and the selected item's chain to its Theme is drawn solid.
 * Pan by dragging, zoom with the wheel or the buttons; Fit shows everything.
 */
export function HierarchyGraph({ roots, orphans, selectedKey, onSelect, showTo = 4, height = 600 }: { roots: HierarchyNode[]; orphans: HierarchyNode[]; selectedKey: string | null; onSelect: (n: HierarchyNode) => void; showTo?: number; height?: number }) {
  const svgRef = useRef<SVGSVGElement>(null);
  const gRef = useRef<SVGGElement>(null);
  const headRef = useRef<SVGGElement>(null);
  const zoomRef = useRef<ZoomBehavior<SVGSVGElement, unknown> | null>(null);

  // Folded branches. Reset whenever the "Show to" depth changes.
  const [folded, setFolded] = useState<Set<string>>(new Set());
  // Bumped with each "Show to" fold, so the fit below runs on the layout that already has the folds.
  const [fitTick, setFitTick] = useState(0);
  useEffect(() => {
    const next = new Set<string>();
    const walk = (n: HierarchyNode) => { if (n.children.length > 0 && levelDepth(n.type) >= showTo) next.add(n.key); n.children.forEach(walk); };
    [...roots, ...orphans].forEach(walk);
    setFolded(next);
    setFitTick((t) => t + 1);
  }, [roots, orphans, showTo]);
  const toggle = (key: string) => setFolded((f) => { const n = new Set(f); if (n.has(key)) n.delete(key); else n.add(key); return n; });

  const layout = useMemo(() => {
    const virtualRoot: GNode = { key: '__root', number: 0, repository: '', canonicalId: 'root', type: 'UNCLASSIFIED', title: 'Portfolio', fullTitle: 'Portfolio', url: null, state: 'open', points: null, effort: null, sprint: null, rolledUpPoints: 0, descendantCount: 0, children: [...roots, ...orphans], synthetic: true };
    const root = hierarchy<GNode>(virtualRoot, (d) => (d.synthetic || !folded.has(d.key) ? (d.children as GNode[]) : []));
    const laidOut: HierarchyPointNode<GNode> = tree<GNode>().nodeSize([ROW_H, COL_W]).separation((a, b) => (a.parent === b.parent ? 1 : 1.25))(root);
    const nodes = laidOut.descendants().filter((d) => !d.data.synthetic);
    let minX = Infinity, maxX = -Infinity;
    for (const d of nodes) { minX = Math.min(minX, d.x); maxX = Math.max(maxX, d.x); }
    if (!isFinite(minX)) { minX = 0; maxX = 0; }
    const used = new Set(nodes.map((d) => column(d.data.type)));
    const lastCol = Math.max(0, ...used);
    return { root: laidOut, nodes, minX, maxX, used, lastCol, width: (lastCol + 1) * COL_W, height: maxX - minX + CARD_H + HEADER + 24 };
  }, [roots, orphans, folded]);

  // Position: column by level, row by tree order (x from d3 is the vertical axis here).
  const px = (d: HierarchyPointNode<GNode>) => 16 + column(d.data.type) * COL_W;
  const py = (d: HierarchyPointNode<GNode>) => HEADER + (d.x - layout.minX);

  // The selected item's chain up to its root, drawn solid.
  const chain = useMemo(() => {
    const s = new Set<string>();
    const hit = layout.nodes.find((d) => d.data.key === selectedKey);
    for (let c: HierarchyPointNode<GNode> | null = hit ?? null; c && !c.data.synthetic; c = c.parent) s.add(c.data.key);
    return s;
  }, [layout, selectedKey]);

  useEffect(() => {
    const svg = svgRef.current;
    const g = gRef.current;
    if (!svg || !g) return;
    const z = zoom<SVGSVGElement, unknown>().scaleExtent([0.15, 2.5]).on('zoom', (event: D3ZoomEvent<SVGSVGElement, unknown>) => {
      g.setAttribute('transform', event.transform.toString());
      // Column headings follow sideways pans and zoom but stay pinned to the top edge.
      headRef.current?.setAttribute('transform', `translate(${event.transform.x},0) scale(${event.transform.k})`);
    });
    zoomRef.current = z;
    const sel = select(svg);
    sel.call(z).on('dblclick.zoom', null);
    sel.call(z.transform, zoomIdentity.translate(8, 4));
    return () => { sel.on('.zoom', null); };
    // Re-arm only when the geometry's scale changes, not on every fold.
  }, []);

  // Moves apply at once (no d3-transition in the bundle), which also suits reduced motion.
  const animate = (fn: (s: ReturnType<typeof select<SVGSVGElement, unknown>>) => void) => {
    const svg = svgRef.current;
    if (!svg || !zoomRef.current) return;
    fn(select(svg));
  };
  const zoomBy = (k: number) => animate((s) => zoomRef.current!.scaleBy(s, k));
  // minK keeps the opening view readable: a tall graph then opens at its top, to be panned.
  const fit = (minK = 0.15) => {
    const svg = svgRef.current;
    if (!svg) return;
    const w = svg.clientWidth || 900;
    const k = Math.max(minK, Math.min(1, (w - 24) / (layout.width + 16), (height - 16) / layout.height));
    animate((s) => zoomRef.current!.transform(s, zoomIdentity.translate(8, 4).scale(k)));
  };
  const centerOn = (key: string | null) => {
    const svg = svgRef.current;
    const d = key ? layout.nodes.find((n) => n.data.key === key) : undefined;
    if (!svg || !d) return;
    const w = svg.clientWidth || 900;
    const k = 1;
    animate((s) => zoomRef.current!.transform(s, zoomIdentity.translate(w / 2 - (px(d) + CARD_W / 2) * k, height / 2 - (py(d) + CARD_H / 2) * k).scale(k)));
  };
  // Open showing everything that is unfolded: on first draw and whenever "Show to" changes.
  useEffect(() => { if (fitTick > 0) fit(0.55); }, [fitTick]); // eslint-disable-line react-hooks/exhaustive-deps

  // Bring a selection made elsewhere (tree, deep link, children list) into view.
  useEffect(() => { if (selectedKey) centerOn(selectedKey); }, [selectedKey]); // eslint-disable-line react-hooks/exhaustive-deps

  const links = layout.root.links().filter((l) => !l.source.data.synthetic);
  const top = -12;
  const bandH = layout.height + 12;

  const onCardKey = (e: KeyboardEvent<SVGGElement>, d: HierarchyPointNode<GNode>) => {
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSelect(d.data); }
    else if ((e.key === 'ArrowRight' && folded.has(d.data.key)) || (e.key === 'ArrowLeft' && !folded.has(d.data.key) && d.data.children.length > 0)) { e.preventDefault(); toggle(d.data.key); }
  };

  return (
    <div className="relative">
      <div className="absolute right-3 top-3 z-10 flex overflow-hidden rounded-ctl border border-line bg-surface shadow-sm">
        <GraphButton label="Zoom in" onClick={() => zoomBy(1.25)}><LuPlus size={14} /></GraphButton>
        <GraphButton label="Zoom out" onClick={() => zoomBy(0.8)}><LuMinus size={14} /></GraphButton>
        <GraphButton label="Fit everything" onClick={() => fit()}><LuMaximize size={14} /></GraphButton>
        <GraphButton label="Center on the selected item" onClick={() => centerOn(selectedKey)} disabled={!selectedKey}><LuFocus size={14} /></GraphButton>
      </div>
      <svg ref={svgRef} width="100%" height={height} className="block cursor-grab rounded-b-panel bg-paper active:cursor-grabbing" aria-label="Hierarchy graph" role="group">
        <g ref={gRef}>
          {/* Level columns: a faint band in the level's hue (the headings are drawn pinned, below). */}
          {COLUMNS.map((c, i) => (layout.used.has(i) ? (
            <rect key={c} aria-hidden x={16 + i * COL_W - 14} y={top} width={CARD_W + 28} height={bandH} rx={10} fill={c === 'OTHER' ? 'var(--surface-2)' : `color-mix(in srgb, ${levelVar(c)} 6%, transparent)`} />
          ) : null))}
          {links.map((l) => {
            const sx = px(l.source) + CARD_W, sy = py(l.source) + CARD_H / 2, tx = px(l.target), ty = py(l.target) + CARD_H / 2;
            const mx = sx + Math.max(16, (tx - sx) / 2);
            const on = chain.has(l.target.data.key) && chain.has(l.source.data.key);
            return <path key={l.target.data.key} d={`M${sx},${sy} C${mx},${sy} ${mx},${ty} ${tx},${ty}`} fill="none" stroke={levelVar(l.target.data.type)} strokeOpacity={on ? 1 : 0.4} strokeWidth={on ? 2.5 : 1.4} />;
          })}
          {layout.nodes.map((d) => {
            const n = d.data;
            const selected = n.key === selectedKey;
            const isFolded = folded.has(n.key) && n.children.length > 0;
            const shown = itemTitle(n.title);
            const label = shown.length > 30 ? shown.slice(0, 29) + '…' : shown;
            const meta = n.type === 'STORY' && n.points ? `${n.points} pts` : n.type === 'FEATURE' && n.effort ? n.effort : n.rolledUpPoints > 0 ? `${n.rolledUpPoints} pts` : '';
            return (
              <g key={n.key} transform={`translate(${px(d)},${py(d)})`} className="cursor-pointer outline-none [&:focus-visible>rect:first-child]:stroke-[var(--focus)]"
                style={{ ['--level' as string]: levelVar(n.type) }}
                tabIndex={0} role="button" aria-label={`${LEVEL_LABEL[n.type as keyof typeof LEVEL_LABEL] ?? n.type} #${n.number}: ${shown}${isFolded ? `, ${n.descendantCount} folded` : ''}`} aria-pressed={selected}
                onClick={() => onSelect(n)} onKeyDown={(e) => onCardKey(e, d)}>
                <rect width={CARD_W} height={CARD_H} rx={6} fill="color-mix(in srgb, var(--level) 11%, var(--surface))" stroke={selected ? 'var(--focus)' : 'color-mix(in srgb, var(--level) 45%, var(--line))'} strokeWidth={selected ? 2.5 : 1} />
                <rect width={5} height={CARD_H} rx={2} fill="var(--level)" />
                <text x={13} y={14} fontSize={9.5} fontWeight={700} letterSpacing="0.06em" fill="color-mix(in srgb, var(--level) 80%, var(--ink))" style={{ fontFamily: 'var(--font-sans)', textTransform: 'uppercase' }}>
                  {LEVEL_LABEL[n.type as keyof typeof LEVEL_LABEL] ?? n.type} · #{n.number}{n.state === 'closed' ? ' · closed' : ''}
                </text>
                <text x={13} y={29} fontSize={12} fontWeight={n.type === 'THEME' || n.type === 'EPIC' ? 600 : 500} fill="var(--ink)" style={{ fontFamily: 'var(--font-sans)' }}>{label}</text>
                {meta ? <text x={CARD_W - (n.children.length > 0 ? 34 : 8)} y={14} textAnchor="end" fontSize={10} fill="var(--ink-2)" style={{ fontFamily: 'var(--font-sans)' }}>{meta}</text> : null}
                {n.children.length > 0 ? (
                  <g transform={`translate(${CARD_W - 14},${CARD_H / 2})`} onClick={(e) => { e.stopPropagation(); toggle(n.key); }} className="cursor-pointer" role="button" aria-label={isFolded ? `Unfold ${n.descendantCount} items` : 'Fold'}>
                    <circle r={isFolded ? 11 : 8} fill={isFolded ? 'var(--level)' : 'var(--surface)'} stroke="var(--level)" strokeWidth={1.2} />
                    <text textAnchor="middle" y={isFolded ? 3.5 : 4} fontSize={isFolded ? 9.5 : 12} fontWeight={700} fill={isFolded ? 'var(--surface)' : 'var(--level)'} style={{ fontFamily: 'var(--font-sans)' }}>{isFolded ? `+${n.descendantCount > 99 ? '99' : n.descendantCount}` : '−'}</text>
                  </g>
                ) : null}
                <title>{n.fullTitle} (#{n.number}){n.points ? ` · ${n.points} pts` : ''}{n.children.length > 0 ? ` · ${n.descendantCount} below` : ''}</title>
              </g>
            );
          })}
        </g>
        <g ref={headRef} aria-hidden>
          {COLUMNS.map((c, i) => (layout.used.has(i) ? (
            <g key={c} transform={`translate(${16 + i * COL_W - 14},0)`}>
              <rect width={CARD_W + 28} height={34} rx={8} fill="color-mix(in srgb, var(--paper) 88%, transparent)" />
              <rect x={14} y={10} width={4} height={14} rx={1.5} fill={c === 'OTHER' ? 'var(--lv-none)' : levelVar(c)} />
              <text x={24} y={22} fontSize={12} fontWeight={600} letterSpacing="0.06em" fill="var(--ink)" style={{ fontFamily: 'var(--font-sans)', textTransform: 'uppercase' }}>{c === 'OTHER' ? 'Other' : LEVEL_PLURAL[c]}</text>
            </g>
          ) : null))}
        </g>
      </svg>
      <p className="pointer-events-none absolute bottom-2 right-3 rounded bg-surface/80 px-1.5 text-[11px] text-ink-3">Drag to pan · scroll to zoom · click a badge to fold or unfold</p>
    </div>
  );
}

function GraphButton({ label, onClick, disabled, children }: { label: string; onClick: () => void; disabled?: boolean; children: ReactNode }) {
  return (
    <button type="button" aria-label={label} title={label} onClick={onClick} disabled={disabled}
      className={cn('flex h-8 w-8 items-center justify-center border-l border-line text-ink-2 first:border-l-0 hover:bg-surface-2 hover:text-ink disabled:opacity-40')}>
      {children}
    </button>
  );
}
