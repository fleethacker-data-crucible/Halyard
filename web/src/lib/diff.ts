// Line diff for proposal previews. Plain LCS on trimmed-right lines: small inputs (issue bodies), no
// dependency, deterministic. 'same' runs are collapsed by the renderer, not here.
export type DiffOp = { kind: 'same' | 'add' | 'del'; text: string };

export function diffLines(before: string, after: string): DiffOp[] {
  const a = split(before);
  const b = split(after);
  const n = a.length, m = b.length;
  // lcs[i][j] = length of LCS of a[i..] and b[j..]
  const lcs: Uint16Array[] = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
  for (let i = n - 1; i >= 0; i--) {
    const row = lcs[i]!, next = lcs[i + 1]!;
    for (let j = m - 1; j >= 0; j--) {
      row[j] = a[i] === b[j] ? next[j + 1]! + 1 : Math.max(next[j]!, row[j + 1]!);
    }
  }
  const out: DiffOp[] = [];
  let i = 0, j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) { out.push({ kind: 'same', text: a[i]! }); i++; j++; }
    else if (lcs[i + 1]![j]! >= lcs[i]![j + 1]!) { out.push({ kind: 'del', text: a[i]! }); i++; }
    else { out.push({ kind: 'add', text: b[j]! }); j++; }
  }
  while (i < n) out.push({ kind: 'del', text: a[i++]! });
  while (j < m) out.push({ kind: 'add', text: b[j++]! });
  return out;
}

function split(text: string): string[] {
  if (!text) return [];
  return text.replace(/\r\n/g, '\n').split('\n').map((l) => l.trimEnd());
}

export function diffStats(ops: DiffOp[]): { added: number; removed: number; unchanged: number } {
  let added = 0, removed = 0, unchanged = 0;
  for (const op of ops) {
    if (op.kind === 'add') added++;
    else if (op.kind === 'del') removed++;
    else unchanged++;
  }
  return { added, removed, unchanged };
}

/** Groups ops into hunks: changed runs with up to `context` unchanged lines around them; long same-runs are folded. */
export type DiffRow = DiffOp | { kind: 'fold'; count: number };
export function foldUnchanged(ops: DiffOp[], context = 2): DiffRow[] {
  const keep = new Array<boolean>(ops.length).fill(false);
  ops.forEach((op, idx) => {
    if (op.kind === 'same') return;
    for (let k = Math.max(0, idx - context); k <= Math.min(ops.length - 1, idx + context); k++) keep[k] = true;
  });
  const rows: DiffRow[] = [];
  let folded = 0;
  ops.forEach((op, idx) => {
    if (keep[idx]) {
      if (folded > 0) { rows.push({ kind: 'fold', count: folded }); folded = 0; }
      rows.push(op);
    } else folded++;
  });
  if (folded > 0) rows.push({ kind: 'fold', count: folded });
  return rows;
}
