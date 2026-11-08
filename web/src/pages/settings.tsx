import { useEffect, useState } from 'react';
import { LuKeyRound, LuSave, LuSparkles } from 'react-icons/lu';
import { Button, Checkbox, Field, Input, Mono, Notice, Page, Pill, Section, useToast } from '../components/ui';
import { AppearanceSettings } from '../components/appearance-settings';
import { VoiceSettings } from '../components/voice-settings';
import { describeError, post } from '../lib/api';
import { type Status } from '../lib/schemas';
import { invalidate } from '../lib/useResource';
import { z } from 'zod';

const LlmTest = z.object({ ok: z.boolean(), source: z.string().optional(), model: z.string(), text: z.string().optional(), elapsedMs: z.number().optional(), code: z.string().optional(), error: z.string().optional() });

const ROUTE_SUGGESTIONS = ['auto', 'fast', 'smart'];

export function SettingsPage({ status, refreshStatus }: { status: Status | undefined; refreshStatus: () => Promise<void> }) {
  const toast = useToast();
  const c = status?.config;
  const [labelFilter, setLabelFilter] = useState('');
  const [saving, setSaving] = useState(false);
  const [llmBusy, setLlmBusy] = useState(false);
  const [llmResult, setLlmResult] = useState<z.infer<typeof LlmTest> | null>(null);
  const [loadedFor, setLoadedFor] = useState<string | null>(null);

  useEffect(() => {
    if (!c) return;
    const key = JSON.stringify([c.provider, c.baseUrl, c.sourceRepository, c.sandboxRepository, c.labelFilter]);
    if (loadedFor === key) return;
    setLabelFilter(c.labelFilter ?? '');
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

  const testLlm = async () => {
    setLlmBusy(true);
    setLlmResult(null);
    try {
      const r = LlmTest.parse(await post('/api/llm/test', {}));
      setLlmResult(r);
      toast.push(r.ok ? 'ok' : 'err', r.ok ? `${r.model} answered in ${r.elapsedMs} ms.` : r.error ?? 'Model test failed');
    } catch (e) {
      toast.push('err', describeError(e));
    } finally {
      setLlmBusy(false);
    }
  };

  return (
    <Page title="Settings" lede="Appearance and voice are yours and stay in this browser. Session settings for the local engine: the model route and label filter can change here; repositories, write mode, provider tokens and the Fairlead address come only from the launcher and config/halyard.env. Model calls use the key Fairlead installs for Halyard.">
      <div className="grid gap-5 xl:grid-cols-2">
        <div className="flex flex-col gap-5">
          <Section title="Model calls (Fairlead)" aside={c?.hasLlmToken ? <Pill tone="ok">ready</Pill> : <Pill tone="warn">not ready</Pill>}>
            <ModelForm config={c} saving={saving} onApply={apply} onTest={() => testLlm()} testing={llmBusy} />
            {llmResult ? <Notice tone={llmResult.ok ? 'ok' : 'err'} className="mx-4 mb-3">{llmResult.ok ? <>Reply from <Mono>{llmResult.model}</Mono>: <Mono>{llmResult.text}</Mono></> : <>{llmResult.code}: {llmResult.error}</>}</Notice> : null}
          </Section>
          <VoiceSettings />
        </div>
        <div className="flex flex-col gap-5">
          <AppearanceSettings />
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

function ModelForm({ config, saving, onApply, onTest, testing }: { config: Status['config'] | undefined; saving: boolean; onApply: (body: Record<string, unknown>, okText: string) => Promise<boolean>; onTest: () => void; testing: boolean }) {
  const [route, setRoute] = useState('');
  const [remember, setRemember] = useState(true);
  const [routeLoaded, setRouteLoaded] = useState(false);
  useEffect(() => {
    if (config && !routeLoaded) { setRoute(config.llmModel); setRouteLoaded(true); }
  }, [config, routeLoaded]);

  const save = async () => {
    await onApply({ llmModel: route.trim(), remember }, 'Route updated.');
  };

  return (
    <div className="flex flex-col gap-3 px-4 py-3">
      <p className="text-[12.5px] text-ink-2">Halyard sends every model call to Fairlead on this PC. Fairlead holds the provider addresses and keys; Halyard holds only its own Fairlead key, which lives in Windows Credential Manager as <Mono>{config?.llmKeyTarget ?? 'Fairlead/Halyard'}</Mono> and is never shown here.</p>
      <dl className="grid grid-cols-[120px_1fr] gap-y-1 text-[12.5px]">
        <dt className="text-ink-2">Fairlead</dt><dd><Mono>{config?.llmUrl || '(not set)'}</Mono> <span className="text-ink-3">· HALYARD_FAIRLEAD_URL in config/halyard.env</span></dd>
        <dt className="text-ink-2">Key</dt><dd>{config?.hasLlmToken ? <Pill tone="ok">installed</Pill> : <span className="text-warn">{config?.llmMissing ?? 'Open Fairlead and install a key for Halyard.'}</span>}</dd>
      </dl>
      <Field label="Route" hint="The Fairlead route to use: auto, or one of your own routes such as fast or smart.">
        <Input list="halyard-routes" value={route} onChange={(e) => setRoute(e.target.value)} placeholder="auto" spellCheck={false} />
        <datalist id="halyard-routes">{ROUTE_SUGGESTIONS.map((m) => <option key={m} value={m} />)}</datalist>
      </Field>
      <Checkbox label="Remember on this machine (writes HALYARD_FAIRLEAD_MODEL to config/halyard.env)" checked={remember} onChange={(e) => setRemember(e.target.checked)} />
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="primary" onClick={() => void save()} busy={saving} disabled={!config || route.trim() === config.llmModel}><LuKeyRound size={14} />Use this route</Button>
        <Button onClick={onTest} busy={testing} disabled={!config?.hasLlmToken}><LuSparkles size={14} />Test model</Button>
      </div>
    </div>
  );
}
