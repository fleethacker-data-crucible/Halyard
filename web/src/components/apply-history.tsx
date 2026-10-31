import { useState } from 'react';
import { LuUndo2 } from 'react-icons/lu';
import { Button, Checkbox, ConfirmDialog, Mono, Notice, Pill, Section, useToast } from './ui';
import { describeError, post } from '../lib/api';
import { relativeTime } from '../lib/format';
import { useJob } from '../lib/jobs';
import { ApplyRunSummary, RollbackPreview } from '../lib/schemas';
import { invalidate, useResource } from '../lib/useResource';
import { z } from 'zod';

/**
 * What each apply wrote, and an undo for it. The undo shows what would be restored and what has been edited since (and would be
 * left alone) before anything is written; items a person edited after Halyard wrote them are only overwritten with the box ticked.
 */
export function ApplyHistory({ onDone }: { onDone: () => void }) {
  const runs = useResource('/api/applies', z.array(ApplyRunSummary), { pollMs: 10000 });
  const toast = useToast();
  const [target, setTarget] = useState<ApplyRunSummary | null>(null);
  const [preview, setPreview] = useState<RollbackPreview | null>(null);
  const [force, setForce] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const job = useJob((j) => {
    if (j.state === 'succeeded') {
      const r = j.result as { restored?: number; conflicts?: number; missing?: number; failed?: number } | null;
      toast.push(r?.failed ? 'err' : 'ok', `Restored ${r?.restored ?? 0}, left ${r?.conflicts ?? 0} edited item(s) alone, ${r?.missing ?? 0} missing, ${r?.failed ?? 0} failed.`);
    } else if (j.state === 'failed') toast.push('err', j.error ?? 'Undo failed');
    invalidate('/api/applies');
    invalidate('/api/proposals');
    onDone();
  });

  const check = async (run: ApplyRunSummary, overwrite: boolean) => {
    setPreview(null);
    setError(null);
    try {
      setPreview(RollbackPreview.parse(await post(`/api/applies/${encodeURIComponent(run.id)}/rollback`, { dryRun: true, force: overwrite })));
    } catch (e) {
      setError(describeError(e));
    }
  };
  const open = (run: ApplyRunSummary) => {
    setTarget(run);
    setForce(false);
    void check(run, false);
  };
  const close = () => { setTarget(null); setPreview(null); };
  const confirm = () => {
    if (!target) return;
    void job.start(`/api/applies/${encodeURIComponent(target.id)}/rollback`, { force });
    close();
  };

  const list = (runs.data ?? []).slice(0, 8);
  if (list.length === 0 && !job.running) return null;
  const latest = list.find((r) => r.canRollBack);
  const p = preview?.preview;
  const nothing = !!p && p.restored === 0;

  return (
    <Section title="Applied changes" aside={job.running ? <span className="text-[11.5px] text-ink-3">{job.job?.message}</span> : <span className="text-[11.5px] text-ink-3">Undo restores the earlier text</span>}>
      <ul>
        {list.map((r) => (
          <li key={r.id} className="flex items-center justify-between gap-3 px-4 py-2.5 hairline first:border-t-0">
            <span className="flex min-w-0 flex-col gap-0.5">
              <span className="flex items-center gap-2"><span className="font-medium">{r.entries} written</span><span className="text-[12px] text-ink-3">{relativeTime(r.startedAt)}</span>{r.id === latest?.id ? <Pill tone="info">latest</Pill> : null}</span>
              <span className="text-[12px] text-ink-2">{r.canRollBack ? `${r.pending} can be undone` : 'undone'}{r.rolledBack > 0 && r.canRollBack ? `, ${r.rolledBack} already undone` : ''} · {r.batchIds.length} {r.batchIds.length === 1 ? 'batch' : 'batches'}</span>
            </span>
            <Button size="sm" variant="quiet" disabled={!r.canRollBack || job.running} onClick={() => open(r)} title="Preview, then restore the text these changes replaced"><LuUndo2 size={13} />Undo</Button>
          </li>
        ))}
      </ul>
      <ConfirmDialog open={target !== null} title="Undo this apply?" confirmLabel={nothing ? 'Nothing to restore' : `Restore ${p?.restored ?? 0} ${p?.restored === 1 ? 'item' : 'items'}`} danger onCancel={close} onConfirm={confirm} confirmDisabled={!p || nothing}>
        {error ? <Notice tone="err">{error}</Notice> : !p ? 'Checking the sandbox…' : (
          <div className="flex flex-col gap-2">
            <p>Writes the earlier text back to {p.restored} {p.restored === 1 ? 'item' : 'items'} in the sandbox. The proposals return to Pending so they can be reviewed again.</p>
            {p.alreadyRestored > 0 ? <p className="text-[12.5px]">{p.alreadyRestored} already have the earlier text.</p> : null}
            {p.missing > 0 ? <Notice tone="warn">{p.missing} {p.missing === 1 ? 'item no longer exists' : 'items no longer exist'}.</Notice> : null}
            {p.conflicts > 0 ? (
              <Notice tone="warn">
                <div className="flex flex-col gap-1">
                  <span>{p.conflicts} {p.conflicts === 1 ? 'item was' : 'items were'} edited after Halyard wrote {p.conflicts === 1 ? 'it' : 'them'} and will be left alone:</span>
                  <ul className="list-disc pl-5">{p.rows.filter((x) => x.status === 'conflict').slice(0, 6).map((x) => <li key={x.proposalId}><Mono>#{x.itemNumber}</Mono> {x.itemTitle}</li>)}</ul>
                </div>
              </Notice>
            ) : null}
            {p.conflicts > 0 || force ? <Checkbox label="Overwrite items edited since with the earlier text" checked={force} onChange={(e) => { setForce(e.target.checked); if (target) void check(target, e.target.checked); }} /> : null}
          </div>
        )}
      </ConfirmDialog>
    </Section>
  );
}
