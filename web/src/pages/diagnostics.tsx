import { useState } from 'react';
import { Button, Empty, Mono, Page, Pill, Section, Select, Tabs, tabPanelProps } from '../components/ui';
import { shortDate } from '../lib/format';
import { Diagnostics, Job, WriteAudit } from '../lib/schemas';
import { useResource } from '../lib/useResource';
import { z } from 'zod';
import { Skeleton } from './overview';

export function DiagnosticsPage() {
  const [tab, setTab] = useState<'log' | 'writes' | 'jobs'>('log');
  const [level, setLevel] = useState('');
  const log = useResource(`/api/diagnostics?count=300${level ? `&level=${level}` : ''}`, Diagnostics, { pollMs: tab === 'log' ? 4000 : undefined });
  const writes = useResource('/api/writes', WriteAudit, { pollMs: tab === 'writes' ? 4000 : undefined });
  const jobs = useResource('/api/jobs', z.array(Job), { pollMs: tab === 'jobs' ? 3000 : undefined });

  return (
    <Page title="Diagnostics" lede="What the local engine did, every write it attempted, and the jobs it ran. Tokens never appear here; token-shaped strings are redacted before logging.">
      <Section>
        <Tabs idPrefix="diagnostics" label="Diagnostics views" value={tab} onChange={setTab} items={[{ value: 'log', label: 'Engine log' }, { value: 'writes', label: 'Write audit', count: writes.data?.entries.length }, { value: 'jobs', label: 'Jobs', count: jobs.data?.length }]} />
        <div {...tabPanelProps('diagnostics', tab)}>
        {tab === 'log' ? (
          <>
            <div className="flex items-center gap-3 px-4 py-2.5">
              <Select value={level} onChange={(e) => setLevel(e.target.value)} className="w-36" aria-label="Level"><option value="">All levels</option><option value="ERROR">Errors</option><option value="WARN">Warnings</option><option value="INFO">Info</option></Select>
              <Button size="sm" variant="quiet" onClick={() => void log.refetch()}>Refresh</Button>
              {log.data?.file ? <span className="ml-auto text-[12px] text-ink-3">File: <Mono>{log.data.file}</Mono></span> : null}
            </div>
            {!log.data ? <Skeleton rows={10} /> : log.data.entries.length === 0 ? <Empty title="No log entries" /> : (
              <div className="scroll-y max-h-[calc(100vh-280px)]">
                <table className="table-dense w-full text-[12px]">
                  <thead><tr><th className="w-40">Time</th><th className="w-16">Level</th><th className="w-48">Event</th><th>Message</th><th className="w-28">Request</th></tr></thead>
                  <tbody>
                    {log.data.entries.map((e, i) => (
                      <tr key={i}>
                        <td className="mono text-ink-3">{shortDate(e.time)}</td>
                        <td><Pill tone={e.level === 'ERROR' ? 'err' : e.level === 'WARN' ? 'warn' : 'neutral'}>{e.level.toLowerCase()}</Pill></td>
                        <td className="mono">{e.event}</td>
                        <td className="text-ink-2 break-words">{e.message}</td>
                        <td className="mono text-ink-3">{e.requestId}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        ) : null}
        {tab === 'writes' ? (
          !writes.data ? <Skeleton rows={6} /> : (
            <>
              <p className="px-4 py-2.5 text-[12.5px] text-ink-2">Policy: {writes.data.policy.description}. Deletes require one of: {writes.data.policy.deleteRequiresAnyLabel.join(', ')}.</p>
              {writes.data.entries.length === 0 ? <Empty title="No writes this session">Seeding, teardown and applying proposals all pass through the write gateway and appear here, success or failure.</Empty> : (
                <div className="scroll-y max-h-[calc(100vh-300px)]">
                  <table className="table-dense w-full text-[12px]">
                    <thead><tr><th className="w-40">Time</th><th className="w-28">Operation</th><th className="w-56">Target</th><th>Item</th><th className="w-40">Run</th><th className="w-24">Outcome</th></tr></thead>
                    <tbody>
                      {writes.data.entries.map((w, i) => (
                        <tr key={i}>
                          <td className="mono text-ink-3">{shortDate(w.at)}</td>
                          <td>{w.operation}</td>
                          <td className="mono">{w.target}</td>
                          <td>{w.number ? <span className="mono text-ink-3">#{w.number} </span> : null}{w.title}{w.error ? <div className="text-err">{w.error}</div> : null}</td>
                          <td className="mono text-ink-3">{w.runId}</td>
                          <td>{w.succeeded ? <Pill tone="ok">ok</Pill> : <Pill tone="err">failed</Pill>}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          )
        ) : null}
        {tab === 'jobs' ? (
          !jobs.data ? <Skeleton rows={4} /> : jobs.data.length === 0 ? <Empty title="No jobs this session" /> : (
            <table className="table-dense w-full text-[12px]">
              <thead><tr><th className="w-36">Job</th><th className="w-24">Kind</th><th className="w-24">State</th><th>Last message</th><th className="w-40">Finished</th></tr></thead>
              <tbody>
                {jobs.data.map((j) => (
                  <tr key={j.id}>
                    <td className="mono">{j.id}</td>
                    <td>{j.kind}</td>
                    <td><Pill tone={j.state === 'succeeded' ? 'ok' : j.state === 'failed' ? 'err' : j.state === 'running' ? 'info' : 'neutral'}>{j.state}</Pill></td>
                    <td className="text-ink-2">{j.error ?? j.message}</td>
                    <td className="mono text-ink-3">{j.finishedAt ? shortDate(j.finishedAt) : '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        ) : null}
        </div>
      </Section>
    </Page>
  );
}
