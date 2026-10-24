import { useEffect, useState } from 'react';
import { LuExternalLink, LuRefreshCw, LuSprout, LuTrash2 } from 'react-icons/lu';
import { CascadeBars } from '../components/charts';
import { Button, Checkbox, ConfirmDialog, Empty, Field, Input, LevelChip, Mono, Notice, Page, Pill, Progress, Section, useToast } from '../components/ui';
import { relativeTime, shortDate } from '../lib/format';
import { useJob } from '../lib/jobs';
import { CatalogPreview, SeedManifest, type Status } from '../lib/schemas';
import { invalidate, useResource } from '../lib/useResource';
import { z } from 'zod';
import { Skeleton } from './overview';

export function SandboxPage({ status, refreshStatus }: { status: Status | undefined; refreshStatus: () => Promise<void> }) {
  const sandbox = status?.config.sandboxRepository ?? '';
  const runs = useResource('/api/sandbox/runs', z.array(SeedManifest), { pollMs: 10000 });
  return (
    <Page
      title="Sandbox"
      lede={sandbox ? <>Synthetic work items in <Mono>{status?.config.provider}:{sandbox}</Mono>. Seed a full Theme → Task hierarchy to prove the workflow, then delete it by run. Only items labelled <Mono>cascade-synthetic</Mono> can be removed.</> : 'No sandbox repository is configured. Set one in Settings before seeding.'}
    >
      {status && status.policy.mode === 'production' ? <Notice tone="warn">Production write mode is on in the environment. Seeding still targets only the sandbox allow-list.</Notice> : null}
      <div className="grid gap-5 xl:grid-cols-2">
        <Seeder status={status} onDone={() => { invalidate('/api/sandbox/runs'); void refreshStatus(); }} />
        <Runs runs={runs.data} error={runs.error} status={status} onChanged={() => { invalidate('/api/sandbox/runs'); void refreshStatus(); }} />
      </div>
    </Page>
  );
}

function Seeder({ status, onDone }: { status: Status | undefined; onDone: () => void }) {
  const [total, setTotal] = useState(100);
  const [seed, setSeed] = useState(20261024);
  const [defects, setDefects] = useState(false);
  const [nativeLinks, setNativeLinks] = useState(true);
  const [runId, setRunId] = useState('');
  const toast = useToast();
  const preview = useResource(`/api/sandbox/preview?total=${total}&seed=${seed}&defects=${defects}`, CatalogPreview);
  const job = useJob((j) => {
    if (j.state === 'succeeded') {
      const m = j.result as { items?: unknown[]; errors?: string[] } | null;
      toast.push(m?.errors?.length ? 'err' : 'ok', `Seeded ${m?.items?.length ?? 0} items${m?.errors?.length ? ` with ${m.errors.length} errors` : ''}.`);
    } else if (j.state === 'failed') toast.push('err', j.error ?? 'Seeding failed');
    onDone();
  });
  const canWrite = !!status?.config.sandboxRepository && !!status?.config.hasProviderToken;

  return (
    <Section title="Seed a synthetic hierarchy">
      <div className="grid grid-cols-2 gap-3 px-4 py-3">
        <Field label="Total work items" hint="Split across themes, epics, features, stories and tasks in the playbook's proportions."><Input type="number" min={5} max={2000} value={total} onChange={(e) => setTotal(Math.max(5, Math.min(2000, Number(e.target.value) || 5)))} /></Field>
        <Field label="Seed" hint="Same seed, same catalogue, every time."><Input type="number" value={seed} onChange={(e) => setSeed(Number(e.target.value) || 0)} /></Field>
        <Field label="Run id" hint="Optional. Defaults to a timestamp; it becomes the cascade-run:: label."><Input value={runId} onChange={(e) => setRunId(e.target.value)} placeholder="e.g. demo-1" /></Field>
        <div className="flex flex-col justify-end gap-1.5 pb-5">
          <Checkbox label="Include a few definition gaps for the audit to find" checked={defects} onChange={(e) => setDefects(e.target.checked)} />
          <Checkbox label={`Native parent links${status?.provider.capabilities && !status.provider.capabilities.nativeParentLinks ? ' (not supported here)' : ''}`} checked={nativeLinks} onChange={(e) => setNativeLinks(e.target.checked)} />
        </div>
      </div>
      <div className="hairline">
        {preview.data ? <CascadeBars counts={Object.fromEntries(Object.entries(preview.data.counts).map(([k, v]) => [k.toUpperCase(), v]))} /> : <Skeleton rows={5} />}
        {preview.data && preview.data.defects.length > 0 ? <p className="px-4 pb-2 text-[12.5px] text-ink-2">Deliberate gaps: {preview.data.defects.map((d) => d.id).join(', ')}</p> : null}
      </div>
      <div className="hairline flex items-center gap-3 px-4 py-3">
        <Button variant="primary" busy={job.running} disabled={!canWrite} onClick={() => void job.start('/api/sandbox/seed', { total, seed, includeDefects: defects, nativeLinks, runId: runId.trim() || undefined })}><LuSprout size={14} />Seed {total} items into the sandbox</Button>
        {!canWrite ? <span className="text-[12px] text-ink-3">{!status?.config.sandboxRepository ? 'Configure a sandbox repository first.' : 'No provider token is loaded.'}</span> : <span className="text-[12px] text-ink-3">About {Math.ceil((total * (status?.provider.capabilities?.recommendedWriteDelayMs ?? 900)) / 1000 / 60)} min on {status?.config.provider === 'github' ? 'GitHub' : 'GitLab'}, paced to stay under rate limits.</span>}
      </div>
      {job.job ? (
        <div className="hairline px-4 py-3">
          <Progress done={job.job.progressDone} total={job.job.progressTotal} label={job.job.message ?? job.job.state} />
          {job.running ? <Button size="sm" variant="quiet" className="mt-2" onClick={() => void job.cancel()}>Cancel</Button> : null}
          {job.job.error ? <p className="mt-2 text-err">{job.job.error}</p> : null}
          {job.job.state === 'succeeded' ? <SeedResult result={job.job.result} /> : null}
        </div>
      ) : null}
      {job.error ? <Notice tone="err" className="m-4">{job.error}</Notice> : null}
    </Section>
  );
}

function SeedResult({ result }: { result: unknown }) {
  const parsed = SeedManifest.safeParse(result);
  if (!parsed.success) return null;
  const m = parsed.data;
  return (
    <div className="mt-2 text-[12.5px] text-ink-2">
      Run <Mono>{m.runId}</Mono>: {m.items.length} of {m.requested} created{m.items.some((i) => i.nativeLinked) ? `, ${m.items.filter((i) => i.nativeLinked).length} natively linked` : ''}.
      {m.errors.length > 0 ? <ul className="mt-1 text-err">{m.errors.slice(0, 5).map((e, i) => <li key={i}>{e}</li>)}</ul> : null}
    </div>
  );
}

function Runs({ runs, error, status, onChanged }: { runs: SeedManifest[] | undefined; error: string | null; status: Status | undefined; onChanged: () => void }) {
  const toast = useToast();
  const [confirm, setConfirm] = useState<{ scope: 'run' | 'allSynthetic' | 'everything'; runId?: string } | null>(null);
  const [phrase, setPhrase] = useState('');
  const teardown = useJob((j) => {
    if (j.state === 'succeeded') {
      const r = j.result as { deleted?: number; skipped?: number; matched?: number; dryRun?: boolean; errors?: string[] } | null;
      toast.push(r?.errors?.length ? 'err' : 'ok', r?.dryRun ? `Dry run: ${r.matched} items would be deleted.` : `Deleted ${r?.deleted ?? 0} of ${r?.matched ?? 0}${r?.skipped ? `, skipped ${r.skipped}` : ''}.`);
    } else if (j.state === 'failed') toast.push('err', j.error ?? 'Teardown failed');
    onChanged();
  });
  useEffect(() => setPhrase(''), [confirm]);
  const sandboxId = status?.config.sandboxRepository ?? '';
  const wipePhrase = `DELETE ${sandboxId}`;

  const start = (dryRun: boolean) => {
    if (!confirm) return;
    void teardown.start('/api/sandbox/teardown', { scope: confirm.scope, runId: confirm.runId, confirmText: confirm.scope === 'everything' ? phrase : undefined, dryRun });
    setConfirm(null);
  };

  return (
    <Section
      title="Seed runs"
      aside={
        <>
          <Button size="sm" variant="quiet" onClick={onChanged}><LuRefreshCw size={13} />Refresh</Button>
          <Button size="sm" variant="danger" onClick={() => setConfirm({ scope: 'allSynthetic' })} disabled={!sandboxId}>Delete all synthetic</Button>
        </>
      }
    >
      {error ? <Notice tone="err" className="m-4">{error}</Notice> : null}
      {!runs ? <Skeleton rows={4} /> : runs.length === 0 ? <Empty title="No seed runs recorded on this machine">Manifests are written to <Mono>output/seeds/</Mono> when a seed finishes. Runs made elsewhere can still be removed with "Delete all synthetic".</Empty> : (
        <table className="table-dense w-full">
          <thead><tr><th>Run</th><th className="w-28">Created</th><th className="w-32">Target</th><th className="w-24">Status</th><th className="w-40" /></tr></thead>
          <tbody>
            {runs.map((m) => (
              <tr key={m.runId}>
                <td><Mono>{m.runId}</Mono><div className="text-[12px] text-ink-3">{m.items.length} of {m.requested} items{m.items[0]?.url ? <> · <a href={m.items[0].url} target="_blank" rel="noreferrer" className="inline-flex items-center gap-0.5">first item <LuExternalLink size={11} /></a></> : null}</div></td>
                <td className="text-ink-2" title={shortDate(m.startedAt)}>{relativeTime(m.startedAt)}</td>
                <td><Mono className="text-[11.5px]">{m.target}</Mono></td>
                <td>{m.completed ? <Pill tone="ok">complete</Pill> : m.errors.length > 0 ? <Pill tone="err">{m.errors.length} errors</Pill> : <Pill tone="warn">partial</Pill>}</td>
                <td className="text-right"><Button size="sm" variant="danger" onClick={() => setConfirm({ scope: 'run', runId: m.runId })} disabled={teardown.running}><LuTrash2 size={13} />Delete run</Button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {teardown.job ? <div className="hairline px-4 py-3"><Progress done={teardown.job.progressDone} total={teardown.job.progressTotal} label={teardown.job.message ?? teardown.job.state} />{teardown.job.error ? <p className="mt-2 text-err">{teardown.job.error}</p> : null}</div> : null}
      {teardown.error ? <Notice tone="err" className="m-4">{teardown.error}</Notice> : null}
      <div className="hairline px-4 py-3 text-[12.5px] text-ink-3">
        Need the sandbox completely empty, including items Cascade did not create? <button type="button" className="underline hover:text-err" onClick={() => setConfirm({ scope: 'everything' })} disabled={!sandboxId}>Wipe everything in {sandboxId || 'the sandbox'}</button>. It asks you to type a phrase.
      </div>
      <ConfirmDialog
        open={confirm !== null}
        title={confirm?.scope === 'run' ? `Delete run ${confirm.runId}?` : confirm?.scope === 'allSynthetic' ? 'Delete every synthetic item?' : `Wipe ${sandboxId}?`}
        confirmLabel="Delete permanently"
        danger
        onCancel={() => setConfirm(null)}
        onConfirm={() => start(false)}
        confirmDisabled={confirm?.scope === 'everything' && phrase !== wipePhrase}
      >
        {confirm?.scope === 'run' ? <p>Every item in <Mono>{sandboxId}</Mono> carrying both <Mono>cascade-synthetic</Mono> and <Mono>cascade-run::{confirm.runId}</Mono> is deleted. Items without the synthetic label are skipped and listed.</p> : null}
        {confirm?.scope === 'allSynthetic' ? <p>Every item in <Mono>{sandboxId}</Mono> carrying <Mono>cascade-synthetic</Mono> is deleted, whichever run created it. Nothing else is touched.</p> : null}
        {confirm?.scope === 'everything' ? (
          <div className="flex flex-col gap-2">
            <p>This deletes <strong className="text-err">every issue</strong> in <Mono>{sandboxId}</Mono>, synthetic or not. There is no undo on GitHub or GitLab.</p>
            <Field label={`Type ${wipePhrase} to continue`}><Input value={phrase} onChange={(e) => setPhrase(e.target.value)} autoFocus /></Field>
          </div>
        ) : null}
        <div className="mt-3"><Button size="sm" variant="quiet" onClick={() => start(true)} disabled={confirm?.scope === 'everything' && phrase !== wipePhrase}>Dry run first (list only)</Button></div>
      </ConfirmDialog>
    </Section>
  );
}
