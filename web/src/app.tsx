import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { LuCircleHelp, LuActivity, LuArrowRightToLine, LuBoxes, LuCalendarRange, LuClipboardCheck, LuCompass, LuGitBranch, LuLayoutDashboard, LuMonitor, LuMoon, LuPanelLeftClose, LuPanelLeftOpen, LuPlug, LuPower, LuPresentation, LuSettings2, LuSparkles, LuSun, LuTestTubeDiagonal } from 'react-icons/lu';
import { post, sessionToken, startHeartbeat } from './lib/api';
import { ageLabel } from './lib/format';
import { Status } from './lib/schemas';
import { useLocalState } from './lib/storage';
import { useThemeMode, type ThemeMode } from './lib/theme';
import { useResource } from './lib/useResource';
import { invalidateSnapshotViews } from './lib/jobs';
import { Button, ConfirmDialog, Emblem, Notice, Pill, ToastProvider, cn, useToast } from './components/ui';
import { Crew } from './components/crew';
import { Welcome } from './components/welcome';
import { openCrew, showWelcomeOnLaunch } from './lib/welcome';
import { CommandPalette, ErrorBoundary, HelpDialog } from './components/shell-dialogs';
import { CREW_PROMPTS } from './lib/crew';
import type { Command } from './lib/commands';
import { GO_KEYS } from './lib/shortcuts';
import { useShortcuts } from './lib/use-shortcuts';
import { describeError } from './lib/api';

// Each page is its own chunk, loaded when it is first opened (build.mjs splits the bundle).
const ConnectionPage = lazy(() => import('./pages/connection').then((m) => ({ default: m.ConnectionPage })));
const DiagnosticsPage = lazy(() => import('./pages/diagnostics').then((m) => ({ default: m.DiagnosticsPage })));
const HierarchyPage = lazy(() => import('./pages/hierarchy').then((m) => ({ default: m.HierarchyPage })));
const IntakePage = lazy(() => import('./pages/intake').then((m) => ({ default: m.IntakePage })));
const LeadershipPage = lazy(() => import('./pages/leadership').then((m) => ({ default: m.LeadershipPage })));
const OverviewPage = lazy(() => import('./pages/overview').then((m) => ({ default: m.OverviewPage })));
const PlanPage = lazy(() => import('./pages/plan').then((m) => ({ default: m.PlanPage })));
const ProposalsPage = lazy(() => import('./pages/proposals').then((m) => ({ default: m.ProposalsPage })));
const ReviewPage = lazy(() => import('./pages/review').then((m) => ({ default: m.ReviewPage })));
const SandboxPage = lazy(() => import('./pages/sandbox').then((m) => ({ default: m.SandboxPage })));
const SettingsPage = lazy(() => import('./pages/settings').then((m) => ({ default: m.SettingsPage })));
const VisionPage = lazy(() => import('./pages/vision').then((m) => ({ default: m.VisionPage })));

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

/**
 * When the engine's snapshot changes (a sync, or a seed/teardown/apply that resynchronized), every page derived
 * from the old snapshot is refetched. Without this a page opened while a write job was still running kept the
 * pre-write data, because the job's poller lives on the page that started it.
 */
function useSnapshotChangeRefresh(snapshotId: string | null) {
  const seen = useRef<string | null | undefined>(undefined);
  useEffect(() => {
    if (seen.current !== undefined && seen.current !== snapshotId) invalidateSnapshotViews();
    seen.current = snapshotId;
  }, [snapshotId]);
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
  useSnapshotChangeRefresh(status.data?.snapshot?.id ?? null);
  const toast = useToast();
  const [confirmStop, setConfirmStop] = useState(false);
  const [stopped, setStopped] = useState(false);
  const [collapsed, setCollapsed] = useLocalState('sidebar-collapsed', false);
  const [skipWelcome, setSkipWelcome] = useLocalState('skip-welcome', false);
  // The welcome page shows on launch (not on a deep link) and comes back from the emblem in the sidebar.
  const [welcome, setWelcome] = useState(() => showWelcomeOnLaunch(window.location.hash, skipWelcome));
  useEffect(() => {
    const leave = () => setWelcome(false);
    window.addEventListener('hashchange', leave);
    return () => window.removeEventListener('hashchange', leave);
  }, []);

  useEffect(() => startHeartbeat(), []);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const go = useCallback((r: Route) => { setWelcome(false); navigate(r); }, []);
  useShortcuts({ onPalette: () => setPaletteOpen(true), onHelp: () => setHelpOpen(true), onTalk: () => openCrew(undefined, true), onGo: go });

  // The window title names the page: "Proposals · Halyard" (the launcher finds the window by it).
  useEffect(() => {
    const label = welcome ? null : NAV.find((n) => n.route === route)?.label;
    document.title = label ? `${label} \u00b7 Halyard` : 'Halyard';
  }, [route, welcome]);

  const commands = useMemo<Command[]>(() => {
    const goKey = (r: Route) => Object.entries(GO_KEYS).find(([, v]) => v === r)?.[0];
    return [
      { id: 'crew:talk', label: 'Talk to the crew', group: 'Crew', keys: ['.'], keywords: 'voice speak listen orb', run: () => openCrew(undefined, true) },
      ...CREW_PROMPTS.map((p) => ({ id: `crew:${p}`, label: p, group: 'Crew' as const, keywords: 'crew scrum product owner', run: () => openCrew(p) })),
      ...NAV.map((n) => { const k = goKey(n.route); return { id: `go:${n.route}`, label: n.label, group: 'Go to' as const, keys: k ? ['g', k] : undefined, run: () => go(n.route) }; }),
      { id: 'backlog:sync', label: 'Synchronize the backlog', group: 'Backlog', keywords: 'sync pull refresh snapshot', run: () => { post('/api/sync', { repository: 'source' }).then(() => toast.push('info', 'Synchronizing. The snapshot updates when it finishes.')).catch((e) => toast.push('err', describeError(e))); } },
      { id: 'view:welcome', label: 'Show the welcome page', group: 'View', keywords: 'home start', run: () => setWelcome(true) },
      { id: 'view:sidebar', label: 'Collapse or expand the sidebar', group: 'View', run: () => setCollapsed((c) => !c) },
      { id: 'view:help', label: 'Keyboard shortcuts and help', group: 'View', keys: ['?'], keywords: 'keys', run: () => setHelpOpen(true) },
      { id: 'session:end', label: 'End session', group: 'Session', keywords: 'quit stop exit close', run: () => setConfirmStop(true) },
    ];
  }, [go, toast, setCollapsed]);
  const dialogs = (
    <>
      <CommandPalette open={paletteOpen} commands={commands} onClose={() => setPaletteOpen(false)} />
      <HelpDialog open={helpOpen} onClose={() => setHelpOpen(false)} onPalette={() => setPaletteOpen(true)} onWelcome={() => setWelcome(true)} version={status.data?.version} />
    </>
  );

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
  if (welcome) {
    return (
      <div className="h-screen">
        <Welcome status={s} skipNextTime={skipWelcome} onSkipNextTime={setSkipWelcome} onGo={go} />
        <Crew />
        {dialogs}
      </div>
    );
  }
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
          <button type="button" onClick={() => setWelcome(true)} title="Welcome page" className="rounded-panel"><Emblem size={168} /></button>
          <div className="text-[12px] text-ink-3">Delivery intelligence</div>
        </div>
        <div className={cn('justify-center py-4', collapsed ? 'flex' : 'hidden max-lg:flex')}>
          <button type="button" onClick={() => setWelcome(true)} title="Welcome page" className="rounded-panel"><img src="brand/icon-256.png" alt="Halyard" width={40} height={40} /></button>
        </div>
        <div className="scroll-y min-h-0 flex-1">
          <NavGroup label="Backlog" items={NAV.filter((n) => n.group === 'work')} route={route} collapsed={collapsed} />
          <NavGroup label="Operate" items={NAV.filter((n) => n.group === 'operate')} route={route} collapsed={collapsed} />
        </div>
        <div className="flex flex-col gap-1 border-t border-line-2 px-2 py-2">
          <Button variant="quiet" className={cn('h-8 justify-start px-3 text-ink-2', centred)} onClick={() => setHelpOpen(true)} title="Help and keyboard shortcuts (?)">
            <LuCircleHelp size={16} /><span className={label}>Help</span><kbd className={cn('ml-auto rounded border border-line px-1 font-mono text-[10.5px] text-ink-3', label)}>Ctrl K</kbd>
          </Button>
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
        <main className="scroll-y min-h-0 min-w-0 flex-1"><ErrorBoundary key={route} label={route}><Suspense fallback={<div className="px-6 py-5 text-ink-3" role="status">Loading…</div>}>{page}</Suspense></ErrorBoundary></main>
      </div>
      <StatusBar status={s} error={status.error} />
      <Crew />
      {dialogs}
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

const THEMES: Array<{ value: ThemeMode; label: string; icon: ReactNode }> = [
  { value: 'system', label: 'System theme', icon: <LuMonitor size={16} /> },
  { value: 'dark', label: 'Night watch', icon: <LuMoon size={16} /> },
  { value: 'light', label: 'Chart room', icon: <LuSun size={16} /> },
];

function ThemeSwitch({ className, labelClass }: { className: string; labelClass: string }) {
  const [pref, setPref] = useThemeMode();
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
