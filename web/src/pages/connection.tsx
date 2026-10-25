import { useState } from 'react';
import { LuCheck, LuDatabase, LuPlug } from 'react-icons/lu';
import { navigate } from '../app';
import { Button, Checkbox, Field, Mono, Notice, Page, Pill, Progress, Section, Select, useToast } from '../components/ui';
import { describeError, post } from '../lib/api';
import { relativeTime } from '../lib/format';
import { useJob } from '../lib/jobs';
import { Labels, Repositories, Snapshot, type Status } from '../lib/schemas';
import { invalidate, useResource } from '../lib/useResource';
import { z } from 'zod';

const TestResult = z.object({ connected: z.boolean(), identity: z.object({ login: z.string(), name: z.string().nullable().optional(), url: z.string().nullable().optional() }).optional(), capabilities: z.object({ deleteIssues: z.boolean(), nativeParentLinks: z.boolean() }).optional(), repository: z.string().optional(), code: z.string().optional(), error: z.string().optional() });

export function ConnectionPage({ status, refreshStatus }: { status: Status | undefined; refreshStatus: () => Promise<void> }) {
  const toast = useToast();
  const [testing, setTesting] = useState(false);
  const [test, setTest] = useState<z.infer<typeof TestResult> | null>(null);
  const [which, setWhich] = useState<'source' | 'sandbox' | 'all'>('source');
  const [labelFilter, setLabelFilter] = useState<string>('');
  const [includeClosed, setIncludeClosed] = useState(true);
  const labels = useResource(status?.provider.identity ? `/api/provider/labels?repository=${which === 'all' ? 'source' : which}` : null, Labels);
  const repos = useResource(status?.provider.identity ? '/api/provider/repositories' : null, Repositories);
  const snapshot = useResource(status?.snapshot ? '/api/snapshot' : null, Snapshot);
  const sync = useJob((j) => {
    if (j.state === 'succeeded') {
      toast.push('ok', 'Snapshot saved.');
      invalidate('/api/');
      void refreshStatus();
      navigate('overview');
    } else if (j.state === 'failed') toast.push('err', j.error ?? 'Synchronize failed');
  });

  const runTest = async () => {
    setTesting(true);
    try {
      const r = TestResult.parse(await post('/api/provider/test'));
      setTest(r);
      if (r.connected) toast.push('ok', `Connected as ${r.identity?.login}.`);
      void refreshStatus();
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setTesting(false);
    }
  };

  const providerName = status?.config.provider === 'gitlab' ? 'GitLab' : 'GitHub';
  const sources = status?.config.sourceRepositories ?? [];
  const sourceLabel = sources.length > 1 ? `${sources.length} repositories` : sources[0] ?? status?.config.sandboxRepository ?? '';
  const repo = which === 'sandbox' ? status?.config.sandboxRepository : which === 'all' ? `${sourceLabel} + sandbox` : sourceLabel;

  return (
    <Page title="Connection" lede={<>Test the {providerName} token, then take a snapshot of a repository. Reads only; the engine never writes while synchronizing.</>}>
      <div className="grid gap-5 xl:grid-cols-2">
        <Section title={`${providerName} access`}>
          <div className="flex flex-col gap-3 px-4 py-3">
            <dl className="grid grid-cols-[140px_1fr] gap-y-1 text-[12.5px]">
              <dt className="text-ink-2">API</dt><dd><Mono>{status?.config.baseUrl}</Mono></dd>
              <dt className="text-ink-2">Source</dt><dd><Mono>{status?.config.sourceRepository || '(not set)'}</Mono></dd>
              <dt className="text-ink-2">Sandbox</dt><dd><Mono>{status?.config.sandboxRepository || '(not set)'}</Mono></dd>
              <dt className="text-ink-2">Token</dt><dd>{status?.config.hasProviderToken ? <Pill tone="ok">loaded in memory</Pill> : <Pill tone="warn">missing</Pill>}</dd>
              <dt className="text-ink-2">Write policy</dt><dd className="text-ink-2">{status?.policy.description}</dd>
            </dl>
            {!status?.config.hasProviderToken ? <Notice tone="warn">{status?.config.provider === 'gitlab' ? 'Unlock the Lazarette vault from the launcher, or set GITLAB_TOKEN in config/halyard.env for local development.' : 'Set GITHUB_TOKEN in config/halyard.env, or run "gh auth login" before launching; the launcher reads the gh token.'}</Notice> : null}
            <div className="flex items-center gap-3">
              <Button variant="primary" onClick={runTest} busy={testing} disabled={!status?.config.hasProviderToken}><LuPlug size={14} />Test connection</Button>
              {status?.provider.identity ? <span className="text-ink-2">Last confirmed as <span className="text-ink">{status.provider.identity.login}</span></span> : status?.provider.error ? <span className="text-err">{status.provider.error}</span> : null}
            </div>
            {test && !test.connected ? <Notice tone="err">{test.code}: {test.error}</Notice> : null}
            {test && test.connected ? <Notice tone="ok"><LuCheck size={12} className="mr-1 inline" />Signed in as {test.identity?.login}; repository {test.repository} reachable. {test.capabilities?.nativeParentLinks ? 'Native parent links available.' : 'Hierarchy lives in halyard-meta only.'}</Notice> : null}
          </div>
          {repos.data ? (
            <div className="hairline px-4 py-3 text-[12.5px]">
              <div className="mb-1 text-ink-2">Repositories this token can see ({repos.data.length} most recent)</div>
              <ul className="flex max-h-40 flex-col gap-0.5 overflow-y-auto">
                {repos.data.map((r) => <li key={r.key} className="flex items-center gap-2"><Mono>{r.identifier}</Mono>{r.identifier === status?.config.sandboxRepository ? <Pill tone="sandbox">sandbox</Pill> : null}{r.identifier === status?.config.sourceRepository ? <Pill tone="neutral">source</Pill> : null}</li>)}
              </ul>
            </div>
          ) : null}
        </Section>

        <Section title="Snapshot">
          <div className="flex flex-col gap-3 px-4 py-3">
            {status?.snapshot ? <p className="text-ink-2">Current snapshot: {status.snapshot.count} items from <Mono>{status.snapshot.repository}</Mono>, taken {relativeTime(status.snapshot.takenAt)}.</p> : <p className="text-ink-2">No snapshot yet. Everything in Halyard reads from a snapshot, so take one first.</p>}
            <div className="grid grid-cols-2 gap-3">
              <Field label="Repository to read">
                <Select value={which} onChange={(e) => setWhich(e.target.value as typeof which)}>
                  <option value="source">Source · {sources.length > 1 ? sources.join(', ') : sources[0] || '(not set)'}</option>
                  <option value="sandbox">Sandbox · {status?.config.sandboxRepository || '(not set)'}</option>
                  {sources.length > 0 && status?.config.sandboxRepository && !sources.includes(status.config.sandboxRepository) ? <option value="all">Sources and sandbox together</option> : null}
                </Select>
              </Field>
              <Field label="Only items with label" hint="Optional; fetched server-side. Leave empty to take everything.">
                <Select value={labelFilter} onChange={(e) => setLabelFilter(e.target.value)} disabled={!labels.data}>
                  <option value="">All labels</option>
                  {labels.data?.labels.map((l) => <option key={l} value={l}>{l}</option>)}
                </Select>
              </Field>
            </div>
            <Checkbox label="Include closed items" checked={includeClosed} onChange={(e) => setIncludeClosed(e.target.checked)} />
            <div className="flex items-center gap-3">
              <Button variant="primary" busy={sync.running} disabled={!status?.config.hasProviderToken || !repo} onClick={() => void sync.start('/api/sync', { repository: which, labels: labelFilter ? [labelFilter] : [], includeClosed })}><LuDatabase size={14} />Synchronize {repo || ''}</Button>
              <span className="text-[12px] text-ink-3">Up to {status?.config.maxSyncItems} items, newest first.</span>
            </div>
            {sync.job ? <Progress done={sync.job.progressDone} total={sync.job.progressTotal} label={sync.job.message ?? sync.job.state} /> : null}
            {sync.error ? <Notice tone="err">{sync.error}</Notice> : null}
          </div>
          {snapshot.data ? (
            <div className="hairline px-4 py-3 text-[12.5px]">
              <div className="mb-1 text-ink-2">Labels in the current snapshot</div>
              <div className="flex flex-wrap gap-1.5">
                {snapshot.data.labels.slice(0, 40).map((l) => <Pill key={l.label} tone="neutral">{l.label} <span className="num ml-1 text-ink-3">{l.count}</span></Pill>)}
              </div>
            </div>
          ) : null}
        </Section>
      </div>
    </Page>
  );
}
