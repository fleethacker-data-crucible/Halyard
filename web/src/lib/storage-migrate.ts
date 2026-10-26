// One-time copy-forward of browser storage from the name before the rename (previous name -> Halyard).
// Keys `cascade.<x>` are copied to `halyard.<x>` when the new key is missing. The old keys are left in place
// (UNIFIED-UI §9: copy forward, leave the original untouched), and a marker key makes later runs a no-op.

export const PREFIX = 'halyard.';
export const LEGACY_PREFIX = 'cascade.';
export const MIGRATED_KEY = PREFIX + 'migrated-from-cascade';

/** The subset of the Web Storage API the migration needs, so tests can pass a plain fake. */
export interface KeyValueStorage {
  readonly length: number;
  key(index: number): string | null;
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

/**
 * Copies every `cascade.*` key to `halyard.*` unless the new key already exists. Returns the number of keys
 * copied. Idempotent: runs once, then only checks the marker. Never throws (storage may be blocked or full).
 */
export function migrateLegacyStorage(storage: KeyValueStorage | null | undefined): number {
  if (!storage) return 0;
  try {
    if (storage.getItem(MIGRATED_KEY) !== null) return 0;
    const legacy: string[] = [];
    for (let i = 0; i < storage.length; i++) {
      const k = storage.key(i);
      if (k !== null && k.startsWith(LEGACY_PREFIX)) legacy.push(k);
    }
    let copied = 0;
    for (const oldKey of legacy) {
      const newKey = PREFIX + oldKey.slice(LEGACY_PREFIX.length);
      if (storage.getItem(newKey) !== null) continue;
      const value = storage.getItem(oldKey);
      if (value === null) continue;
      storage.setItem(newKey, value);
      copied++;
    }
    storage.setItem(MIGRATED_KEY, new Date().toISOString());
    return copied;
  } catch {
    return 0;
  }
}
