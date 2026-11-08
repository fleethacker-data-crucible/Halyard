// The landing page (UNIFIED-UI 6, .fl-empty), as Semaphore has it: the full emblem, one headline and the ways in. It shows
// when Halyard opens and gives way to the pages as soon as the person picks something. The primary way in follows the
// engine's state (continue with the snapshot, seed the sandbox, or connect); the crew is one click away.
import { useState, type ReactNode } from 'react';
import { LuArrowRightToLine, LuClipboardCheck, LuCompass, LuMessagesSquare, LuPlug, LuPresentation, LuSparkles, LuSunrise, LuTestTubeDiagonal } from 'react-icons/lu';
import type { Route } from '../app';
import type { Status } from '../lib/schemas';
import { queueCrew, welcomePrimary } from '../lib/welcome';
import { Emblem } from './ui';

export function Welcome({ status, onGo, skipNextTime, onSkipNextTime }: {
  status: Status | undefined;
  onGo: (route: Route) => void;
  skipNextTime: boolean;
  onSkipNextTime: (skip: boolean) => void;
}) {
  const [skip, setSkip] = useState(skipNextTime);
  const primary = welcomePrimary(status);
  const hasSnapshot = !!status?.snapshot;
  // Leaving the welcome page swaps the layout, so the request waits for the crew panel there.
  const crew = (message?: string) => { queueCrew(message); onGo(hasSnapshot ? 'overview' : primary.route); };
  const secondary = 'inline-flex items-center gap-1.5 rounded-ctl border border-line bg-surface px-3.5 py-2 text-[13px] font-medium text-ink hover:border-accent';
  const way = (icon: ReactNode, label: string, go: () => void, show = true) => (show ? <button type="button" className={secondary} onClick={go}>{icon}{label}</button> : null);
  const primaryIcon = primary.kind === 'seed' ? <LuTestTubeDiagonal size={17} /> : primary.kind === 'connect' ? <LuPlug size={17} /> : <LuCompass size={17} />;
  return (
    // Centred with auto margins, not justify-center: on a short window the top stays reachable by scrolling.
    <main aria-labelledby="welcome-title" className="flex h-full flex-col overflow-auto px-4 py-8 text-center">
      <div className="my-auto flex flex-col items-center gap-4">
        <div className="welcome-emblem"><Emblem size={380} /></div>
        <h1 id="welcome-title" className="font-display text-[28px] leading-tight font-semibold text-ink">Raise the sails.</h1>
        <p className="max-w-[60ch] text-[14px] text-ink-2">
          Halyard works your backlog up to leadership and down to stories and tasks, on this computer. Its crew, a product owner
          and scrum master, runs standup, gets work ready and plans the sprint. It proposes; you approve.
        </p>
        <button
          type="button"
          autoFocus
          disabled={primary.kind === 'waiting'}
          onClick={() => onGo(primary.route)}
          className="inline-flex max-w-full items-center gap-2 rounded-ctl bg-accent px-5 py-2.5 text-[15px] font-semibold text-accent-ink hover:brightness-110 disabled:opacity-60"
        >
          {primaryIcon}<span className="truncate">{primary.label}</span>
        </button>
        <div className="flex max-w-[760px] flex-wrap justify-center gap-2">
          {way(<LuMessagesSquare size={16} />, 'Talk to the crew', () => crew())}
          {way(<LuSunrise size={16} />, 'Run standup', () => crew('Run standup'), hasSnapshot)}
          {way(<LuClipboardCheck size={16} />, 'Review the backlog', () => onGo('review'), hasSnapshot)}
          {way(<LuSparkles size={16} />, status && status.proposals > 0 ? `Proposals (${status.proposals})` : 'Proposals', () => onGo('proposals'), hasSnapshot)}
          {way(<LuPresentation size={16} />, 'Leadership view', () => onGo('leadership'), hasSnapshot)}
          {way(<LuArrowRightToLine size={16} />, 'Intake tickets', () => onGo('intake'), hasSnapshot)}
          {way(<LuTestTubeDiagonal size={16} />, 'Sandbox', () => onGo('sandbox'), primary.kind !== 'seed')}
          {way(<LuPlug size={16} />, 'Connection', () => onGo('connection'), primary.kind !== 'connect')}
        </div>
        <label className="mt-1 flex items-center gap-2 text-[12px] text-ink-3">
          <input type="checkbox" checked={skip} onChange={(e) => { setSkip(e.target.checked); onSkipNextTime(e.target.checked); }} />
          Open straight to the overview next time
        </label>
      </div>
    </main>
  );
}
