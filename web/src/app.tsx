import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { LuActivity, LuArrowRightToLine, LuBoxes, LuCalendarRange, LuClipboardCheck, LuCompass, LuGitBranch, LuLayoutDashboard, LuPlug, LuPower, LuPresentation, LuSettings2, LuSparkles, LuTestTubeDiagonal } from 'react-icons/lu';
import { post, sessionToken, startHeartbeat } from './lib/api';
import { ageLabel } from './lib/format';
import { Status } from './lib/schemas';
import { useResource } from './lib/useResource';
import { Button, ConfirmDialog, Notice, Pill, ToastProvider, cn, useToast } from './components/ui';
import { ConnectionPage } from './pages/connection';
import { DiagnosticsPage } from './pages/diagnostics';
import { HierarchyPage } from './pages/hierarchy';
import { IntakePage } from './pages/intake';
import { LeadershipPage } from './pages/leadership';
import { OverviewPage } from './pages/overview';
import { PlanPage } from './pages/plan';
import { ProposalsPage } from './pages/proposals';
import { ReviewPage } from './pages/review';
import { SandboxPage } from './pages/sandbox';
import { SettingsPage } from './pages/settings';
import { VisionPage } from './pages/vision';

export type Route = 'overview' | 'leadership' | 'vision' | 'intake' | 'hierarchy' | 'review' | 'proposals' | 'plan' | 'sandbox' | 'connection' | 'diagnostics' | 'settings';

const NAV: Array<{ route: Route; label: string; icon: ReactNode; group: 'work' | 'operate' }> = [
  { route: 'overview', label: 'Overview', icon: <LuLayoutDashboard size={16} />, group: 'work' },
  { route: 'leadership', label: 'Leadership', icon: <LuPresentation size={16} />, group: 'work' },
  { route: 'vision', label: 'Vision', icon: <LuCompass size={16} />, group: 'work' },
  { route: 'intake', label: 'Intake', icon: <LuArrowRightToLine size={16} />, group: 'work' },
  { route: 'hierarchy', label: 'Hierarchy', icon: <LuGitBranch size={16} />, group: 'work' },
  { route: 'review', label: 'Review', icon: <LuClipboardCheck size={16} />, group: 'work' },
  { route: 'proposals', label: 'Proposals', icon: <LuSparkles size={16} />, group: 'work' },
  { route: 'plan', label: 'Plan', icon: <LuCalendarRange size={16} />, group: 'work' },
  { route: 'sandbox', label: 'Sandbox', icon: <LuTestTubeDiagonal size={16} />, group: 'operate' },
  { route: 'connection', label: 'Connection', icon: <LuPlug size={16} />, group: 'operate' },
  { route: 'diagnostics', label: 'Diagnostics', icon: <LuActivity size={16} />, group: 'operate' },
  { route: 'settings', label: 'Settings', icon: <LuSettings2 size={16} />, group: 'operate' },
];

export function parseHash(hash: string): { route: Route; params: URLSearchParams } {
  const raw = hash.replace(/^#\/?/, '');
  const [path, query = ''] = raw.split('?');
  const route = (NAV.find((n) => n.route === path)?.route ?? 'overview') as Route;
  return { route, params: new URLSearchParams(query) };
}

export function navigate(route: Route, params?: Record<string, string>) {
  const q = params ? new URLSearchParams(params).toString() : '';
  window.location.hash = `/${route}${q ? '?' + q : ''}`;
}

function useHashRoute() {
  const [state, setState] = useState(() => parseHash(window.location.hash));
  useEffect(() => {
    const onChange = () => setState(parseHash(window.location.hash));
    window.addEventListener('hashchange', onChange);
    return () => window.removeEventListener('hashchange', onChange);
  }, []);
  return state;
}

export function App() {
  return (
    <ToastProvider>
      <Shell />
    </ToastProvider>
  );
}

function Shell() {
  const { route, params } = useHashRoute();
  const status = useResource('/api/status', Status, { pollMs: 5000 });
  const toast = useToast();
  const [confirmStop, setConfirmStop] = useState(false);
  const [stopped, setStopped] = useState(false);

  useEffect(() => startHeartbeat(), []);

  const endSession = useCallback(async () => {
    try {
      await post('/api/session/stop', {}, { 'X-Cascade-Intent': 'end-session' });
      setStopped(true);
    } catch (e) {
      toast.push('err', e instanceof Error ? e.message : String(e));
    } finally {
      setConfirmStop(false);
    }
  }, [toast]);

  if (!sessionToken()) {
    return (
      <main className="mx-auto max-w-[60ch] px-6 py-16">
        <h1>Open Cascade from the launcher</h1>
        <p className="mt-3 text-ink-2">This page has no session token, so the local engine will refuse its requests. Run <code>Launch-Cascade.bat</code>; it opens the browser with a one-time link to this session.</p>
      </main>
    );
  }

  if (stopped) {
    return (
      <main className="mx-auto max-w-[60ch] px-6 py-16">
        <h1>Session ended</h1>
        <p className="mt-3 text-ink-2">The local Cascade engine has stopped. Close this tab, or run the launcher again to start a new session.</p>
      </main>
    );
  }

  const s = status.data;
  const page = (() => {
    switch (route) {
      case 'leadership': return <LeadershipPage status={s} />;
      case 'vision': return <VisionPage status={s} />;
      case 'intake': return <IntakePage status={s} />;
      case 'hierarchy': return <HierarchyPage params={params} status={s} />;
      case 'review': return <ReviewPage params={params} status={s} />;
      case 'proposals': return <ProposalsPage params={params} status={s} />;
      case 'plan': return <PlanPage status={s} />;
      case 'sandbox': return <SandboxPage status={s} refreshStatus={status.refetch} />;
      case 'connection': return <ConnectionPage status={s} refreshStatus={status.refetch} />;
      case 'diagnostics': return <DiagnosticsPage />;
      case 'settings': return <SettingsPage status={s} refreshStatus={status.refetch} />;
      default: return <OverviewPage status={s} />;
    }
  })();

  return (
    <div className="grid min-h-screen grid-cols-[216px_minmax(0,1fr)]">
      <nav className="sticky top-0 flex h-screen flex-col border-r border-line-2 bg-surface">
        <div className="flex items-center gap-2.5 px-4 py-4">
          <CascadeMark />
          <div className="leading-tight">
            <div className="text-[15px] font-semibold tracking-tight">Cascade</div>
            <div className="text-[11.5px] text-ink-3">Delivery intelligence</div>
          </div>
        </div>
        <NavGroup label="Backlog" items={NAV.filter((n) => n.group === 'work')} route={route} />
        <NavGroup label="Operate" items={NAV.filter((n) => n.group === 'operate')} route={route} />
        <div className="mt-auto flex flex-col gap-2 border-t border-line-2 px-4 py-3 text-[12px] text-ink-3">
          {s ? <span>Engine {s.version} · port {s.port}</span> : <span>Connecting to engine…</span>}
          <Button variant="quiet" size="sm" className="justify-start px-1 text-ink-2" onClick={() => setConfirmStop(true)}><LuPower size={14} />End session</Button>
        </div>
      </nav>
      <div className="flex min-w-0 flex-col">
        <StatusStrip status={s} error={status.error} />
        <main className="min-w-0 flex-1">{page}</main>
      </div>
      <ConfirmDialog open={confirmStop} title="End this Cascade session?" confirmLabel="End session" onConfirm={endSession} onCancel={() => setConfirmStop(false)}>
        The local engine stops and this page stops working. Snapshots, proposals and seed manifests stay on disk under <code>output/</code>.
      </ConfirmDialog>
    </div>
  );
}

function NavGroup({ label, items, route }: { label: string; items: typeof NAV; route: Route }) {
  return (
    <div className="px-2 pb-3">
      <div className="px-2 pb-1 text-[11.5px] text-ink-3">{label}</div>
      <ul className="flex flex-col gap-px">
        {items.map((n) => (
          <li key={n.route}>
            <a
              href={`#/${n.route}`}
              aria-current={route === n.route ? 'page' : undefined}
              className={cn('flex items-center gap-2.5 rounded-ctl px-2 py-1.5 text-ink-2 hover:bg-surface-2 hover:text-ink', route === n.route && 'bg-surface-2 text-ink font-medium')}
            >
              <span className={cn('text-ink-3', route === n.route && 'text-accent')}>{n.icon}</span>
              {n.label}
            </a>
          </li>
        ))}
      </ul>
    </div>
  );
}

function StatusStrip({ status, error }: { status: Status | undefined; error: string | null }) {
  if (error && !status) {
    return <Notice tone="err" className="m-3">Cannot reach the local engine: {error}. If you closed the launcher window, start Cascade again.</Notice>;
  }
  if (!status) return <div className="h-9 border-b border-line-2 bg-surface" />;
  const repo = status.config.sourceRepository || status.config.sandboxRepository || 'no repository';
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1 border-b border-line-2 bg-surface px-6 py-1.5 text-[12px] text-ink-2">
      <span className="flex items-center gap-1.5"><LuBoxes size={13} className="text-ink-3" />{status.config.provider === 'gitlab' ? 'GitLab' : 'GitHub'} · <span className="mono text-ink">{repo}</span></span>
      <Pill tone={status.policy.mode === 'production' ? 'production' : 'sandbox'}>{status.policy.mode === 'production' ? 'Production writes enabled' : 'Sandbox-only writes'}</Pill>
      {status.provider.identity ? <span>Signed in as <span className="text-ink">{status.provider.identity.login}</span></span> : <span className="text-ink-3">Not connected</span>}
      <span className="ml-auto">{status.snapshot ? <>Snapshot of {status.snapshot.count} items, {ageLabel(status.snapshot.ageSeconds)}</> : 'No snapshot yet'}</span>
      {status.jobs.running > 0 ? <Pill tone="info">{status.jobs.running} job{status.jobs.running === 1 ? '' : 's'} running</Pill> : null}
    </div>
  );
}

function CascadeMark() {
  // Three equal arcs forming a "c": theme to epic to story. Top arc follows the theme (navy / light on dark).
  return (
    <svg width="28" height="28" viewBox="0 0 32 32" aria-hidden fill="none" strokeWidth="5" strokeLinecap="round">
      <path d="M26.11 8.6A11.5 11.5 0 0 0 13.23 5.24" stroke="var(--mark-top)" />
      <path d="M7.91 9.35A11.5 11.5 0 0 0 7.91 22.65" stroke="#14b8a6" />
      <path d="M13.23 26.76A11.5 11.5 0 0 0 26.11 23.4" stroke="#38bdf8" />
    </svg>
  );
}
