# Agentic product owner (target - not built yet)

Weekly Git runner (goal):
1. Collect packages: GitLab issues JSON + repo signals (README, paths, recent commits)
2. Cascade adapter posts packages to GenAi
3. AI returns proposals: create/link work items, adjust AC, parent, status hints
4. Human or policy gate before write; cascade-test first

Time tracking:
- Prefer GitLab Time tracking / estimates as optional
- Do not require engineers to log hours as primary process
- Conservative AI estimate from change size (files/LOC band) -> effort band on Story
- Feature % complete from Story points Done, not hours
- Refine estimates as Cascade matures

Epic metrics: status + feature completion counts + blocker age - not Fibonacci.
Feature: capacity_band in PfW; points on Stories; Definition of Done (not Done-When).
