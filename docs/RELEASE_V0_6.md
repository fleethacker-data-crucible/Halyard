# Cascade v0.6: Hierarchy and diagnostics

## Added
- Deeply nested, expandable GitLab hierarchy UI: Theme → Epic → Feature → Story → Task. Source hierarchy is extracted from existing Cascade metadata. Missing or ambiguous parent links remain unlinked rather than fabricated.
- Git.mil snapshots now include a `Hierarchy` projection; older plans fall back to flat, unclassified issues.
- Browser diagnostic event capture, navigation/button clicks, JavaScript errors, failed Git.mil requests, server request IDs, status codes and latency.
- Diagnostics page with refresh and JSON support-bundle export.
- Server JSONL logs at `logs/cascade-YYYYMMDD.jsonl`, without request bodies, credentials, tokens or issue descriptions.
- Local Git scanning remains supported alongside Git.mil remote sync.

## Known limitations / required testing
- This is **not production certified**; Windows PowerShell 5.1, ARC and Git.mil integration testing is still required.
- Hierarchy only represents existing explicit metadata links; it does not yet create or repair the entire hierarchy autonomously.
- Git.mil issue metadata is inconsistent across projects; unclassified records are displayed separately as roots.
- Logs are intentionally minimal to avoid leaking sensitive Git.mil data. Review support bundle before sharing.
- Autonomous AI semantic classification, reliable closed-loop execution and verified auto-closure are not implemented.
- No production write controls are exposed in the GUI.

## Test instructions
1. Launch `Cascade-GUI.bat` on an approved Windows machine.
2. Open Diagnostics, then Git.mil connection. Check ARC, discover projects, sync.
3. Open Agile hierarchy and expand work items. Search for a known epic and inspect the link.
4. If a button fails, open Diagnostics → Refresh server log → Export support bundle. Send only the reviewed JSON bundle, not your ARC vault or raw GitLab token.
