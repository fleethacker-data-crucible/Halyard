# CASCADE — local development (no CAC)

Run `Launch-LocalDev.bat` from this repository on your **personal Windows PC** after checking out the feature branch. Do not use on a managed system to defeat organizational security policy.

## What it does

- Prerequisite: Windows PowerShell 5.1; no CAC needed.
- Startup: Starts Cascade browser without -AuthenticateArc and leaves its console visible for diagnosis.
- Boundary: Live Git.mil, GenAI and Arc-backed actions are unavailable until legitimately authenticated. Existing local/mock fixtures can be used.
- Diagnostics / manual check: `powershell.exe -NoProfile -File scripts/Start-CascadeWeb.ps1 -NoBrowser`.

## Workflow

```powershell
git fetch origin
git switch feature/local-dev-no-cac-launcher
.\Launch-LocalDev.bat
```

The original production launcher and authentication checks remain unchanged. Mock, local, or offline sessions are **not** authenticated Navy sessions, and must not contain CUI, real credentials, classified material, or production personal data.

When stable, remove `Launch-LocalDev.bat` (and the ARC mock files where applicable) from the release branch. Do not deploy them as a means of avoiding CAC or authorization for live systems.
