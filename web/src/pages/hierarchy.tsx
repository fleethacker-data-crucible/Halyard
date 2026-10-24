import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { LuExternalLink, LuListTree, LuNetwork } from 'react-icons/lu';
import { navigate } from '../app';
import { HierarchyGraph } from '../components/graph';
import { Tree, flatten } from '../components/tree';
import { Button, Empty, Input, LevelChip, Mono, Page, Pill, Section, SeverityIcon, cn, severityTone } from '../components/ui';
import { humanizeCode, shortDate } from '../lib/format';
import { LEVEL_LABEL, expectedParent } from '../lib/levels';
import { Hierarchy, ItemDetail, type HierarchyNode, type Status } from '../lib/schemas';
import { useResource } from '../lib/useResource';
import { Skeleton } from './overview';

export function HierarchyPage({ params, status }: { params: URLSearchParams; status: Status | undefined }) {
  const label = params.get('label') ?? status?.config.labelFilter ?? '';
  const hierarchy = useResource(status?.snapshot ? `/api/hierarchy${label ? `?label=${encodeURIComponent(label)}` : ''}` : null, Hierarchy);
  const [view, setView] = useState<'tree' | 'graph'>('tree');
  const [query, setQuery] = useState('');
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [expandSignal, setExpandSignal] = useState({ expanded: true, tick: 0 });

  const all = useMemo(() => (hierarchy.data ? flatten([...hierarchy.data.roots, ...hierarchy.data.orphans]) : []), [hierarchy.data]);
  const byKey = useMemo(() => new Map(all.map((n) => [n.key, n])), [all]);

  // Deep link: #/hierarchy?item=31
  const itemParam = params.get('item');
  useEffect(() => {
    if (!itemParam || all.length === 0) return;
    const n = all.find((x) => String(x.number) === itemParam);
    if (n) setSelectedKey(n.key);
  }, [itemParam, all]);

  const selected = selectedKey ? byKey.get(selectedKey) ?? null : null;

  if (status && !status.snapshot) {
    return (
      <Page title="Hierarchy" lede="Theme → Epic → Feature → Story → Task, as declared in cascade-meta.">
        <Section><Empty title="No snapshot yet" action={<Button variant="primary" onClick={() => navigate('connection')}>Connect and synchronize</Button>}>Synchronize a repository to see its hierarchy.</Empty></Section>
      </Page>
    );
  }

  const h = hierarchy.data;
  return (
    <Page
      title="Hierarchy"
      lede={h ? <>{h.total} work items{h.labelFilter ? <>, filtered to <Mono>{h.labelFilter}</Mono></> : null}. {h.orphans.length > 0 ? <span className="text-warn">{h.orphans.length} without a resolved parent.</span> : 'Every parent resolves.'}</> : 'Theme → Epic → Feature → Story → Task, as declared in cascade-meta.'}
      actions={
        <div className="flex items-center gap-2">
          <Input placeholder="Find by title, id or #number" value={query} onChange={(e) => setQuery(e.target.value)} className="w-64" aria-label="Find work item" />
          <div className="flex rounded-ctl border border-line">
            <button type="button" aria-pressed={view === 'tree'} onClick={() => setView('tree')} className={cn('flex h-8 items-center gap-1.5 px-2.5 text-[12.5px]', view === 'tree' ? 'bg-surface-2 text-ink' : 'text-ink-2')}><LuListTree size={14} />Tree</button>
            <button type="button" aria-pressed={view === 'graph'} onClick={() => setView('graph')} className={cn('flex h-8 items-center gap-1.5 border-l border-line px-2.5 text-[12.5px]', view === 'graph' ? 'bg-surface-2 text-ink' : 'text-ink-2')}><LuNetwork size={14} />Graph</button>
          </div>
        </div>
      }
    >
      {hierarchy.error ? <p className="text-err">{hierarchy.error}</p> : null}
      <div className="grid gap-5 xl:grid-cols-[minmax(0,7fr)_minmax(360px,5fr)]">
        <Section
          title={view === 'tree' ? 'Declared chain' : 'Graph'}
          aside={view === 'tree' ? (
            <>
              <Button variant="quiet" size="sm" onClick={() => setExpandSignal({ expanded: true, tick: Date.now() })}>Expand all</Button>
              <Button variant="quiet" size="sm" onClick={() => setExpandSignal({ expanded: false, tick: Date.now() })}>Collapse all</Button>
            </>
          ) : null}
        >
          {!h ? <Skeleton rows={8} /> : view === 'tree' ? (
            <div className="scroll-y max-h-[calc(100vh-260px)]">
              <Tree roots={h.roots} selectedKey={selectedKey} onSelect={(n) => setSelectedKey(n.key)} query={query} expandAllSignal={expandSignal} />
              {h.orphans.length > 0 ? (
                <div className="hairline">
                  <div className="px-4 pt-3 pb-1 text-[12.5px] text-warn">Without a resolved parent</div>
                  <Tree roots={h.orphans} selectedKey={selectedKey} onSelect={(n) => setSelectedKey(n.key)} query={query} expandAllSignal={expandSignal} />
                </div>
              ) : null}
            </div>
          ) : (
            <HierarchyGraph roots={h.roots} orphans={h.orphans} selectedKey={selectedKey} onSelect={(n) => setSelectedKey(n.key)} />
          )}
        </Section>
        <Section title={selected ? 'Work item' : 'Select a work item'}>
          {selected ? <Detail node={selected} byKey={byKey} onSelect={setSelectedKey} /> : <Empty title="Nothing selected">Pick a row in the tree or a box in the graph to read its definition, metadata and findings.</Empty>}
        </Section>
      </div>
    </Page>
  );
}

function Detail({ node, byKey, onSelect }: { node: HierarchyNode; byKey: Map<string, HierarchyNode>; onSelect: (key: string) => void }) {
  const detail = useResource(`/api/items/${node.number}`, ItemDetail);
  const d = detail.data;
  const chain: HierarchyNode[] = [];
  if (d?.parentKey) {
    let cursor: HierarchyNode | undefined = byKey.get(d.parentKey);
    let guard = 0;
    while (cursor && guard++ < 10) {
      chain.unshift(cursor);
      cursor = findParent(byKey, cursor.key);
    }
  }
  const [tab, setTab] = useState<'definition' | 'meta' | 'raw'>('definition');
  useEffect(() => setTab('definition'), [node.key]);
  const expected = expectedParent(node.type);

  return (
    <div className="flex flex-col">
      <div className="flex flex-col gap-2 px-4 py-3">
        {chain.length > 0 ? (
          <nav aria-label="Parent chain" className="flex flex-wrap items-center gap-1 text-[12px] text-ink-3">
            {chain.map((c) => (
              <span key={c.key} className="flex items-center gap-1">
                <button type="button" onClick={() => onSelect(c.key)} className="hover:text-ink">{c.title}</button>
                <span>/</span>
              </span>
            ))}
          </nav>
        ) : expected ? <span className="text-[12px] text-warn">No resolved parent. A {LEVEL_LABEL[node.type]} belongs under a {LEVEL_LABEL[expected]}.</span> : null}
        <div className="flex items-start justify-between gap-3">
          <div>
            <div className="flex items-center gap-2"><LevelChip type={node.type} /><Mono className="text-[11.5px]">#{node.number}</Mono>{node.state === 'closed' ? <Pill tone="neutral">closed</Pill> : null}</div>
            <h2 className="mt-1.5 text-[15px]">{node.title}</h2>
            <Mono className="text-[11.5px]">{node.canonicalId}</Mono>
          </div>
          {node.url ? <a href={node.url} target="_blank" rel="noreferrer" className="inline-flex shrink-0 items-center gap-1 text-[12px]">Open <LuExternalLink size={12} /></a> : null}
        </div>
        <div className="flex flex-wrap gap-x-5 gap-y-1 text-[12.5px] text-ink-2">
          {node.points ? <span>Points <span className="num font-medium text-ink">{node.points}</span></span> : null}
          {node.effort ? <span>Effort <span className="font-medium text-ink">{node.effort}</span></span> : null}
          {node.sprint ? <span>Sprint <span className="font-medium text-ink">{node.sprint}</span></span> : null}
          {node.descendantCount > 0 ? <span>{node.descendantCount} descendants{node.rolledUpPoints > 0 ? `, ${node.rolledUpPoints} points rolled up` : ''}</span> : null}
          {d?.updatedAt ? <span>Updated {shortDate(d.updatedAt)}</span> : null}
        </div>
      </div>
      {d && (d.findings.length > 0 || d.deficiencies.length > 0 || d.missingMeta.length > 0) ? (
        <ul className="hairline flex flex-col gap-1.5 px-4 py-3 text-[12.5px]">
          {d.findings.map((f) => <li key={f.code} className="flex items-start gap-2"><SeverityIcon severity={f.severity} /><span><Pill tone={severityTone(f.severity)} className="mr-1.5">{humanizeCode(f.code)}</Pill>{f.explanation}</span></li>)}
          {d.deficiencies.map((x) => <li key={x.code} className="flex items-start gap-2 text-ink-2"><span className="mt-[5px] inline-block h-1.5 w-1.5 shrink-0 rounded-full bg-warn" aria-hidden /><span><span className="font-medium text-ink">{humanizeCode(x.code)}.</span> {x.rule?.explanation}</span></li>)}
          {d.missingMeta.length > 0 ? <li className="text-ink-2">Required meta missing: <Mono>{d.missingMeta.join(', ')}</Mono></li> : null}
        </ul>
      ) : d ? <p className="hairline px-4 py-2 text-[12.5px] text-ok">Complete for its level: metadata, parent and sections all present.</p> : null}
      <div className="hairline flex gap-1 px-3 pt-1">
        {(['definition', 'meta', 'raw'] as const).map((t) => (
          <button key={t} type="button" onClick={() => setTab(t)} className={cn('-mb-px border-b-2 px-2.5 py-1.5 text-[12.5px]', tab === t ? 'border-accent text-ink' : 'border-transparent text-ink-2')}>{t === 'definition' ? 'Definition' : t === 'meta' ? 'cascade-meta' : 'Raw body'}</button>
        ))}
      </div>
      <div className="hairline scroll-y max-h-[calc(100vh-520px)] min-h-[160px] px-4 py-3">
        {detail.error ? <p className="text-err">{detail.error}</p> : !d ? <Skeleton rows={6} /> : tab === 'meta' ? (
          d.meta && d.meta.length > 0 ? (
            <table className="w-full text-[12.5px]">
              <tbody>{d.meta.map((m) => <tr key={m.key}><td className="mono w-36 py-0.5 pr-3 text-ink-3">{m.key}</td><td className="py-0.5">{m.value || <span className="text-ink-3">(empty)</span>}</td></tr>)}</tbody>
            </table>
          ) : <p className="text-ink-3">This item has no cascade-meta block.</p>
        ) : tab === 'raw' ? (
          <pre className="whitespace-pre-wrap break-words text-[12px] leading-relaxed text-ink-2">{d.body || '(empty body)'}</pre>
        ) : (
          <Markdownish text={d.narrative || '(no narrative beyond the metadata)'} />
        )}
      </div>
      {d && d.children.length > 0 ? (
        <div className="hairline px-4 py-3 text-[12.5px]">
          <div className="mb-1 text-ink-2">Children</div>
          <ul className="flex flex-col gap-0.5">
            {d.children.map((k) => {
              const c = byKey.get(k);
              return <li key={k}><button type="button" onClick={() => onSelect(k)} className="flex items-center gap-2 hover:text-accent">{c ? <><LevelChip type={c.type} compact />{c.title}</> : <Mono>{k}</Mono>}</button></li>;
            })}
          </ul>
        </div>
      ) : null}
    </div>
  );
}

function findParent(byKey: Map<string, HierarchyNode>, childKey: string): HierarchyNode | undefined {
  for (const n of byKey.values()) if (n.children.some((c) => c.key === childKey)) return n;
  return undefined;
}

/** Minimal, safe rendering of the narrative: headings, bullets, checkboxes and paragraphs. No HTML passthrough. */
export function Markdownish({ text }: { text: string }) {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  const blocks: ReactNode[] = [];
  let list: ReactNode[] = [];
  const flush = () => { if (list.length) { blocks.push(<ul key={blocks.length} className="my-1 flex flex-col gap-0.5 pl-4">{list}</ul>); list = []; } };
  lines.forEach((raw, i) => {
    const line = raw.trimEnd();
    const h = /^(#{1,4})\s+(.*)$/.exec(line);
    const li = /^\s*[-*]\s+(?:\[([ xX])\]\s+)?(.*)$/.exec(line);
    if (h) { flush(); blocks.push(<h3 key={i} className={cn('mt-3 mb-1 first:mt-0', h[1]!.length <= 2 ? 'text-[13.5px]' : 'text-[12.5px] text-ink-2')}>{inline(h[2]!)}</h3>); return; }
    if (li) { list.push(<li key={i} className="flex items-start gap-2">{li[1] !== undefined ? <input type="checkbox" readOnly checked={li[1] !== ' '} className="mt-[3px]" aria-label="checklist item" /> : <span className="mt-[7px] inline-block h-1 w-1 shrink-0 rounded-full bg-ink-3" aria-hidden />}<span>{inline(li[2]!)}</span></li>); return; }
    flush();
    if (line.startsWith('>')) { blocks.push(<p key={i} className="my-1 border-l-2 border-line pl-2 text-ink-3">{inline(line.replace(/^>\s?/, ''))}</p>); return; }
    if (line.trim().length === 0) return;
    blocks.push(<p key={i} className="my-1 text-ink-2">{inline(line)}</p>);
  });
  flush();
  return <div className="text-[12.5px] leading-relaxed">{blocks}</div>;
}

function inline(text: string): ReactNode {
  const parts = text.split(/(\*\*[^*]+\*\*|`[^`]+`)/g);
  return parts.map((p, i) => {
    if (p.startsWith('**') && p.endsWith('**')) return <strong key={i} className="font-semibold text-ink">{p.slice(2, -2)}</strong>;
    if (p.startsWith('`') && p.endsWith('`')) return <code key={i} className="rounded bg-surface-2 px-1">{p.slice(1, -1)}</code>;
    return p;
  });
}
