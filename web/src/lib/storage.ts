// Per-viewer conveniences only (expanded tree nodes, last view). Browser storage can be empty, blocked or
// throw (private windows, cleared site data), so every access is guarded and the UI never depends on it.
import { useCallback, useState } from 'react';

// Kept from before the rename (Cascade -> Halyard): renaming the prefix would orphan what viewers already saved.
const PREFIX = 'cascade.';

export function readLocal<T>(key: string, fallback: T): T {
  try {
    const raw = window.localStorage.getItem(PREFIX + key);
    return raw === null ? fallback : (JSON.parse(raw) as T);
  } catch {
    return fallback;
  }
}

export function writeLocal<T>(key: string, value: T): void {
  try {
    window.localStorage.setItem(PREFIX + key, JSON.stringify(value));
  } catch {
    // ignore: storage unavailable
  }
}

export function useLocalState<T>(key: string, fallback: T): [T, (next: T | ((prev: T) => T)) => void] {
  const [value, setValue] = useState<T>(() => readLocal(key, fallback));
  const set = useCallback((next: T | ((prev: T) => T)) => {
    setValue((prev) => {
      const v = typeof next === 'function' ? (next as (p: T) => T)(prev) : next;
      writeLocal(key, v);
      return v;
    });
  }, [key]);
  return [value, set];
}
