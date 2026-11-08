// What the welcome page offers first, from the engine's status. Pure, so it is tested without a browser.
// The same rule as Semaphore's welcome: one primary way in that fits where the person is, the rest secondary.
import type { Status } from './schemas';

export type WelcomePrimary =
  | { kind: 'continue'; label: string; route: 'overview' }
  | { kind: 'seed'; label: string; route: 'sandbox' }
  | { kind: 'connect'; label: string; route: 'connection' }
  | { kind: 'waiting'; label: string; route: 'overview' };

export function welcomePrimary(status: Status | undefined): WelcomePrimary {
  if (!status) return { kind: 'waiting', label: 'Starting the engine…', route: 'overview' };
  if (status.snapshot) {
    const repo = status.snapshot.repository || 'the backlog';
    return { kind: 'continue', label: `Continue with ${repo} (${status.snapshot.count} items)`, route: 'overview' };
  }
  if (status.config.sandboxRepository) return { kind: 'seed', label: 'Seed the sandbox and look around (about a minute)', route: 'sandbox' };
  return { kind: 'connect', label: 'Connect a repository', route: 'connection' };
}

/** The welcome shows on launch unless the person turned it off, or the link already names a page (a deep link). */
export function showWelcomeOnLaunch(hash: string, skip: boolean): boolean {
  return !skip && hash.replace(/^#\/?/, '').length === 0;
}

export type CrewRequest = { message?: string; talk?: boolean };
let pendingCrew: CrewRequest | null = null;

/**
 * Ask the crew panel to open, optionally with a message to send. A panel that is mounted hears the event; one that mounts
 * a moment later (leaving the welcome page swaps the layout) takes the request with takeCrewRequest.
 */
export function openCrew(message?: string, talk = false) {
  pendingCrew = { message, talk };
  window.dispatchEvent(new CustomEvent('halyard:crew', { detail: { message, talk } }));
}

/** Leave a crew request for the panel that mounts next, without telling the one mounted now (it is about to go). */
export function queueCrew(message?: string) {
  pendingCrew = { message };
}

/** The crew request not yet handled, once: the panel calls this when it opens or mounts. */
export function takeCrewRequest(): CrewRequest | null {
  const r = pendingCrew;
  pendingCrew = null;
  return r;
}
