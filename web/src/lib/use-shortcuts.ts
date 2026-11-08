// The app-wide keyboard (lib/shortcuts.ts holds the table and the rules). One listener for the page's lifetime that reads the
// latest handlers. "g" waits up to a second for the page letter.
import { useEffect, useRef } from 'react';
import type { Route } from '../app';
import { isTyping, shortcutFor } from './shortcuts';

export interface ShortcutHandlers {
  onPalette: () => void;
  onHelp: () => void;
  onTalk: () => void;
  onGo: (route: Route) => void;
}

const dialogOpen = () => !!document.querySelector('dialog[open]');

export function useShortcuts(h: ShortcutHandlers) {
  const ref = useRef(h);
  ref.current = h;
  useEffect(() => {
    let pendingGo = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const onKey = (e: KeyboardEvent) => {
      if (e.defaultPrevented) return;
      const action = shortcutFor(e, { typing: isTyping(e.target), dialogOpen: dialogOpen(), pendingGo });
      const wasPending = pendingGo;
      pendingGo = false;
      clearTimeout(timer);
      if (!action) return;
      if (action.kind === 'prefix') { pendingGo = true; timer = setTimeout(() => { pendingGo = false; }, 1000); return; }
      e.preventDefault();
      if (action.kind === 'palette') ref.current.onPalette();
      else if (action.kind === 'help') ref.current.onHelp();
      else if (action.kind === 'talk') ref.current.onTalk();
      else if (action.kind === 'go' && wasPending) ref.current.onGo(action.route);
    };
    window.addEventListener('keydown', onKey);
    return () => { window.removeEventListener('keydown', onKey); clearTimeout(timer); };
  }, []);
}
