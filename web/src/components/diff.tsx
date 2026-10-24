import { useMemo, useState } from 'react';
import { diffLines, diffStats, foldUnchanged } from '../lib/diff';
import { cn } from './ui';

/**
 * Before/after view of an issue body. Unified by default (what reviewers scan fastest), with a toggle to
 * the full proposed text. Colour is used only on changed lines; unchanged context is quiet.
 */
export function DiffView({ before, after, className, maxHeight = 420 }: { before: string; after: string; className?: string; maxHeight?: number }) {
  const [mode, setMode] = useState<'diff' | 'after' | 'before'>('diff');
  const [full, setFull] = useState(false);
  const ops = useMemo(() => diffLines(before, after), [before, after]);
  const stats = diffStats(ops);
  const rows = useMemo(() => (full ? ops : foldUnchanged(ops, 2)), [ops, full]);

  return (
    <div className={cn('flex flex-col rounded-ctl border border-line-2', className)}>
      <div className="flex flex-wrap items-center gap-3 border-b border-line-2 bg-surface-2 px-3 py-1.5 text-[12px]">
        <span className="num"><span className="text-ok">+{stats.added}</span> <span className="text-err">−{stats.removed}</span> <span className="text-ink-3">· {stats.unchanged} unchanged</span></span>
        <div className="ml-auto flex items-center gap-1">
          {(['diff', 'after', 'before'] as const).map((m) => (
            <button key={m} type="button" onClick={() => setMode(m)} className={cn('rounded-ctl px-2 py-0.5', mode === m ? 'bg-surface text-ink' : 'text-ink-2 hover:text-ink')}>{m === 'diff' ? 'Changes' : m === 'after' ? 'Proposed' : 'Current'}</button>
          ))}
          {mode === 'diff' ? <label className="ml-2 inline-flex items-center gap-1 text-ink-2"><input type="checkbox" checked={full} onChange={(e) => setFull(e.target.checked)} />all lines</label> : null}
        </div>
      </div>
      <div className="scroll-y mono text-[12px] leading-[1.45]" style={{ maxHeight }}>
        {mode === 'diff' ? (
          stats.added + stats.removed === 0 ? <p className="px-3 py-3 text-ink-3">No change to the body.</p> : (
            <table className="w-full border-collapse">
              <tbody>
                {rows.map((r, i) => r.kind === 'fold' ? (
                  <tr key={i}><td colSpan={2} className="bg-surface-2 px-3 py-0.5 text-center text-[11px] text-ink-3">… {r.count} unchanged lines …</td></tr>
                ) : (
                  <tr key={i} className={r.kind === 'add' ? 'bg-ok-bg' : r.kind === 'del' ? 'bg-err-bg' : ''}>
                    <td className={cn('w-6 select-none px-2 text-right', r.kind === 'add' ? 'text-ok' : r.kind === 'del' ? 'text-err' : 'text-ink-3')}>{r.kind === 'add' ? '+' : r.kind === 'del' ? '−' : ' '}</td>
                    <td className={cn('whitespace-pre-wrap break-words pr-3', r.kind === 'same' ? 'text-ink-2' : 'text-ink', r.kind === 'del' && 'line-through decoration-err/50')}>{r.text || ' '}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        ) : (
          <pre className="whitespace-pre-wrap break-words px-3 py-2 text-ink-2">{(mode === 'after' ? after : before) || '(empty)'}</pre>
        )}
      </div>
    </div>
  );
}
