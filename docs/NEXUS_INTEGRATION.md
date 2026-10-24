# Cascade Nexus v0.9 — browser-native integration

This release adds three working views in the **Nexus workspace** using the existing Cascade plan data: sortable/searchable work grid with CSV export, interactive SVG parent-child relationship graph, and commit activity timeline. It also introduces a blue/violet Nexus visual treatment.

## Open-source design influences and deployment decisions

- Tabulator: sortable/filterable work tables. Implemented a **small original browser-native grid**, not a copy of Tabulator or its source. No external dependency.
- Cytoscape.js: relationship graphs. Implemented a **small original SVG graph** for declared parent-child links; not a full graph-layout engine.
- DHTMLX Gantt: scheduling/timeline interaction. Implemented a **commit activity histogram**, not a Gantt chart; dates in current data are insufficient to infer task start/end dates.
- OpenProject and Plane: work hierarchy concepts. Existing Cascade hierarchy remains; no third-party backend imported.
- Mermaid: graph and workflow diagrams. Not bundled; the SVG graph needs no runtime or CDN.
- LangGraph: agent orchestration concepts. Not ported; doing so would require implementing durable checkpoints, retries, human approval, and tool contracts, and should not be represented as complete.

All Nexus workspace code is written in vanilla browser JavaScript/CSS/HTML. Existing PowerShell 5.1 backend is retained. No Node, Python, Docker, administrator installation, or internet-hosted scripts are needed **at runtime**.

## Limits and verification

- Browser views use `Hierarchy` or `Issues` from the loaded plan. A missing parent is not fabricated. Graph shows up to 150 nodes and truncates labels.
- The timeline counts timestamped commits, not effort or completed work.
- Grid export sanitizes leading spreadsheet formula characters and quotes CSV fields.
- ARC/CAC and Git.mil API issues from v0.7 are **not fixed** in this release; they require live Nautilus testing and ARC source.
- No third-party source code has been copied, and no claim is made to implement all of Tabulator, DHTMLX, Cytoscape, OpenProject, Plane, Mermaid, or LangGraph.
- This package has not been executed on Windows PowerShell 5.1 or Nautilus. Validate browser loading, import, graph, grid, export, and backend endpoints on target.


See [v0.9 engineering review](NEXUS_V09_ENGINEERING_REVIEW.md) for bug fixes and verified automated smoke tests.
