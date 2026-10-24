import { useMemo, useState } from 'react';
import { LuChevronRight } from 'react-icons/lu';
import { levelVar } from '../lib/levels';
import type { HierarchyNode } from '../lib/schemas';
import { cn, LevelChip } from './ui';

export function flatten(nodes: HierarchyNode[]): HierarchyNode[] {
  const out: HierarchyNode[] = [];
  const walk = (n: HierarchyNode) => { out.push(n); n.children.forEach(walk); };
  nodes.forEach(walk);
  return out;
}

function matches(node: HierarchyNode, q: string): boolean {
  if (!q) return true;
  const s = q.toLowerCase();
  return node.title.toLowerCase().includes(s) || node.canonicalId.toLowerCase().includes(s) || String(node.number) === s;
}

function subtreeMatches(node: HierarchyNode, q: string): boolean {
  return matches(node, q) || node.children.some((c) => subtreeMatches(c, q));
}

export function Tree({ roots, selectedKey, onSelect, query, expandAllSignal }: { roots: HierarchyNode[]; selectedKey: string | null; onSelect: (n: HierarchyNode) => void; query: string; expandAllSignal?: { expanded: boolean; tick: number } }) {
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});
  const [lastSignal, setLastSignal] = useState(0);
  const all = useMemo(() => flatten(roots), [roots]);
  if (expandAllSignal && expandAllSignal.tick !== lastSignal) {
    setLastSignal(expandAllSignal.tick);
    const next: Record<string, boolean> = {};
    if (!expandAllSignal.expanded) for (const n of all) if (n.children.length > 0) next[n.key] = true;
    setCollapsed(next);
  }
  const toggle = (key: string) => setCollapsed((c) => ({ ...c, [key]: !c[key] }));
  if (roots.length === 0) return <p className="px-4 py-6 text-ink-3">No work items in this view.</p>;
  return (
    <ul role="tree" className="py-1">
      {roots.filter((r) => subtreeMatches(r, query)).map((r) => (
        <TreeRow key={r.key} node={r} depth={0} collapsed={collapsed} toggle={toggle} selectedKey={selectedKey} onSelect={onSelect} query={query} />
      ))}
    </ul>
  );
}

function TreeRow({ node, depth, collapsed, toggle, selectedKey, onSelect, query }: { node: HierarchyNode; depth: number; collapsed: Record<string, boolean>; toggle: (k: string) => void; selectedKey: string | null; onSelect: (n: HierarchyNode) => void; query: string }) {
  const isCollapsed = !!collapsed[node.key] && !query;
  const hasChildren = node.children.length > 0;
  const selected = node.key === selectedKey;
  const visibleChildren = node.children.filter((c) => subtreeMatches(c, query));
  return (
    <li role="treeitem" aria-expanded={hasChildren ? !isCollapsed : undefined} aria-selected={selected}>
      <div
        className={cn('level-rail flex items-center gap-1.5 py-[3px] pr-3 hover:bg-surface-2', selected && 'bg-surface-2')}
        style={{ ['--level' as string]: levelVar(node.type), paddingLeft: 8 + depth * 16 }}
      >
        <button
          type="button"
          aria-label={hasChildren ? (isCollapsed ? 'Expand' : 'Collapse') : undefined}
          onClick={() => hasChildren && toggle(node.key)}
          className={cn('flex h-5 w-5 shrink-0 items-center justify-center rounded text-ink-3 hover:text-ink', !hasChildren && 'invisible')}
        >
          <LuChevronRight size={14} className={cn('transition-transform', !isCollapsed && 'rotate-90')} />
        </button>
        <button type="button" onClick={() => onSelect(node)} className="flex min-w-0 flex-1 items-center gap-2 text-left">
          <LevelChip type={node.type} compact />
          <span className={cn('truncate', matches(node, query) && query ? 'font-medium' : '')}>{node.title}</span>
          <span className="mono ml-auto shrink-0 text-[11.5px] text-ink-3">#{node.number}</span>
          {node.type === 'STORY' && node.points ? <span className="num shrink-0 w-6 text-right text-[11.5px] text-ink-2">{node.points}</span> : node.descendantCount > 0 && node.rolledUpPoints > 0 ? <span className="num shrink-0 w-6 text-right text-[11.5px] text-ink-3" title="Rolled-up story points">{node.rolledUpPoints}</span> : <span className="w-6 shrink-0" />}
        </button>
      </div>
      {hasChildren && !isCollapsed ? (
        <ul role="group">
          {visibleChildren.map((c) => (
            <TreeRow key={c.key} node={c} depth={depth + 1} collapsed={collapsed} toggle={toggle} selectedKey={selectedKey} onSelect={onSelect} query={query} />
          ))}
        </ul>
      ) : null}
    </li>
  );
}
