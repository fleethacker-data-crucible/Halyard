import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { LuActivity, LuArrowRightToLine, LuBoxes, LuCalendarRange, LuClipboardCheck, LuCompass, LuGitBranch, LuLayoutDashboard, LuMonitor, LuMoon, LuPanelLeftClose, LuPanelLeftOpen, LuPlug, LuPower, LuPresentation, LuSettings2, LuSparkles, LuSun, LuTestTubeDiagonal } from 'react-icons/lu';
import { post, sessionToken, startHeartbeat } from './lib/api';
import { ageLabel } from './lib/format';
import { Status } from './lib/schemas';
import { useLocalState } from './lib/storage';
import { useTheme, type ThemePref } from './lib/theme';
import { useResource } from './lib/useResource';
import { Button, ConfirmDialog, Emblem, Notice, Pill, ToastProvider, cn, useToast } from './components/ui';
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
  { route: 'overview', label: 'Overview', icon: <LuLayoutDashboard size={20} />, group: 'work' },
  { route: 'leadership', label: 'Leadership', icon: <LuPresentation size={20} />, group: 'work' },
  { route: 'vision', label: 'Vision', icon: <LuCompass size={20} />, group: 'work' },
  { route: 'intake', label: 'Intake', icon: <LuArrowRightToLine size={20} />, group: 'work' },
  { route: 'hierarchy', label: 'Hierarchy', icon: <LuGitBranch size={20} />, group: 'work' },
  { route: 'review', label: 'Review', icon: <LuClipboardCheck size={20} />, group: 'work' },
  { route: 'proposals', label: 'Proposals', icon: <LuSparkles size={20} />, group: 'work' },
  { route: 'plan', label: 'Plan', icon: <LuCalendarRange size={20} />, group: 'work' },
  { route: 'sandbox', label: 'Sandbox', icon: <LuTestTubeDiagonal size={20} />, group: 'operate' },
  { route: 'connection', label: 'Connection', icon: <LuPlug size={20} />, group: 'operate' },
  { route: 'diagnostics', label: 'Diagnostics', icon: <LuActivity size={20} />, group: 'operate' },
  { route: 'settings', label: 'Settings', icon: <LuSettings2 size={20} />, group: 'operate' },
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
  const [collapsed, setCollapsed] = useLocalState('sidebar-collapsed', false);

  useEffect(() => startHeartbeat(), []);

  const endSession = useCallback(async () => {
    try {
      await post('/api/session/stop', {}, { 'X-Halyard-Intent': 'end-session' });
      setStopped(true);
    } catch (e) {
      toast.push('err', e instanceof Error ? e.message : String(e));
    } finally {
      setConfirmStop(false);
    }
  }, [toast]);

  if (!sessionToken()) {
    return (
      <main className="mx-auto flex max-w-[60ch] flex-col items-center px-6 py-12 text-center">
        <Emblem size={280} />
        <h1 className="mt-4 text-[28px]">Open Halyard from the launcher</h1>
        <p className="mt-3 text-ink-2">This page has no session token, so the local engine will refuse its requests. Run <code>Launch-Halyard.bat</code>; it opens the browser with a one-time link to this session.</p>
      </main>
    );
  }

  if (stopped) {
    return (
      <main className="mx-auto flex max-w-[60ch] flex-col items-center px-6 py-12 text-center">
        <Emblem size={280} />
        <h1 className="mt-4 text-[28px]">Session ended</h1>
        <p className="mt-3 text-ink-2">The local Halyard engine has stopped. Close this tab, or run the launcher again to start a new session.</p>
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

  const label = collapsed ? 'sr-only' : 'max-lg:sr-only';
  const centred = collapsed ? 'justify-center px-0' : 'max-lg:justify-center max-lg:px-0';
  return (
    <div className={cn('grid h-screen grid-rows-[minmax(0,1fr)_28px]', collapsed ? 'grid-cols-[64px_minmax(0,1fr)]' : 'grid-cols-[240px_minmax(0,1fr)] max-lg:grid-cols-[64px_minmax(0,1fr)]')}>
      <nav className="flex min-h-0 flex-col border-r border-line-2 bg-sidebar" aria-label="Halyard">
        {/* Expanded: the full emblem with its wordmark. Collapsed rail: the emblem's circle only. */}
        <div className={cn('flex-col items-center px-3 pt-3 pb-2', collapsed ? 'hidden' : 'flex max-lg:hidden')}>
          <Emblem size={168} />
          <div className="text-[12px] text-ink-3">Delivery intelligence</div>
        </div>
        <div className={cn('justify-center py-4', collapsed ? 'flex' : 'hidden max-lg:flex')}>
          <img src="brand/icon-256.png" alt="Halyard" width={40} height={40} />
        </div>
        <div className="scroll-y min-h-0 flex-1">
          <NavGroup label="Backlog" items={NAV.filter((n) => n.group === 'work')} route={route} collapsed={collapsed} />
          <NavGroup label="Operate" items={NAV.filter((n) => n.group === 'operate')} route={route} collapsed={collapsed} />
        </div>
        <div className="flex flex-col gap-1 border-t border-line-2 px-2 py-2">
          <ThemeSwitch className={centred} labelClass={label} />
          <Button variant="quiet" className={cn('h-8 justify-start px-3 text-ink-2', centred)} onClick={() => setCollapsed(!collapsed)} title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}>
            {collapsed ? <LuPanelLeftOpen size={16} /> : <LuPanelLeftClose size={16} />}<span className={label}>Collapse sidebar</span>
          </Button>
          <Button variant="quiet" className={cn('h-8 justify-start px-3 text-ink-2', centred)} onClick={() => setConfirmStop(true)} title="End session">
            <LuPower size={16} /><span className={label}>End session</span>
          </Button>
        </div>
      </nav>
      <div className="flex min-h-0 min-w-0 flex-col">
        <StatusStrip status={s} error={status.error} />
        <main className="scroll-y min-h-0 min-w-0 flex-1">{page}</main>
      </div>
      <StatusBar status={s} error={status.error} />
      <ConfirmDialog open={confirmStop} title="End this Halyard session?" confirmLabel="End session" onConfirm={endSession} onCancel={() => setConfirmStop(false)}>
        The local engine stops and this page stops working. Snapshots, proposals and seed manifests stay on disk under <code>output/</code>.
      </ConfirmDialog>
    </div>
  );
}

function NavGroup({ label, items, route, collapsed }: { label: string; items: typeof NAV; route: Route; collapsed: boolean }) {
  const hide = collapsed ? 'sr-only' : 'max-lg:sr-only';
  return (
    <div className="pb-3">
      <div className={cn('px-4 pb-1 text-[12px] font-medium text-ink-3', hide)}>{label}</div>
      <ul className="fl-nav flex flex-col gap-px">
        {items.map((n) => (
          <li key={n.route}>
            <a
              href={`#/${n.route}`}
              aria-current={route === n.route ? 'page' : undefined}
              title={n.label}
              className={cn('flex min-h-9 items-center gap-3 px-4 text-ink-2 hover:bg-surface-2 hover:text-ink', route === n.route && 'bg-surface-2 font-medium', collapsed ? 'justify-center px-0' : 'max-lg:justify-center max-lg:px-0')}
            >
              {n.icon}
              <span className={hide}>{n.label}</span>
            </a>
          </li>
        ))}
      </ul>
    </div>
  );
}

const THEMES: Array<{ value: ThemePref; label: string; icon: ReactNode }> = [
  { value: 'system', label: 'System theme', icon: <LuMonitor size={16} /> },
  { value: 'dark', label: 'Night watch', icon: <LuMoon size={16} /> },
  { value: 'light', label: 'Chart room', icon: <LuSun size={16} /> },
];

function ThemeSwitch({ className, labelClass }: { className: string; labelClass: string }) {
  const [pref, setPref] = useTheme();
  const i = Math.max(0, THEMES.findIndex((t) => t.value === pref));
  const current = THEMES[i]!;
  const next = THEMES[(i + 1) % THEMES.length]!;
  return (
    <Button variant="quiet" className={cn('h-8 justify-start px-3 text-ink-2', className)} onClick={() => setPref(next.value)} title={`Theme: ${current.label}. Switch to ${next.label}`}>
      {current.icon}<span className={labelClass}>{current.label}</span>
    </Button>
  );
}

function StatusBar({ status, error }: { status: Status | undefined; error: string | null }) {
  // UNIFIED-UI 5: engine state as a dot and a word, the bound address, session state, version.
  const state = status && !error ? 'Running' : status ? 'Reconnecting' : error ? 'Disconnected' : 'Connecting';
  const dot = state === 'Running' ? 'bg-ok' : state === 'Disconnected' ? 'bg-err' : 'bg-warn';
  return (
    <footer className="col-span-2 flex items-center gap-4 border-t border-line-2 bg-sidebar px-4 text-[12px] text-ink-2">
      <span className="flex items-center gap-1.5"><span className={cn('inline-block h-2 w-2 rounded-full', dot)} aria-hidden />{state}</span>
      <span className="mono">{window.location.host}</span>
      <span>{status ? (status.provider.identity ? <>Signed in as <span className="text-ink">{status.provider.identity.login}</span></> : 'Not connected') : '—'}</span>
      <span className="ml-auto">Halyard {status ? status.version : '—'}</span>
    </footer>
  );
}

function StatusStrip({ status, error }: { status: Status | undefined; error: string | null }) {
  if (error && !status) {
    return <Notice tone="err" className="m-3">Cannot reach the local engine: {error}. If you closed the launcher window, start Halyard again.</Notice>;
  }
  if (error) {
    // The engine answered before and has stopped since: say so, or every button on the page seems to do nothing.
    return <Notice tone="err" className="m-3">Lost the local engine ({error}). It may have stopped itself or been closed; nothing on this page can be saved until Halyard is started again with Launch-Halyard.bat.</Notice>;
  }
  if (!status) return <div className="h-9 border-b border-line-2 bg-surface" />;
  const repo = status.config.sourceRepository || status.config.sandboxRepository || 'no repository';
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1 border-b border-line-2 bg-surface px-6 py-1.5 text-[12px] text-ink-2">
      <span className="flex items-center gap-1.5"><LuBoxes size={13} className="text-ink-3" />{status.config.provider === 'gitlab' ? 'GitLab' : 'GitHub'} · <span className="mono text-ink">{repo}</span></span>
      <Pill tone={status.policy.mode === 'production' ? 'production' : 'sandbox'}>{status.policy.mode === 'production' ? 'Production writes enabled' : 'Sandbox-only writes'}</Pill>
      <span className="ml-auto">{status.snapshot ? <>Snapshot of {status.snapshot.count} items, {ageLabel(status.snapshot.ageSeconds)}</> : 'No snapshot yet'}</span>
      {status.jobs.running > 0 ? <Pill tone="info">{status.jobs.running} job{status.jobs.running === 1 ? '' : 's'} running</Pill> : null}
    </div>
  );
}
