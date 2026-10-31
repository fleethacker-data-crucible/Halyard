# Handoffs from other Fleet apps

Work flows between Fleet apps as plain files (Fleet relationship map, flow 4). PacketRat can hand over action items it found in mail and
Eight Bells can hand over lessons learned and follow-ups; Halyard turns them into backlog drafts. **No handoff format existed on the
PacketRat or Eight Bells side when this was written**, so this page defines `fleet-handoff` v1 for both to emit; neither app writes
it yet.

## Format

```json
{
  "format": "fleet-handoff",
  "version": 1,
  "source": { "app": "packetrat", "exportedAt": "2026-10-31T09:00:00Z" },
  "items": [
    { "ref": "msg-0042", "title": "Renew the badge reader contract", "body": "Vendor asked for a decision by Friday.",
      "labels": ["area::facilities"], "url": "https://example.org/ticket/42", "due": "2026-11-06", "kind": "story" }
  ]
}
```

| Field | Rule |
|---|---|
| `format`, `version` | exactly `fleet-handoff` and `1` |
| `source.app` | the exporting app, lower case: `packetrat`, `eight-bells` |
| `items[].ref` | required; the exporter's own stable id. Importing the same ref again is recognised as already copied |
| `items[].title` | required, at most 200 characters |
| `items[].body` | optional text, at most 20 000 characters. No personal data beyond what the item needs: no mailbox contents, no names of people unless the item is about them |
| `items[].kind` | optional hint: `story`, `feature`, `epic`, `theme`; otherwise Intake guesses |
| `items[].labels`, `url`, `due` | optional; `url` must be http(s); `due` is carried into the body as text |

At most 500 items per file. Control characters are removed; an item without `ref` or `title`, or with a repeated `ref`, is skipped and listed.

## What Halyard does with it

1. `POST /api/intake/handoff/preview` with `{ "document": <the file's JSON> }` returns the drafts Intake would create (type, canonical id,
   halyard-meta body, parent guess, gaps) and which refs were already copied. Nothing is written.
2. `POST /api/intake/handoff/run` copies them into the sandbox through the normal write gateway with a link back to the handoff ref
   (`source::<app>` label, "Handed over by <app> (ref ...)" in the body), the review label and an intake manifest. Sandbox only; the
   operator write lease applies.
3. The drafts then go through the usual review, sizing and approval. A handoff never writes to the source backlog or production.

Mapping suggestions for the exporters: PacketRat exports an action only after the user accepted it in its queue; Eight Bells exports a
lesson learned as `kind: "story"` with its evidence reference in `url`. These are suggestions, not part of the format.
