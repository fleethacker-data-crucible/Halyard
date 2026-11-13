# GHAS alert triage (sprint 2026-11)

Open GitHub Advanced Security alerts reviewed in this sprint. This file records the disposition. It does not dismiss alerts; dismissal stays with the owner.

| Alert | Rule / package | Path | Disposition | Reason |
| --- | --- | --- | --- | --- |
| code-scanning #1 | js/incomplete-sanitization | web/test/ui-a11y.test.tsx | fixed | Dialog-id matching only prefixed `:`, so a backslash stayed a regex escape; it now uses a full literal escape. |
| code-scanning #2 | js/incomplete-sanitization | web/test/ui-a11y.test.tsx | fixed | Same escape as #1, on the described-by id. |
| dependabot #1 | zod (npm), GHSA-mrhx-c7xc-m3w5 / CVE-2023-54404 | web/package.json | needs owner decision | Pinned at 3.25.76; 4.6.6 is a major bump, and the bounded array/set/map path that release changes is unused. If kept, dismiss as tolerable_risk. |

Fixed code-scanning alerts should close on the next scan. Do not dismiss them.

## code-scanning #1 and #2

`web/test/ui-a11y.test.tsx` builds a `RegExp` from the dialog ids that `ConfirmDialog` renders (`useId()`, so the ids are not attacker input). The old replace only turned `:` into `\:`. CodeQL's incomplete-sanitization rule flags that pattern because a backslash already in the id is not escaped, and the backslash then eats the next character.

`escapeRegExp` replaces `\`, `.`, `*`, `+`, `?`, `^`, `$`, `{`, `}`, `(`, `)`, `|`, `[`, and `]` with a backslash-prefixed literal. `:` is not a regex metacharacter; leaving it unescaped matches the same ids as `\:` did. A test locks the backslash case.

These findings are test-only. The escape is small and keeps the assertion meaning, so they are fixed rather than accepted as "used in tests".

## dependabot #1 — zod

Jeremy Gerdes needs to choose: keep zod 3.25.76, or approve a separate migration to zod >= 4.6.6.

If the choice is to keep 3.25.76, dismiss Dependabot alert 1 with reason **tolerable_risk**. `not_used` does not fit: unbounded `z.array(...)` schemas do run on host JSON. The risk is tolerable because the behavior 4.6.6 actually changes is not on a path this UI uses, and the behavior the advisory text describes is unchanged in 4.6.6.

### What 4.6.6 changes

The patched release rejects an oversized array, set, or map before it parses elements, when `.max()`, `.length()`, or `.size()` is the leading size check. Unbounded collections are unchanged: each failing element still becomes an issue. There is no fixed 3.x release. npm's latest 3.x is 3.25.76; 4.6.6 is the first patched version and 4.6.7 is newer. Both are a major bump, which this sprint does not take.

### What this repo runs

`web/package.json` pins `zod` at 3.25.76. Every import is `from 'zod'`, and that entry re-exports the v3 API (`zod/index.js` → `./v3/external.js`). Nothing imports `zod/v4`.

On the installed 3.25.76 parser, `ZodArray._parse` records `maxLength` and `exactLength`, then still walks every element. Measured here: `z.array(z.string()).max(10)` and `.length(10)` against 1,000 non-strings each report 1,001 issues (one `too_big`, then one `invalid_type` per element). An unbounded array reports 1,000 issues. A valid array reports none.

### Reachability

The path 4.6.6 changes is not reachable. Schemas under `web/src` use `z.array(...)` with no `.max()`, `.length()`, `.size()`, or `.nonempty()`, and they do not use `z.set` or `z.map`.

Unbounded arrays are reachable. `useResource` (`web/src/lib/useResource.ts`) calls `schema.safeParse` on JSON from the loopback host, and it surfaces only the first issue, after zod has already built the list. Issues are allocated when an element fails. A payload that matches the schema does not build that list. The host caps request bodies at 8 MiB (`HttpServer.MaxBodyBytes`) and builds the responses the UI validates; sync size defaults to `MaxSyncItems` 2000. That bounds a normal response. It does not change zod's per-failure issue list, and 4.6.6 would not change it either, because these arrays have no upper bound.

The installed tarball also contains a v4 preview at `zod/v4` whose array parser walks every element and applies size checks afterwards. Application source does not import it, and the UI bundle is built from the v3 entry.
