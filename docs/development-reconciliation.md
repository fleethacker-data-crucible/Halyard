# Development branch reconciliation

2026-10-30

The old launcher/release-link branch predates the source-only C# engine. Its compatibility intent is retained through forwarding BAT files and an included launch alias in release bundles. Release references point at the organization. Keep the current in-memory compiler and source-only build: do not restore the old DLL/Newtonsoft loader or DLL-based update wording. Shared UI 0.2 work is merged separately on this reconciliation branch.
