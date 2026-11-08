// One banner for the whole app when a model job was refused because Fairlead is not running, with a button that starts it.
import { useState } from 'react';
import { LuPlay, LuX } from 'react-icons/lu';
import { describeError } from '../lib/api';
import { dismissFairleadBanner, startFairlead, useFairleadBanner } from '../lib/fairlead';
import { Button, Notice } from './ui';

export function FairleadBanner() {
  const b = useFairleadBanner();
  const [error, setError] = useState<string | null>(null);
  if (!b.show) return null;
  const start = async () => {
    setError(null);
    try {
      const s = await startFairlead();
      if (s.state === 'ready') setError(null);
    } catch (e) { setError(describeError(e)); }
  };
  return (
    <Notice tone="warn" className="mx-6 mt-3">
      <div role="alert" className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <span className="min-w-0 flex-1">{b.starting ? 'Starting Fairlead… model calls work again once its tray icon appears.' : b.message}{error ? <span className="block text-err">{error}</span> : null}</span>
        {b.canStart ? <Button size="sm" variant="primary" busy={b.starting} onClick={() => void start()}><LuPlay size={13} />Start Fairlead</Button> : null}
        <Button size="sm" variant="quiet" aria-label="Dismiss" title="Dismiss" onClick={dismissFairleadBanner}><LuX size={14} /></Button>
      </div>
    </Notice>
  );
}
