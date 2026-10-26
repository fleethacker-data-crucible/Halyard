// In-memory fake of the GitHub REST/GraphQL endpoints Halyard uses, plus an OpenAI-compatible chat
// endpoint. Lets the full seed -> sync -> propose -> apply -> teardown loop run end to end without a
// token or network access. Node only, so CI needs nothing beyond the Node it already sets up.
//
//   node tests/fakes/fake-github.mjs 8790
//
// Then point Halyard at it: GITHUB_API_URL=http://127.0.0.1:8790, GITHUB_TOKEN=fake,
// LLM_URL=http://127.0.0.1:8790/v1/chat/completions, LLM_TOKEN=fake.
import { createServer } from 'node:http';

const port = Number(process.argv[2] ?? 8790);
const SANDBOX = 'fleethacker-data-crucible/Halyard-Ballast';
// One issue store per repository, so a source backlog and the sandbox can coexist (intake copies between them).
const repos = new Map();
function repoState(repo) {
  const key = repo.toLowerCase();
  if (!repos.has(key)) repos.set(key, { issues: new Map(), labels: new Map(), next: 1, subIssues: [] });
  return repos.get(key);
}

function issueJson(repo, n, issue) {
  return {
    number: n,
    id: 100000 + n,
    node_id: `I_${repo}_${n}`,
    title: issue.title,
    body: issue.body,
    state: issue.state,
    labels: issue.labels.map((name) => ({ name })),
    assignees: [],
    html_url: `https://github.com/${repo}/issues/${n}`,
    created_at: '2026-10-24T12:00:00Z',
    updated_at: issue.updatedAt ?? '2026-10-24T12:00:00Z',
    milestone: null,
  };
}

function repoId(repo) {
  let h = 0;
  for (const ch of repo.toLowerCase()) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return h % 1e9;
}

function send(res, status, payload, headers = {}) {
  const body = Buffer.from(JSON.stringify(payload));
  res.writeHead(status, { 'Content-Type': 'application/json', 'Content-Length': body.length, ...headers });
  res.end(body);
}

function readBody(req) {
  return new Promise((resolve) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => {
      const text = Buffer.concat(chunks).toString('utf8');
      try { resolve(text ? JSON.parse(text) : {}); } catch { resolve({}); }
    });
  });
}

function refine(user) {
  // Echo the current narrative and append the sections the gaps ask for, the way a cooperative model would.
  const start = user.indexOf('Current narrative');
  let current = start >= 0 ? user.slice(user.indexOf('\n', start) + 1) : '';
  current = current.replace(/\n\nRespond with the JSON object only\.\s*$/, '').trim();
  if (current === '(empty)') current = '';
  let add = '';
  if (user.includes('story_missing_user_need')) add += '\n\n## User need\nAs a reviewer, I want this gap closed so that the story is ready for a sprint.';
  if (user.includes('story_missing_acceptance_criteria')) add += '\n\n## Acceptance criteria\n- [ ] One positive case passes end to end\n- [ ] One negative case is rejected with a clear message\n- [ ] Evidence is attached to the story';
  if (user.includes('story_missing_tasks')) add += '\n\n## Tasks\n- [ ] Implement the slice\n- [ ] Demonstrate both cases\n- [ ] Attach evidence';
  if (user.includes('feature_missing_done_when')) add += '\n\n## Definition of done\n- [ ] Every child story is accepted\n- [ ] The capability is demonstrated in the sandbox';
  if (user.includes('feature_missing_short_description')) add += '\n\n## Description\nDelivers the capability named in the title for the parent epic (to confirm).';
  if (user.includes('epic_missing_outcome_statement')) add += '\n\n## Outcome\nThe capability is live, measured and relied upon by its features.';
  if (user.includes('epic_missing_success_picture')) add += '\n\n## Success picture\n- Teams can see the outcome\n- Nothing regresses\n- The next epic can start';
  if (user.includes('theme_missing_purpose')) add += '\n\n## Purpose\nWhy this lane exists (to confirm).';
  if (user.includes('theme_missing_scope')) add += '\n\n## In scope\n- Platform work\n\n## Out of scope\n- Product-owned reports';
  return { narrative: current + add, note: 'Added the missing sections; kept the existing text.' };
}

function vision(user) {
  // A grounded vision: keep every epic under its current theme, rank epics by order of appearance.
  const themes = [...user.matchAll(/^- (THEME-[a-z0-9-]+): ([^\n—]+)/gm)].map((m) => ({ id: m[1], title: m[2].trim() }));
  const epics = [...user.matchAll(/^- (EPIC-[a-z0-9-]+): ([^\[\n]+)\[([^\]]+)\]/gm)].map((m) => ({ id: m[1], title: m[2].trim(), theme: m[3].trim() }));
  return {
    vision: 'A data platform teams can rely on: trusted ingestion, secure foundations and observable services, delivered in small verified steps.',
    themes: themes.map((t) => ({ id: t.id, title: t.title, purpose: `Why ${t.title} exists: shared capability every product epic depends on.`, epics: epics.filter((e) => e.theme === t.id).map((e) => e.id) })),
    priorities: epics.slice(0, 5).map((e, i) => ({ rank: i + 1, epic: e.id, why: `${e.title} unblocks the most downstream features.` })),
    risks: ['Escalated blockers on identity work', 'No one on the team holds streaming-data skills'],
    nextQuarter: ['Ingestion contracts live for every source feed', 'Identity and access baseline complete', 'On-call alerting trusted enough to page'],
    narrative: '## Vision\nA data platform teams can rely on.\n\n## Strategic themes\n' + themes.map((t) => `### ${t.title}\nShared capability.`).join('\n\n') + '\n\n## Priorities\n' + epics.slice(0, 5).map((e, i) => `${i + 1}. **${e.title}**`).join('\n') + '\n\n## Risks\n- Escalated blockers\n\n## Next quarter\n- Ingestion contracts live',
  };
}

function chat(data, res) {
  const messages = data.messages ?? [];
  const prompt = messages.map((m) => m.content ?? '').join('\n');
  if (prompt.includes('portfolio vision')) {
    const user = messages.filter((m) => m.role === 'user').map((m) => m.content ?? '').join('\n');
    return send(res, 200, { choices: [{ message: { content: '```json\n' + JSON.stringify(vision(user)) + '\n```' } }], usage: { prompt_tokens: 1000, completion_tokens: 400 } });
  }
  if (prompt.includes('tagging backlog items with the skills')) {
    const ids = [...prompt.matchAll(/### id: (\S+)/g)].map((m) => m[1]);
    const pool = ['sql-pipelines', 'data-quality', 'identity-access', 'ci-cd-pipelines', 'monitoring-alerting', 'technical-writing'];
    const out = ids.map((id, i) => ({ id, skills: [pool[i % pool.length], pool[(i + 1) % pool.length]], confidence: 'medium', reason: 'fake model' }));
    return send(res, 200, { choices: [{ message: { content: JSON.stringify(out) } }], usage: { prompt_tokens: 500, completion_tokens: 100 } });
  }
  if (prompt.includes('refinement assistant')) {
    const user = messages.filter((m) => m.role === 'user').map((m) => m.content ?? '').join('\n');
    return send(res, 200, { choices: [{ message: { content: JSON.stringify(refine(user)) } }], usage: { prompt_tokens: Math.floor(prompt.length / 4), completion_tokens: 200 } });
  }
  const ids = [...prompt.matchAll(/### id: (\S+)/g)].map((m) => m[1]);
  const effort = prompt.includes('FEATURE below');
  const out = ids.map((id, i) => effort
    ? { id, effort: ['S', 'M', 'L', 'XL'][i % 4], confidence: 'high', reason: 'fake model' }
    : { id, points: [3, 5, 13, 2][i % 4], confidence: ['high', 'medium'][i % 2], reason: 'fake model' });
  send(res, 200, { choices: [{ message: { content: '```json\n' + JSON.stringify(out) + '\n```' } }], usage: { prompt_tokens: Math.floor(prompt.length / 4), completion_tokens: 50 } });
}

function graphql(data, res) {
  const node = data.variables?.id ?? '';
  // node ids are "I_<repo>_<n>" so a node resolves to exactly one repository
  const mm = /^I_(.+)_(\d+)$/.exec(node);
  const owner = mm ? [...repos.entries()].find(([k]) => k === mm[1].toLowerCase()) : null;
  const n = mm ? Number(mm[2]) : -1;
  const state = owner ? owner[1] : null;
  if ((data.query ?? '').trimStart().startsWith('query')) {
    const found = state && state.issues.has(n) ? { number: n, repository: { nameWithOwner: mm[1] } } : null;
    return send(res, 200, { data: { node: found } });
  }
  if (!state || !state.issues.delete(n)) return send(res, 200, { errors: [{ message: `Could not resolve to a node with the global id of '${node}'` }] });
  return send(res, 200, { data: { deleteIssue: { clientMutationId: null } } });
}

const server = createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`);
  const path = url.pathname;
  const q = Object.fromEntries(url.searchParams);
  const data = req.method === 'GET' ? {} : await readBody(req);

  if (path === '/v1/chat/completions' && req.method === 'POST') return chat(data, res);
  if (!(req.headers.authorization ?? '').startsWith('Bearer ')) return send(res, 401, { message: 'Bad credentials' });
  if (path === '/graphql' && req.method === 'POST') return graphql(data, res);
  if (path === '/user') return send(res, 200, { login: 'fake-anthony', name: 'Fake User', html_url: 'https://github.com/fake' });
  if (path === '/user/repos') return send(res, 200, [...new Set([SANDBOX, ...repos.keys()])].map((r) => ({ full_name: r, html_url: `https://github.com/${r}` })));

  const m = /^\/repos\/([^/]+\/[^/]+)(\/.*)?$/.exec(path);
  if (!m) return send(res, 404, { message: 'Not Found' });
  const repo = decodeURIComponent(m[1]);
  const rest = m[2] ?? '';
  const state = repoState(repo);

  if (req.method === 'GET') {
    if (rest === '') return send(res, 200, { full_name: repo, id: repoId(repo) });
    if (rest === '/labels') return send(res, 200, [...state.labels.keys()].sort().map((name) => ({ name })));
    if (rest === '/issues') {
      const wanted = (q.labels ?? '').split(',').filter(Boolean);
      const stateFilter = q.state ?? 'open';
      const rows = [...state.issues.keys()].sort((a, b) => b - a)
        .filter((n) => stateFilter === 'all' || state.issues.get(n).state === stateFilter)
        .filter((n) => wanted.every((w) => state.issues.get(n).labels.includes(w)))
        .map((n) => issueJson(repo, n, state.issues.get(n)));
      const perPage = Number(q.per_page ?? 30);
      const page = Number(q.page ?? 1);
      const headers = {};
      if (page * perPage < rows.length) {
        const next = new URL(url);
        next.searchParams.set('page', String(page + 1));
        headers.Link = `<${next.toString()}>; rel="next"`;
      }
      return send(res, 200, rows.slice((page - 1) * perPage, page * perPage), headers);
    }
    const mi = /^\/issues\/(\d+)$/.exec(rest);
    if (mi) {
      const n = Number(mi[1]);
      return state.issues.has(n) ? send(res, 200, issueJson(repo, n, state.issues.get(n))) : send(res, 404, { message: 'Not Found' });
    }
    return send(res, 404, { message: 'Not Found' });
  }

  if (req.method === 'PATCH') {
    const mi = /^\/issues\/(\d+)$/.exec(rest);
    const issue = mi && state.issues.get(Number(mi[1]));
    if (!issue) return send(res, 404, { message: 'Not Found' });
    for (const key of ['title', 'body', 'state']) if (key in data) issue[key] = data[key];
    if ('labels' in data) issue.labels = [...data.labels];
    issue.updatedAt = '2026-10-24T13:00:00Z';
    return send(res, 200, issueJson(repo, Number(mi[1]), issue));
  }

  if (req.method === 'POST') {
    if (rest === '/labels') {
      if (state.labels.has(data.name)) return send(res, 422, { message: 'Validation Failed', errors: [{ code: 'already_exists' }] });
      state.labels.set(data.name, data);
      return send(res, 201, data);
    }
    if (rest === '/issues') {
      const n = state.next++;
      const labels = [...(data.labels ?? [])];
      for (const label of labels) if (!state.labels.has(label)) state.labels.set(label, { name: label });
      state.issues.set(n, { title: data.title, body: data.body ?? '', labels, state: 'open' });
      return send(res, 201, issueJson(repo, n, state.issues.get(n)));
    }
    const ms = /^\/issues\/(\d+)\/sub_issues$/.exec(rest);
    if (ms) {
      state.subIssues.push([Number(ms[1]), data.sub_issue_id]);
      return send(res, 201, { ok: true });
    }
  }
  return send(res, 404, { message: 'Not Found' });
});

server.listen(port, '127.0.0.1', () => console.log(`fake GitHub on http://127.0.0.1:${port}`));
