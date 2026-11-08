// Fairlead from the page: every model call goes through it (fleet contract 1). When the engine refuses a model job because
// Fairlead is not answering (code FAIRLEAD_OFFLINE), the shell shows one banner with a Start Fairlead button instead of each
// page reporting its own failure. Starting runs Fairlead's own tray app through the engine; the page then asks until it answers.
import { useSyncExternalStore } from 'react';
import { z } from 'zod';
import { get, post } from './api';

export const FairleadState = z.object({
  state: z.enum(['ready', 'offline', 'starting', 'missing']),
  url: z.string().optional(),
  canStart: z.boolean(),
  message: z.string(),
});
export type FairleadState = z.infer<typeof FairleadState>;

export const FAIRLEAD_OFFLINE = 'FAIRLEAD_OFFLINE';
/** How long to wait for a started Fairlead to answer before saying so. */
export const START_WAIT_MS = 45_000;

type Banner = { show: boolean; starting: boolean; message: string; canStart: boolean };
let banner: Banner = { show: false, starting: false, message: '', canStart: true };
const listeners = new Set<() => void>();
function set(next: Partial<Banner>) { banner = { ...banner, ...next }; listeners.forEach((l) => l()); }

/** Called by the API client when a request fails with FAIRLEAD_OFFLINE. */
export function reportFairleadOffline(message: string): void {
  if (!banner.starting) set({ show: true, message });
  void get<unknown>('/api/fairlead').then((raw) => {
    const s = FairleadState.safeParse(raw);
    if (s.success && s.data.state !== 'ready') set({ canStart: s.data.canStart });
  }).catch(() => undefined);
}

export function dismissFairleadBanner(): void { set({ show: false }); }

export function useFairleadBanner(): Banner {
  return useSyncExternalStore((l) => { listeners.add(l); return () => listeners.delete(l); }, () => banner, () => banner);
}

const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));

/**
 * Asks the engine to start Fairlead, then waits until it answers. Resolves with the last state: 'ready' when it came up,
 * otherwise the reason (missing, still offline after the wait).
 */
export async function startFairlead(onProgress?: (s: FairleadState) => void, waitMs = START_WAIT_MS, pollMs = 1000): Promise<FairleadState> {
  set({ starting: true, message: 'Starting Fairlead…' });
  try {
    let s = FairleadState.parse(await post('/api/fairlead/start', {}));
    onProgress?.(s);
    const until = Date.now() + waitMs;
    while (s.state === 'starting' || (s.state === 'offline' && s.canStart && Date.now() < until)) {
      if (Date.now() >= until) break;
      await wait(pollMs);
      s = FairleadState.parse(await get('/api/fairlead'));
      onProgress?.(s);
    }
    if (s.state === 'ready') set({ show: false, starting: false });
    else set({ show: true, starting: false, canStart: s.canStart, message: s.state === 'missing' ? s.message : 'Fairlead did not answer yet. Check its tray icon, or start it with Launch-Fairlead.bat.' });
    return s;
  } catch (e) {
    set({ starting: false });
    throw e;
  }
}
