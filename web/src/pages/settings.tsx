import { useEffect, useState } from 'react';
import { LuSave, LuSparkles } from 'react-icons/lu';
import { Button, Field, Input, Mono, Notice, Page, Pill, Section, Select, useToast } from '../components/ui';
import { describeError, post } from '../lib/api';
import { type Status } from '../lib/schemas';
import { invalidate } from '../lib/useResource';
import { z } from 'zod';

const LlmTest = z.object({ ok: z.boolean(), profile: z.string(), model: z.string(), text: z.string().optional(), elapsedMs: z.number().optional(), code: z.string().optional(), error: z.string().optional() });

export function SettingsPage({ status, refreshStatus }: { status: Status | undefined; refreshStatus: () => Promise<void> }) {
  const toast = useToast();
  const c = status?.config;
  const [provider, setProvider] = useState<'github' | 'gitlab'>('github');
  const [baseUrl, setBaseUrl] = useState('');
  const [source, setSource] = useState('');
  const [sandbox, setSandbox] = useState('');
  const [labelFilter, setLabelFilter] = useState('');
  const [llmProfile, setLlmProfile] = useState('auto');
  const [saving, setSaving] = useState(false);
  const [llmBusy, setLlmBusy] = useState(false);
  const [llmResult, setLlmResult] = useState<z.infer<typeof LlmTest> | null>(null);
  const [loadedFor, setLoadedFor] = useState<string | null>(null);

  useEffect(() => {
    if (!c) return;
    const key = JSON.stringify([c.provider, c.baseUrl, c.sourceRepository, c.sandboxRepository, c.labelFilter, c.llmProfile]);
    if (loadedFor === key) return;
    setProvider(c.provider); setBaseUrl(c.baseUrl); setSource(c.sourceRepository); setSandbox(c.sandboxRepository); setLabelFilter(c.labelFilter ?? ''); setLlmProfile(c.llmProfile);
    setLoadedFor(key);
  }, [c, loadedFor]);

  const save = async () => {
    setSaving(true);
    try {
      await post('/api/config', { provider, baseUrl, sourceRepository: source, sandboxRepository: sandbox, labelFilter, llmProfile });
      invalidate('/api/');
      await refreshStatus();
      toast.push('ok', 'Settings applied for this session.');
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setSaving(false);
    }
  };

  const testLlm = async () => {
    setLlmBusy(true);
    setLlmResult(null);
    try {
      const r = LlmTest.parse(await post('/api/llm/test', { profile: llmProfile }));
      setLlmResult(r);
      toast.push(r.ok ? 'ok' : 'err', r.ok ? `${r.model} answered in ${r.elapsedMs} ms.` : r.error ?? 'Model test failed');
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setLlmBusy(false);
    }
  };

  return (
    <Page title="Settings" lede="Session settings for the local engine. Repositories and the model profile can change here; write mode and tokens come only from the launcher and config/cascade.env.">
      <div className="grid gap-5 xl:grid-cols-2">
        <Section title="Repositories">
          <div className="flex flex-col gap-3 px-4 py-3">
            <Field label="Provider"><Select value={provider} onChange={(e) => setProvider(e.target.value as typeof provider)}><option value="github">GitHub (github.com or Enterprise)</option><option value="gitlab">GitLab (web.git.mil or other)</option></Select></Field>
            <Field label="API base URL" hint={provider === 'github' ? 'Empty uses https://api.github.com. GitHub Enterprise: https://ghe.example/api/v3' : 'Empty uses https://web.git.mil. /api/v4 is added automatically.'}><Input value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder={provider === 'github' ? 'https://api.github.com' : 'https://web.git.mil'} /></Field>
            <Field label="Source repository (read)" hint={provider === 'github' ? 'owner/repo' : 'Numeric project id or group/project path'}><Input value={source} onChange={(e) => setSource(e.target.value)} placeholder={provider === 'github' ? 'sun-of-anton/cascade-sandbox' : '16629'} /></Field>
            <Field label="Sandbox repository (write)" hint="The only place Cascade may create, update or delete. Can be the same as the source during development."><Input value={sandbox} onChange={(e) => setSandbox(e.target.value)} placeholder={provider === 'github' ? 'sun-of-anton/cascade-sandbox' : '52999'} /></Field>
            <Field label="Default label filter" hint="Exact label; narrows review and hierarchy views. Leave empty for everything."><Input value={labelFilter} onChange={(e) => setLabelFilter(e.target.value)} placeholder="e.g. team::Data-Crucible" /></Field>
            <div className="flex items-center gap-3">
              <Button variant="primary" onClick={save} busy={saving}><LuSave size={14} />Apply</Button>
              <span className="text-[12px] text-ink-3">Applies to this session. To keep it, put the values in <Mono>config/cascade.env</Mono>.</span>
            </div>
          </div>
        </Section>
        <div className="flex flex-col gap-5">
          <Section title="Model">
            <div className="flex flex-col gap-3 px-4 py-3">
              <Field label="Profile" hint="Auto picks the first profile with a token: GenAI, then Grok, then OpenAI-compatible.">
                <Select value={llmProfile} onChange={(e) => setLlmProfile(e.target.value)}>
                  <option value="auto">Auto</option>
                  {c?.llmProfiles.map((p) => <option key={p.profile} value={p.profile}>{p.profile} · {p.model}{p.configured ? '' : ' (no token)'}</option>)}
                </Select>
              </Field>
              <dl className="grid grid-cols-[120px_1fr] gap-y-1 text-[12.5px]">
                <dt className="text-ink-2">Resolved</dt><dd>{c?.llmResolvedProfile} · <Mono>{c?.llmModel}</Mono></dd>
                <dt className="text-ink-2">Endpoint</dt><dd><Mono>{c?.llmUrl}</Mono></dd>
                <dt className="text-ink-2">Token</dt><dd>{c?.hasLlmToken ? <Pill tone="ok">loaded</Pill> : <Pill tone="warn">missing</Pill>}</dd>
              </dl>
              <div className="flex items-center gap-3">
                <Button onClick={testLlm} busy={llmBusy} disabled={!c?.hasLlmToken}><LuSparkles size={14} />Test model</Button>
                <Button variant="quiet" onClick={save} busy={saving}>Apply profile</Button>
              </div>
              {llmResult ? <Notice tone={llmResult.ok ? 'ok' : 'err'}>{llmResult.ok ? <>Reply: <Mono>{llmResult.text}</Mono></> : <>{llmResult.code}: {llmResult.error}</>}</Notice> : null}
            </div>
          </Section>
          <Section title="Environment">
            <div className="px-4 py-3 text-[12.5px] text-ink-2">
              <p>Write mode is <Pill tone={c?.writeMode === 'production' ? 'production' : 'sandbox'}>{c?.writeMode}</Pill>{c?.allowProduction ? ' with CASCADE_ALLOW_PRODUCTION set' : ''}. Changing it needs a relaunch with a different <Mono>config/cascade.env</Mono>; the browser cannot do it.</p>
              <p className="mt-2">Root <Mono>{c?.rootDirectory}</Mono> · output <Mono>{c?.outputDirectory}</Mono></p>
              {status?.launcher && Object.keys(status.launcher).length > 0 ? <p className="mt-2">Launcher: {Object.entries(status.launcher).map(([k, v]) => `${k}=${String(v)}`).join(', ')}</p> : null}
            </div>
          </Section>
        </div>
      </div>
    </Page>
  );
}
