import { hierarchy, tree, type HierarchyPointNode } from 'd3-hierarchy';
import { select } from 'd3-selection';
import { zoom, zoomIdentity, type D3ZoomEvent } from 'd3-zoom';
import { useEffect, useMemo, useRef } from 'react';
import { levelVar } from '../lib/levels';
import type { HierarchyNode } from '../lib/schemas';
import { itemTitle } from '../lib/format';

type GNode = HierarchyNode & { synthetic?: boolean };

/**
 * Left-to-right tidy tree of the hierarchy, drawn as SVG and panned/zoomed with d3-zoom.
 * One colour per level, no gradients; the chain itself is the picture.
 */
export function HierarchyGraph({ roots, orphans, selectedKey, onSelect, height = 520 }: { roots: HierarchyNode[]; orphans: HierarchyNode[]; selectedKey: string | null; onSelect: (n: HierarchyNode) => void; height?: number }) {
  const svgRef = useRef<SVGSVGElement>(null);
  const gRef = useRef<SVGGElement>(null);

  const layout = useMemo(() => {
    const virtualRoot: GNode = { key: '__root', number: 0, repository: '', canonicalId: 'root', type: 'UNCLASSIFIED', title: 'Portfolio', fullTitle: 'Portfolio', url: null, state: 'open', points: null, effort: null, sprint: null, rolledUpPoints: 0, descendantCount: 0, children: [...roots, ...orphans], synthetic: true };
    const root = hierarchy<GNode>(virtualRoot, (d) => d.children as GNode[]);
    const leaves = Math.max(1, root.leaves().length);
    const rowHeight = 22;
    const depth = Math.max(1, root.height);
    const width = Math.max(900, depth * 260);
    const h = Math.max(height, leaves * rowHeight);
    // tree() returns the same nodes with x/y assigned, typed as HierarchyPointNode.
    const laidOut: HierarchyPointNode<GNode> = tree<GNode>().size([h - 20, width - 320])(root);
    return { root: laidOut, width, height: h };
  }, [roots, orphans, height]);

  useEffect(() => {
    const svg = svgRef.current;
    const g = gRef.current;
    if (!svg || !g) return;
    const z = zoom<SVGSVGElement, unknown>().scaleExtent([0.25, 3]).on('zoom', (event: D3ZoomEvent<SVGSVGElement, unknown>) => {
      g.setAttribute('transform', event.transform.toString());
    });
    const sel = select(svg);
    sel.call(z);
    sel.call(z.transform, zoomIdentity.translate(40, 10).scale(Math.min(1, height / layout.height)));
    return () => { sel.on('.zoom', null); };
  }, [layout, height]);

  const nodes = layout.root.descendants().filter((d) => !d.data.synthetic);
  const links = layout.root.links().filter((l) => !l.source.data.synthetic);

  return (
    <div className="relative">
      <svg ref={svgRef} width="100%" height={height} className="block cursor-grab rounded-b-panel bg-surface active:cursor-grabbing" role="img" aria-label="Hierarchy graph">
        <g ref={gRef}>
          {links.map((l) => {
            const sx = l.source.y, sy = l.source.x, tx = l.target.y, ty = l.target.x;
            const mx = (sx + tx) / 2;
            return <path key={l.target.data.key} d={`M${sx + 110},${sy} C${mx},${sy} ${mx},${ty} ${tx},${ty}`} fill="none" stroke="var(--line)" strokeWidth={1.2} />;
          })}
          {nodes.map((d) => {
            const selected = d.data.key === selectedKey;
            const shown = itemTitle(d.data.title);
            const label = shown.length > 26 ? shown.slice(0, 25) + '…' : shown;
            return (
              <g key={d.data.key} transform={`translate(${d.y},${d.x})`} onClick={() => onSelect(d.data)} className="cursor-pointer">
                <rect x={0} y={-10} width={110} height={20} rx={4} fill="var(--surface)" stroke={selected ? 'var(--focus)' : 'var(--line)'} strokeWidth={selected ? 2 : 1} />
                <rect x={0} y={-10} width={4} height={20} rx={1.5} fill={levelVar(d.data.type)} />
                <text x={9} y={4} fontSize={10.5} fill="var(--ink)" style={{ fontFamily: 'var(--font-sans)' }}>{label}</text>
                <title>{d.data.fullTitle} (#{d.data.number}){d.data.points ? ` · ${d.data.points} pts` : ''}</title>
              </g>
            );
          })}
        </g>
      </svg>
      <p className="pointer-events-none absolute bottom-2 right-3 text-[11px] text-ink-3">Drag to pan, scroll to zoom</p>
    </div>
  );
}
