import { useState } from 'react';
import { LuArrowUpFromLine, LuListChecks, LuMegaphone, LuRefreshCw } from 'react-icons/lu';
import { Button, Checkbox, Empty, Field, Input, Mono, Notice, Pill, Section, useToast } from './ui';
import { describeError, post } from '../lib/api';
import { useJob } from '../lib/jobs';
import { invalidate, useResource } from '../lib/useResource';
import { z } from 'zod';

const PromoteStatus = z.object({ enabled: z.boolean(), mode: z.string(), allowProduction: z.boolean(), target: z.string().nullable().optional(), reason: z.string().nullable().optional() });
const PromotePlan = z.object({
  source: z.string(), target: z.string(), creates: z.number().optional(), updates: z.number().optional(), unchanged: z.number().optional(), confirmPhrase: z.string().optional(),
  rows: z.array(z.object({ sourceKey: z.string(), canonicalId: z.string(), title: z.string(), action: z.string(), targetNumber: z.number().nullable().optional() })),
  warnings: z.array(z.string()),
});

/**
 * Production promote. Off unless the environment enables production mode; then a preview of what would be created or updated, and a
 * run that needs the exact confirmation phrase typed. Never a default action.
 */
export function PromotePanel() {
  const status = useResource('/api/promote/status', PromoteStatus);
  const toast = useToast();
  const [plan, setPlan] = useState<z.infer<typeof PromotePlan> | null>(null);
  const [busy, setBusy] = useState(false);
  const [phrase, setPhrase] = useState('');
  const job = useJob((j) => {
    if (j.state === 'succeeded') {
      const r = j.result as { created?: number; updated?: number; failed?: number } | null;
      toast.push(r?.failed ? 'err' : 'ok', `Promoted: ${r?.created ?? 0} created, ${r?.updated ?? 0} updated, ${r?.failed ?? 0} failed.`);
    } else if (j.state === 'failed') toast.push('err', j.error ?? 'Promote failed');
    setPlan(null);
    setPhrase('');
    invalidate('/api/snapshot');
  });
  const s = status.data;
  if (!s) return null;

  const preview = async () => {
    setBusy(true);
    try { setPlan(PromotePlan.parse(await post('/api/promote/preview'))); setPhrase(''); }
    catch (e) { toast.push('err', describeError(e)); }
    finally { setBusy(false); }
  };
  const todo = plan ? plan.rows.filter((r) => r.action !== 'unchanged').length : 0;
  const expected = plan ? (plan.confirmPhrase ?? `PROMOTE ${todo} ITEMS TO ${plan.target}`) : '';

  return (
    <Section title="Promote to production" aside={<Pill tone={s.enabled ? 'warn' : 'neutral'}>{s.enabled ? 'production mode' : 'off'}</Pill>}>
      <div className="flex flex-col gap-3 px-4 py-3">
        {!s.enabled ? <Notice tone="info">{s.reason}</Notice> : (
          <>
            <p className="text-ink-2">Copies reviewed sandbox items into <Mono>{s.target}</Mono>, matched by their halyard-meta id. Synthetic items are never promoted. Nothing is written until you type the confirmation phrase.</p>
            <div><Button onClick={() => void preview()} busy={busy}><LuArrowUpFromLine size={14} />Preview promote</Button></div>
          </>
        )}
        {plan ? (
          <>
            <p className="text-[12.5px] text-ink-2">{plan.creates ?? plan.rows.filter((r) => r.action === 'create').length} to create · {plan.updates ?? plan.rows.filter((r) => r.action === 'update').length} to update · {plan.rows.filter((r) => r.action === 'unchanged').length} unchanged</p>
            <ul className="scroll-y max-h-[30vh] text-[12.5px]">
              {plan.rows.map((r) => <li key={r.sourceKey} className="flex gap-2 py-0.5"><Pill tone={r.action === 'create' ? 'ok' : r.action === 'update' ? 'warn' : 'neutral'}>{r.action}</Pill><Mono>{r.canonicalId}</Mono><span className="truncate">{r.title}</span></li>)}
            </ul>
            {plan.warnings.length ? <Notice tone="warn">{plan.warnings.slice(0, 5).map((w, i) => <div key={i}>{w}</div>)}</Notice> : null}
            {todo > 0 ? (
              <Field label="Confirmation phrase" hint={<>Type <Mono>{expected}</Mono> to confirm</>}>
                <div className="flex gap-2">
                  <Input value={phrase} onChange={(e) => setPhrase(e.target.value)} autoComplete="off" spellCheck={false} />
                  <Button variant="danger" disabled={phrase !== expected || job.running} busy={job.running} onClick={() => void job.start('/api/promote/run', { confirmText: phrase })}>Promote {todo}</Button>
                </div>
              </Field>
            ) : <Empty title="Nothing to promote">Production already matches the sandbox for these items.</Empty>}
          </>
        ) : null}
        {job.error ? <Notice tone="err">{job.error}</Notice> : null}
      </div>
    </Section>
  );
}

/** Posts the leadership summary to the webhook configured in the environment; the message is shown first and the address never is. */
export function WebhookPanel() {
  const toast = useToast();
  const [text, setText] = useState<string | null>(null);
  const [configured, setConfigured] = useState(false);
  const [busy, setBusy] = useState(false);
  const check = async () => {
    setBusy(true);
    try {
      const r = await post<{ text: string; configured: boolean }>('/api/leadership/notify', { dryRun: true });
      setText(r.text);
      setConfigured(r.configured);
    } catch (e) { toast.push('err', describeError(e)); }
    finally { setBusy(false); }
  };
  const send = async () => {
    setBusy(true);
    try { await post('/api/leadership/notify', {}); toast.push('ok', 'Summary sent to the configured webhook.'); setText(null); }
    catch (e) { toast.push('err', describeError(e)); }
    finally { setBusy(false); }
  };
  return (
    <Section title="Share to Teams or Slack" aside={<Button size="sm" variant="quiet" onClick={() => void check()} busy={busy && text === null}><LuMegaphone size={13} />Preview message</Button>}>
      <div className="flex flex-col gap-2 px-4 py-3">
        {text === null ? <p className="text-ink-2">Posts a short summary (features on track, velocity, ready backlog, capacity, oldest blockers) to the incoming webhook set as <Mono>HALYARD_REPORT_WEBHOOK</Mono>. Preview it first.</p> : (
          <>
            <pre className="whitespace-pre-wrap rounded-ctl bg-surface-2 p-3 text-[12.5px]">{text}</pre>
            {configured ? <div><Button variant="primary" onClick={() => void send()} busy={busy}>Send to the webhook</Button></div> : <Notice tone="info">No webhook is configured. Set HALYARD_REPORT_WEBHOOK in config/halyard.env.</Notice>}
          </>
        )}
      </div>
    </Section>
  );
}

/** Re-reads the Shellback export or endpoint set in the environment. */
export function ShellbackRefresh({ onDone }: { onDone: () => void }) {
  const toast = useToast();
  const [busy, setBusy] = useState(false);
  const refresh = async () => {
    setBusy(true);
    try {
      const r = await post<{ result: { written: number; unchanged: number; removed: number; skippedNoConsent: number } }>('/api/team/shellback/refresh');
      toast.push('ok', `Shellback: ${r.result.written} updated, ${r.result.unchanged} unchanged, ${r.result.removed} removed, ${r.result.skippedNoConsent} without consent.`);
      invalidate('/api/team');
      onDone();
    } catch (e) { toast.push('err', describeError(e)); }
    finally { setBusy(false); }
  };
  return <Button size="sm" variant="quiet" onClick={() => void refresh()} busy={busy} title="Re-read the Shellback export or endpoint (HALYARD_SHELLBACK_EXPORT / HALYARD_SHELLBACK_URL)"><LuRefreshCw size={13} />Refresh</Button>;
}

const GoalRun = z.object({ id: z.string(), next: z.string(), steps: z.array(z.object({ id: z.string(), title: z.string(), status: z.string(), batchId: z.string().nullable().optional(), note: z.string() })) });

/** "Get this backlog ready": the ordered deterministic steps, stopping at the first one with proposals to approve. */
export function GoalPanel({ onBatch }: { onBatch: () => void }) {
  const toast = useToast();
  const [run, setRun] = useState<z.infer<typeof GoalRun> | null>(null);
  const [busy, setBusy] = useState(false);
  const [useModel, setUseModel] = useState(false);
  const go = async () => {
    setBusy(true);
    try { setRun(GoalRun.parse(await post('/api/goals/ready', { useModel }))); invalidate('/api/proposals'); onBatch(); }
    catch (e) { toast.push('err', describeError(e)); }
    finally { setBusy(false); }
  };
  const tone = (s: string) => (s === 'proposed' ? 'info' : s === 'clean' || s === 'done' ? 'ok' : s === 'failed' ? 'err' : 'neutral') as 'info' | 'ok' | 'neutral' | 'err';
  return (
    <Section title="Get the backlog ready" aside={<Button size="sm" variant="quiet" onClick={() => void go()} busy={busy}><LuListChecks size={13} />Run</Button>}>
      <div className="flex flex-col gap-2 px-4 py-3">
        <Checkbox label="Use the model to draft sections and size stories" checked={useModel} onChange={(e) => setUseModel(e.target.checked)} />
        {!run ? <p className="text-ink-2">Repairs metadata, links parents, drafts and sizes (with the model, when ticked), tags skills and plans sprints in that order, and stops at the first step with proposals for you to approve. Nothing is applied.</p> : (
          <>
            <ul className="text-[12.5px]">{run.steps.map((s) => <li key={s.id} className="flex items-baseline gap-2 py-0.5"><Pill tone={tone(s.status)}>{s.status}</Pill><span className="font-medium">{s.title}</span><span className="truncate text-ink-3">{s.id === 'report' ? '' : s.note}</span></li>)}</ul>
            <Notice tone="info">{run.next}</Notice>
          </>
        )}
      </div>
    </Section>
  );
}
