import { useEffect, useState } from 'react';
import { LuEye, LuEyeOff, LuKeyRound, LuSave, LuSparkles, LuTrash2 } from 'react-icons/lu';
import { Button, Checkbox, Field, Input, Mono, Notice, Page, Pill, Section, Select, useToast } from '../components/ui';
import { describeError, post } from '../lib/api';
import { type Status } from '../lib/schemas';
import { invalidate } from '../lib/useResource';
import { z } from 'zod';

const LlmTest = z.object({ ok: z.boolean(), profile: z.string(), model: z.string(), text: z.string().optional(), elapsedMs: z.number().optional(), code: z.string().optional(), error: z.string().optional() });

const MODEL_SUGGESTIONS = ['gpt-4o-mini', 'gpt-4o', 'gpt-4.1-mini', 'gpt-4.1', 'o4-mini'];

export function SettingsPage({ status, refreshStatus }: { status: Status | undefined; refreshStatus: () => Promise<void> }) {
  const toast = useToast();
  const c = status?.config;
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
    setLabelFilter(c.labelFilter ?? ''); setLlmProfile(c.llmProfile);
    setLoadedFor(key);
  }, [c, loadedFor]);

  const apply = async (body: Record<string, unknown>, okText: string) => {
    setSaving(true);
    try {
      const r = await post<{ remembered?: boolean }>('/api/config', body);
      invalidate('/api/');
      await refreshStatus();
      toast.push('ok', okText + (r.remembered ? ' Remembered in config/halyard.env.' : ''));
      return true;
    } catch (e) {
      toast.push('err', describeError(e));
      return false;
    } finally {
      setSaving(false);
    }
  };

  const testLlm = async (profile: string) => {
    setLlmBusy(true);
    setLlmResult(null);
    try {
      const r = LlmTest.parse(await post('/api/llm/test', { profile }));
      setLlmResult(r);
      toast.push(r.ok ? 'ok' : 'err', r.ok ? `${r.model} answered in ${r.elapsedMs} ms.` : r.error ?? 'Model test failed');
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setLlmBusy(false);
    }
  };

  const openai = c?.llmProfiles.find((p) => p.profile === 'openai');

  return (
    <Page title="Settings" lede="Session settings for the local engine. The model key, model name, profile and label filter can change here; repositories, endpoints, write mode and provider tokens come only from the launcher and config/halyard.env.">
      <div className="grid gap-5 xl:grid-cols-2">
        <div className="flex flex-col gap-5">
          <Section title="Model API key" aside={openai?.configured ? <Pill tone="ok">key loaded</Pill> : <Pill tone="warn">no key</Pill>}>
            <ApiKeyForm config={c} saving={saving} onApply={apply} onTest={() => testLlm('openai')} testing={llmBusy} />
            {llmResult ? <Notice tone={llmResult.ok ? 'ok' : 'err'} className="mx-4 mb-3">{llmResult.ok ? <>Reply from <Mono>{llmResult.model}</Mono>: <Mono>{llmResult.text}</Mono></> : <>{llmResult.code}: {llmResult.error}</>}</Notice> : null}
          </Section>
          <Section title="Model profile">
            <div className="flex flex-col gap-3 px-4 py-3">
              <Field label="Profile" hint="Auto uses the OpenAI-compatible profile when it has a key, otherwise Databricks.">
                <Select value={llmProfile} onChange={(e) => setLlmProfile(e.target.value)}>
                  <option value="auto">Auto ({c?.llmResolvedProfile ?? '…'})</option>
                  {c?.llmProfiles.map((p) => <option key={p.profile} value={p.profile}>{p.profile} · {p.model || '(no model)'}{p.configured ? '' : ' (not configured)'}</option>)}
                </Select>
              </Field>
              <dl className="grid grid-cols-[120px_1fr] gap-y-1 text-[12.5px]">
                <dt className="text-ink-2">Resolved</dt><dd>{c?.llmResolvedProfile} · <Mono>{c?.llmModel || '(no model)'}</Mono></dd>
                <dt className="text-ink-2">Endpoint</dt><dd><Mono>{c?.llmUrl || '(not set)'}</Mono> <span className="text-ink-3">· from config/halyard.env only</span></dd>
                <dt className="text-ink-2">Status</dt><dd>{c?.hasLlmToken ? <Pill tone="ok">ready</Pill> : <span className="text-warn">{c?.llmMissing}</span>}</dd>
              </dl>
              <div className="flex items-center gap-3">
                <Button variant="primary" onClick={() => void apply({ llmProfile }, 'Profile applied for this session.')} busy={saving}><LuSave size={14} />Apply profile</Button>
                <Button onClick={() => void testLlm(llmProfile)} busy={llmBusy} disabled={!c?.hasLlmToken}><LuSparkles size={14} />Test model</Button>
              </div>
            </div>
          </Section>
        </div>
        <div className="flex flex-col gap-5">
          <Section title="Repositories">
            <div className="flex flex-col gap-3 px-4 py-3">
              <dl className="grid grid-cols-[150px_1fr] gap-y-1.5 text-[12.5px]">
                <dt className="text-ink-2">Provider</dt><dd>{c?.provider === 'gitlab' ? 'GitLab' : 'GitHub'}</dd>
                <dt className="text-ink-2">API</dt><dd><Mono>{c?.baseUrl || '(not set)'}</Mono></dd>
                <dt className="text-ink-2">Source (read)</dt><dd><Mono>{c?.sourceRepository || '(not set)'}</Mono></dd>
                <dt className="text-ink-2">Sandbox (write)</dt><dd><Mono>{c?.sandboxRepository || '(not set)'}</Mono></dd>
                {c && c.protectedRepositories.length > 0 ? <><dt className="text-ink-2">Protected</dt><dd><Mono>{c.protectedRepositories.join(', ')}</Mono></dd></> : null}
              </dl>
              <Notice tone="info">Where Halyard reads, writes and sends tokens is set only in <Mono>config/halyard.env</Mono> (<Mono>HALYARD_PROVIDER</Mono>, <Mono>HALYARD_SOURCE_REPO</Mono>, <Mono>HALYARD_SANDBOX_REPO</Mono>, API URLs). Edit it and relaunch. The browser cannot change these.</Notice>
              <Field label="Default label filter" hint="Exact label; narrows review and hierarchy views. Leave empty for everything."><Input value={labelFilter} onChange={(e) => setLabelFilter(e.target.value)} placeholder="e.g. team::example" /></Field>
              <div className="flex items-center gap-3">
                <Button variant="primary" onClick={() => void apply({ labelFilter }, 'Label filter applied for this session.')} busy={saving}><LuSave size={14} />Apply</Button>
              </div>
            </div>
          </Section>
          <Section title="Environment">
            <div className="px-4 py-3 text-[12.5px] text-ink-2">
              <p>Write mode is <Pill tone={c?.writeMode === 'production' ? 'production' : 'sandbox'}>{c?.writeMode}</Pill>{c?.allowProduction ? ' with HALYARD_ALLOW_PRODUCTION set' : ''}. Changing it needs a relaunch with a different <Mono>config/halyard.env</Mono>; the browser cannot do it.</p>
              <p className="mt-2">Root <Mono>{c?.rootDirectory}</Mono> · output <Mono>{c?.outputDirectory}</Mono></p>
              <p className="mt-2">Env file <Mono>{c?.envFile}</Mono> {c?.envFileExists ? <Pill tone="neutral">present</Pill> : <Pill tone="neutral">not created yet</Pill>}</p>
              {status?.launcher && Object.keys(status.launcher).length > 0 ? <p className="mt-2">Launcher: {Object.entries(status.launcher).map(([k, v]) => `${k}=${String(v)}`).join(', ')}</p> : null}
            </div>
          </Section>
        </div>
      </div>
    </Page>
  );
}

function ApiKeyForm({ config, saving, onApply, onTest, testing }: { config: Status['config'] | undefined; saving: boolean; onApply: (body: Record<string, unknown>, okText: string) => Promise<boolean>; onTest: () => void; testing: boolean }) {
  const [key, setKey] = useState('');
  const [show, setShow] = useState(false);
  const [model, setModel] = useState('');
  const [remember, setRemember] = useState(true);
  const openai = config?.llmProfiles.find((p) => p.profile === 'openai');
  const [modelLoaded, setModelLoaded] = useState(false);
  useEffect(() => {
    if (openai && !modelLoaded) { setModel(openai.model); setModelLoaded(true); }
  }, [openai, modelLoaded]);

  const save = async () => {
    const body: Record<string, unknown> = { remember };
    if (key.trim()) body.llmToken = key.trim();
    if (model.trim() && model.trim() !== openai?.model) body.llmModel = model.trim();
    if (Object.keys(body).length === 1) return;
    if (await onApply(body, key.trim() ? 'API key loaded for this session.' : 'Model updated.')) setKey('');
  };

  const clear = async () => {
    await onApply({ llmToken: null, remember: true }, 'API key removed from this session and from config/halyard.env.');
  };

  return (
    <div className="flex flex-col gap-3 px-4 py-3">
      <p className="text-[12.5px] text-ink-2">Paste an OpenAI API key (or a key for any OpenAI-compatible endpoint) to power sizing and refinement proposals. It is held in memory for this session and sent only to <Mono>{openai?.url || 'the configured endpoint'}</Mono>; with "remember" ticked it is also written to the git-ignored <Mono>config/halyard.env</Mono> on this machine so the next launch has it.</p>
      <Field label="API key" hint={openai?.configured ? 'A key is already loaded; pasting a new one replaces it.' : 'sk-…'}>
        <span className="relative block">
          <Input type={show ? 'text' : 'password'} value={key} onChange={(e) => setKey(e.target.value)} placeholder={openai?.configured ? '•••••••••••••••• (loaded)' : 'sk-...'} autoComplete="off" spellCheck={false} className="pr-9" />
          <button type="button" onClick={() => setShow((s) => !s)} aria-label={show ? 'Hide key' : 'Show key'} className="absolute right-2 top-1/2 -translate-y-1/2 text-ink-3 hover:text-ink">{show ? <LuEyeOff size={14} /> : <LuEye size={14} />}</button>
        </span>
      </Field>
      <Field label="Model" hint="Any chat model the endpoint serves.">
        <Input list="halyard-models" value={model} onChange={(e) => setModel(e.target.value)} placeholder="gpt-4o-mini" spellCheck={false} />
        <datalist id="halyard-models">{MODEL_SUGGESTIONS.map((m) => <option key={m} value={m} />)}</datalist>
      </Field>
      <Checkbox label="Remember on this machine (writes LLM_TOKEN and LLM_MODEL to config/halyard.env)" checked={remember} onChange={(e) => setRemember(e.target.checked)} />
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="primary" onClick={() => void save()} busy={saving} disabled={!key.trim() && (!model.trim() || model.trim() === openai?.model)}><LuKeyRound size={14} />{key.trim() ? 'Use this key' : 'Update model'}</Button>
        <Button onClick={onTest} busy={testing} disabled={!openai?.configured}><LuSparkles size={14} />Test model</Button>
        {openai?.configured ? <Button variant="quiet" className="ml-auto text-err" onClick={() => void clear()} busy={saving}><LuTrash2 size={14} />Forget key</Button> : null}
      </div>
    </div>
  );
}
