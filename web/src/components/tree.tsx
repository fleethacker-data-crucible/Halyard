import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { LuChevronRight } from 'react-icons/lu';
import { levelVar } from '../lib/levels';
import type { HierarchyNode } from '../lib/schemas';
import { readLocal, writeLocal } from '../lib/storage';
import { cn, LevelChip } from './ui';
import { itemTitle } from '../lib/format';

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

type Row = { node: HierarchyNode; depth: number; parent: HierarchyNode | null };

/**
 * Keyboard-navigable tree (roving tabindex): ↑/↓ move, →/← expand/collapse or jump to parent, Home/End,
 * Enter/Space select, type-ahead by first letters. Collapsed state is remembered per repository.
 */
export function Tree({ roots, selectedKey, onSelect, query, expandAllSignal, storageKey, showRepository }: { roots: HierarchyNode[]; selectedKey: string | null; onSelect: (n: HierarchyNode) => void; query: string; expandAllSignal?: { expanded: boolean; tick: number }; storageKey?: string; showRepository?: boolean }) {
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>(() => (storageKey ? readLocal<Record<string, boolean>>('tree.' + storageKey, {}) : {}));
  const [lastSignal, setLastSignal] = useState(0);
  const [focusKey, setFocusKey] = useState<string | null>(null);
  const listRef = useRef<HTMLUListElement>(null);
  const typeahead = useRef<{ text: string; at: number }>({ text: '', at: 0 });
  const all = useMemo(() => flatten(roots), [roots]);

  useEffect(() => { if (storageKey) writeLocal('tree.' + storageKey, collapsed); }, [collapsed, storageKey]);

  if (expandAllSignal && expandAllSignal.tick !== lastSignal) {
    setLastSignal(expandAllSignal.tick);
    const next: Record<string, boolean> = {};
    if (!expandAllSignal.expanded) for (const n of all) if (n.children.length > 0) next[n.key] = true;
    setCollapsed(next);
  }
  const toggle = useCallback((key: string) => setCollapsed((c) => ({ ...c, [key]: !c[key] })), []);

  // Visible rows in display order, for keyboard movement.
  const rows = useMemo(() => {
    const out: Row[] = [];
    const walk = (n: HierarchyNode, depth: number, parent: HierarchyNode | null) => {
      if (!subtreeMatches(n, query)) return;
      out.push({ node: n, depth, parent });
      const isCollapsed = !!collapsed[n.key] && !query;
      if (!isCollapsed) n.children.forEach((c) => walk(c, depth + 1, n));
    };
    roots.forEach((r) => walk(r, 0, null));
    return out;
  }, [roots, collapsed, query]);

  const active = focusKey ?? selectedKey ?? rows[0]?.node.key ?? null;

  useEffect(() => {
    if (!focusKey) return;
    const el = listRef.current?.querySelector<HTMLElement>(`[data-key="${CSS.escape(focusKey)}"]`);
    el?.focus();
    el?.scrollIntoView({ block: 'nearest' });
  }, [focusKey, rows]);

  const onKeyDown = (e: KeyboardEvent<HTMLUListElement>) => {
    if (rows.length === 0) return;
    const idx = Math.max(0, rows.findIndex((r) => r.node.key === active));
    const row = rows[idx]!;
    const go = (i: number) => { const t = rows[Math.min(rows.length - 1, Math.max(0, i))]; if (t) setFocusKey(t.node.key); };
    switch (e.key) {
      case 'ArrowDown': e.preventDefault(); go(idx + 1); break;
      case 'ArrowUp': e.preventDefault(); go(idx - 1); break;
      case 'Home': e.preventDefault(); go(0); break;
      case 'End': e.preventDefault(); go(rows.length - 1); break;
      case 'ArrowRight':
        e.preventDefault();
        if (row.node.children.length > 0 && collapsed[row.node.key]) toggle(row.node.key);
        else if (row.node.children.length > 0) go(idx + 1);
        break;
      case 'ArrowLeft':
        e.preventDefault();
        if (row.node.children.length > 0 && !collapsed[row.node.key] && !query) toggle(row.node.key);
        else if (row.parent) setFocusKey(row.parent.key);
        break;
      case 'Enter': case ' ': e.preventDefault(); onSelect(row.node); break;
      case '*': e.preventDefault(); setCollapsed({}); break;
      default: {
        if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
          const now = Date.now();
          const text = (now - typeahead.current.at < 700 ? typeahead.current.text : '') + e.key.toLowerCase();
          typeahead.current = { text, at: now };
          const start = text.length === 1 ? idx + 1 : idx;
          const order = [...rows.slice(start), ...rows.slice(0, start)];
          const hit = order.find((r) => r.node.title.toLowerCase().startsWith(text));
          if (hit) setFocusKey(hit.node.key);
        }
      }
    }
  };

  if (roots.length === 0) return <p className="px-4 py-6 text-ink-3">No work items in this view.</p>;
  return (
    <ul role="tree" ref={listRef} className="py-1 outline-none" onKeyDown={onKeyDown} aria-label="Work item hierarchy">
      {rows.map((r) => {
        const n = r.node;
        const isCollapsed = !!collapsed[n.key] && !query;
        const hasChildren = n.children.length > 0;
        const selected = n.key === selectedKey;
        return (
          <li key={n.key} role="treeitem" aria-level={r.depth + 1} aria-expanded={hasChildren ? !isCollapsed : undefined} aria-selected={selected}>
            <div
              data-key={n.key}
              tabIndex={n.key === active ? 0 : -1}
              onFocus={() => setFocusKey(n.key)}
              onClick={() => onSelect(n)}
              className={cn('level-rail flex cursor-pointer items-center gap-1.5 py-[3px] pr-3 outline-none hover:bg-surface-2 focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-focus', selected && 'bg-surface-2')}
              style={{ ['--level' as string]: levelVar(n.type), paddingLeft: 8 + r.depth * 16 }}
            >
              <button
                type="button"
                tabIndex={-1}
                aria-label={hasChildren ? (isCollapsed ? 'Expand' : 'Collapse') : undefined}
                onClick={(e) => { e.stopPropagation(); if (hasChildren) toggle(n.key); }}
                className={cn('flex h-5 w-5 shrink-0 items-center justify-center rounded text-ink-3 hover:text-ink', !hasChildren && 'invisible')}
              >
                <LuChevronRight size={14} className={cn('transition-transform', !isCollapsed && 'rotate-90')} />
              </button>
              <span className="flex min-w-0 flex-1 items-center gap-2 text-left">
                <LevelChip type={n.type} compact />
                <span className={cn('truncate', matches(n, query) && query ? 'font-medium' : '')}>{itemTitle(n.title)}</span>
                {n.state === 'closed' ? <span className="text-[11px] text-ink-3">closed</span> : null}
                <span className="mono ml-auto shrink-0 text-[11.5px] text-ink-3">{showRepository ? n.repository.split('/').pop() + ' ' : ''}#{n.number}</span>
                {n.type === 'STORY' && n.points ? <span className="num w-6 shrink-0 text-right text-[11.5px] text-ink-2" title="Story points">{n.points}</span>
                  : n.type === 'FEATURE' && n.effort ? <span className="w-6 shrink-0 text-right text-[11px] text-ink-2" title={`Effort ${n.effort}`}>{n.effort.slice(0, 1)}</span>
                  : n.descendantCount > 0 && n.rolledUpPoints > 0 ? <span className="num w-6 shrink-0 text-right text-[11.5px] text-ink-3" title="Rolled-up story points">{n.rolledUpPoints}</span>
                  : <span className="w-6 shrink-0" />}
              </span>
            </div>
          </li>
        );
      })}
    </ul>
  );
}
