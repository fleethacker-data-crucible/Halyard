// The shell's dialogs: the Ctrl+K command palette, the ? help, and the error boundary that keeps one broken page from blanking
// the window. Native <dialog> (as ConfirmDialog), so focus stays inside and Esc closes.
import { Component, useEffect, useMemo, useRef, useState, type ErrorInfo, type ReactNode } from 'react';
import { LuCornerDownLeft, LuLifeBuoy, LuRotateCcw, LuSearch, LuX } from 'react-icons/lu';
import { noteCommand, rankCommands, recentCommands, type Command } from '../lib/commands';
import { SHORTCUTS, keysText } from '../lib/shortcuts';
import { Button, Notice } from './ui';

function useModalDialog(open: boolean, onClose: () => void) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    if (open && !el.open) el.showModal();
    if (!open && el.open) el.close();
  }, [open]);
  return { ref, onClose, onCancel: (e: React.SyntheticEvent) => { e.preventDefault(); onClose(); } };
}

function Keys({ keys }: { keys: string[] }) {
  return (
    <span className="flex shrink-0 gap-1">
      {keys.map((k, i) => <kbd key={i} className="min-w-6 rounded border border-line bg-surface-2 px-1.5 py-0.5 text-center font-mono text-[11px] text-ink">{k}</kbd>)}
    </span>
  );
}

export function CommandPalette({ open, commands, onClose }: { open: boolean; commands: Command[]; onClose: () => void }) {
  const d = useModalDialog(open, onClose);
  const [q, setQ] = useState('');
  const [active, setActive] = useState(0);
  const recent = useMemo(() => (open ? recentCommands() : []), [open]);
  const rows = useMemo(() => rankCommands(commands, q, recent), [commands, q, recent]);
  const at = Math.min(active, Math.max(0, rows.length - 1));
  useEffect(() => { if (open) { setQ(''); setActive(0); } }, [open]);
  useEffect(() => { document.getElementById(`cmd-${at}`)?.scrollIntoView({ block: 'nearest' }); }, [at]);

  const run = (c: Command | undefined) => {
    if (!c) return;
    noteCommand(c.id);
    onClose();
    // After the palette has closed and handed focus back, so a command can move focus or open a dialog.
    window.setTimeout(c.run, 0);
  };
  let lastGroup = '';
  return (
    <dialog ref={d.ref} onCancel={d.onCancel} aria-label="Command palette" onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
      className="mx-auto mt-[12vh] w-[600px] max-w-[calc(100vw-2rem)] rounded-panel border border-line bg-surface p-0 text-ink backdrop:bg-black/40">
      {open ? (
        <div className="flex max-h-[70vh] flex-col">
          <div className="flex items-center gap-2 border-b border-line-2 px-3">
            <LuSearch size={16} aria-hidden className="shrink-0 text-ink-3" />
            <input
              autoFocus
              role="combobox"
              aria-expanded="true"
              aria-controls="palette-list"
              aria-activedescendant={rows[at] ? `cmd-${at}` : undefined}
              aria-autocomplete="list"
              aria-label="Search pages and actions"
              placeholder="Go to a page, run standup, synchronize…"
              value={q}
              onChange={(e) => { setQ(e.target.value); setActive(0); }}
              onKeyDown={(e) => {
                if (e.key === 'ArrowDown') { e.preventDefault(); setActive((a) => Math.min(rows.length - 1, a + 1)); }
                else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((a) => Math.max(0, a - 1)); }
                else if (e.key === 'Enter') { e.preventDefault(); run(rows[at]); }
              }}
              className="min-w-0 flex-1 bg-transparent py-3 text-[15px] outline-none placeholder:text-ink-3"
            />
            <kbd className="rounded border border-line px-1.5 py-0.5 font-mono text-[11px] text-ink-3">Esc</kbd>
          </div>
          <ul id="palette-list" role="listbox" aria-label="Results" className="scroll-y min-h-0 flex-1 p-1.5">
            {rows.length === 0 ? <li className="px-3 py-4 text-[13px] text-ink-3">Nothing matches “{q}”.</li> : null}
            {rows.map((c, i) => {
              const label = !q.trim() && i < recent.length && recent.includes(c.id) ? 'Recent' : c.group;
              const head = label !== lastGroup;
              lastGroup = label;
              return (
                <li key={`${c.id}-${i}`} role="presentation">
                  {head ? <div className="px-2.5 pt-2.5 pb-1 text-[11px] font-semibold tracking-wide text-ink-3 uppercase">{label}</div> : null}
                  <div id={`cmd-${i}`} role="option" aria-selected={i === at} onMouseMove={() => setActive(i)} onClick={() => run(c)}
                    className={`flex cursor-pointer items-center gap-2 rounded-ctl px-2.5 py-2 text-[13.5px] ${i === at ? 'bg-surface-2 text-ink' : 'text-ink-2'}`}>
                    <span className="min-w-0 flex-1 truncate">{c.label}</span>
                    {c.keys ? <span className="font-mono text-[11px] text-ink-3">{keysText(c.keys)}</span> : null}
                    {i === at ? <LuCornerDownLeft size={13} aria-hidden className="text-ink-3" /> : null}
                  </div>
                </li>
              );
            })}
          </ul>
        </div>
      ) : null}
    </dialog>
  );
}

export function HelpDialog({ open, onClose, onPalette, onWelcome, version }: { open: boolean; onClose: () => void; onPalette: () => void; onWelcome: () => void; version: string | undefined }) {
  const d = useModalDialog(open, onClose);
  return (
    <dialog ref={d.ref} onCancel={d.onCancel} aria-labelledby="help-title" onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
      className="mx-auto mt-16 w-[620px] max-w-[calc(100vw-2rem)] rounded-panel border border-line bg-surface p-0 text-ink backdrop:bg-black/40">
      {open ? (
        <div className="flex max-h-[calc(100vh-6rem)] flex-col">
          <header className="flex items-center gap-2 border-b border-line-2 px-4 py-3">
            <LuLifeBuoy size={16} aria-hidden className="text-accent" />
            <h2 id="help-title" className="flex-1 text-[15px]">Help</h2>
            <Button variant="quiet" size="sm" onClick={onClose} aria-label="Close" autoFocus><LuX size={15} /></Button>
          </header>
          <div className="scroll-y min-h-0 flex-1 px-4 py-3">
            {SHORTCUTS.map((g) => (
              <section key={g.group} className="mb-4">
                <h3 className="mb-1.5 text-[12px] font-semibold tracking-wide text-ink-3 uppercase">{g.group}</h3>
                <ul className="flex flex-col">
                  {g.items.map((s) => (
                    <li key={s.what} className="flex items-center justify-between gap-4 border-b border-line-2 py-1.5 text-[13px] last:border-b-0">
                      <span className="text-ink-2">{s.what}</span><Keys keys={s.keys} />
                    </li>
                  ))}
                </ul>
              </section>
            ))}
          </div>
          <footer className="flex flex-wrap items-center gap-2 border-t border-line-2 px-4 py-2.5 text-[12px] text-ink-3">
            <Button size="sm" onClick={() => { onClose(); window.setTimeout(onPalette, 0); }}>Open the command palette</Button>
            <Button size="sm" variant="quiet" onClick={() => { onClose(); onWelcome(); }}>Show the welcome page</Button>
            <span className="ml-auto">Halyard {version ?? ''}</span>
          </footer>
        </div>
      ) : null}
    </dialog>
  );
}

/** Keeps a page that throws from blanking the window: the error, a way back, and the details to report. */
export class ErrorBoundary extends Component<{ children: ReactNode; label: string }, { error: Error | null }> {
  override state: { error: Error | null } = { error: null };

  static getDerivedStateFromError(error: Error) { return { error }; }

  override componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Halyard page error', this.props.label, error, info.componentStack);
  }

  override render() {
    const { error } = this.state;
    if (!error) return this.props.children;
    const details = `${this.props.label}: ${error.name}: ${error.message}`;
    return (
      <div role="alert" className="mx-auto flex max-w-[70ch] flex-col gap-3 px-6 py-10">
        <h1>This page ran into a problem</h1>
        <p className="text-ink-2">The rest of Halyard still works: pick another page in the sidebar, or try this one again. Nothing was written.</p>
        <Notice tone="err"><span className="mono">{details}</span></Notice>
        <div className="flex gap-2">
          <Button variant="primary" onClick={() => this.setState({ error: null })}><LuRotateCcw size={14} />Try again</Button>
          <Button onClick={() => { void navigator.clipboard?.writeText(details); }}>Copy the details</Button>
        </div>
      </div>
    );
  }
}
