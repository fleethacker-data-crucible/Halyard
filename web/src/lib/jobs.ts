// Background jobs: start one, poll it until it finishes, surface progress to the page.
import { useCallback, useEffect, useRef, useState } from 'react';
import { describeError, get, post } from './api';
import { Job } from './schemas';
import { invalidate } from './useResource';

/** Everything derived from the snapshot. A write job re-synchronizes inside itself and reports `resynced`. */
export const SNAPSHOT_VIEWS = ['/api/snapshot', '/api/status', '/api/review', '/api/hierarchy', '/api/item', '/api/leadership', '/api/intake', '/api/vision', '/api/team', '/api/sandbox'];

/**
 * Drop every cached view built from an older snapshot. The shell calls this when /api/status reports a new
 * snapshot id, so a write job that finished after its page was left (polling stops on unmount) still refreshes
 * the other pages. `/api/status` itself is kept: it is the poll that noticed the change.
 */
export function invalidateSnapshotViews(): void {
  for (const k of SNAPSHOT_VIEWS) if (k !== '/api/status') invalidate(k);
}

export function resyncedBy(job: Job): boolean {
  const r = job.result as { resynced?: unknown } | null | undefined;
  return !!r && r.resynced === true;
}

export type StartedJob = { job: Job } & Record<string, unknown>;

export function useJob(onFinished?: (job: Job) => void) {
  const [job, setJob] = useState<Job | null>(null);
  const [error, setError] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const finishedRef = useRef(onFinished);
  finishedRef.current = onFinished;

  const stop = useCallback(() => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
  }, []);

  const poll = useCallback(async (id: string) => {
    try {
      const raw = await get<unknown>(`/api/jobs/${id}`);
      const parsed = Job.parse(raw);
      setJob(parsed);
      if (parsed.state === 'succeeded' || parsed.state === 'failed' || parsed.state === 'cancelled') {
        stop();
        if (parsed.state === 'succeeded' && resyncedBy(parsed)) for (const k of SNAPSHOT_VIEWS) invalidate(k);
        finishedRef.current?.(parsed);
        return;
      }
      timer.current = setTimeout(() => void poll(id), 900);
    } catch (e) {
      setError(describeError(e));
      stop();
    }
  }, [stop]);

  const start = useCallback(async (path: string, body?: unknown) => {
    setError(null);
    setJob(null);
    stop();
    try {
      const started = await post<StartedJob | Job>(path, body);
      const j = 'job' in started && started.job ? Job.parse(started.job) : Job.parse(started);
      setJob(j);
      void poll(j.id);
      return started;
    } catch (e) {
      setError(describeError(e));
      return null;
    }
  }, [poll, stop]);

  const cancel = useCallback(async () => {
    if (!job) return;
    try {
      await post(`/api/jobs/${job.id}/cancel`);
    } catch (e) {
      setError(describeError(e));
    }
  }, [job]);

  useEffect(() => stop, [stop]);

  const running = job !== null && (job.state === 'running' || job.state === 'queued');
  return { job, error, running, start, cancel, reset: () => { stop(); setJob(null); setError(null); } };
}
