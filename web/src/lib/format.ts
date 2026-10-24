export function relativeTime(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return '—';
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '—';
  const s = Math.round((now - t) / 1000);
  if (s < 5) return 'just now';
  if (s < 60) return `${s}s ago`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m} min ago`;
  const h = Math.round(m / 60);
  if (h < 48) return `${h} h ago`;
  const d = Math.round(h / 24);
  return `${d} days ago`;
}

export function ageLabel(seconds: number): string {
  if (seconds < 60) return 'under a minute old';
  if (seconds < 3600) return `${Math.round(seconds / 60)} min old`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)} h old`;
  return `${Math.round(seconds / 86400)} days old`;
}

export function shortDate(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit' });
}

export function pct(part: number, whole: number): string {
  if (!whole) return '0%';
  return `${Math.round((part / whole) * 100)}%`;
}

export function plural(n: number, one: string, many = one + 's'): string {
  return `${n} ${n === 1 ? one : many}`;
}

/** Turns a stable code like story_missing_acceptance_criteria or PARENT_NOT_DECLARED into readable words. */
export function humanizeCode(code: string): string {
  const words = code.toLowerCase().split(/[_\-\s]+/).filter(Boolean);
  if (words.length === 0) return code;
  const first = words[0]!;
  return first.charAt(0).toUpperCase() + first.slice(1) + (words.length > 1 ? ' ' + words.slice(1).join(' ') : '');
}

export function truncate(text: string, max = 80): string {
  return text.length <= max ? text : text.slice(0, max - 1) + '…';
}

/** CSV with RFC 4180 quoting; used for review-pack exports. */
export function toCsv(rows: Array<Record<string, unknown>>, columns: string[]): string {
  const esc = (v: unknown) => {
    const s = v === null || v === undefined ? '' : Array.isArray(v) ? v.join('; ') : String(v);
    return /[",\n\r]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
  };
  return [columns.join(','), ...rows.map((r) => columns.map((c) => esc(r[c])).join(','))].join('\r\n');
}

export function downloadText(filename: string, text: string, type = 'text/plain') {
  const blob = new Blob([text], { type });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
