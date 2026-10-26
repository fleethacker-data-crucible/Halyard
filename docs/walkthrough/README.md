# Halyard walkthrough

Seed a synthetic backlog, find its gaps, and approve the fixes Halyard proposes, without touching a real repository.

> **Status: skeleton.** The screenshots below are placeholders. Zombie Admin captures them with demo data, and the Chief of Staff scans them for sensitive content before they are added (team directive 10). Until then the image links do not resolve.

## Before you start

- Windows PowerShell 5.1 (part of Windows). No administrator rights and no installer.
- Microsoft Edge or Google Chrome.
- Launch: `build\Build-Halyard.ps1` once on the build machine, then `.\Launch-Halyard.bat` (opens the browser UI on `127.0.0.1`).
- A sandbox repository you are allowed to write to (the walkthrough uses a synthetic one).

## Steps

### 1. First launch

Halyard opens on **Overview**. With nothing synchronized yet, the page is empty and the sidebar lists the Work and Operate pages.

![Halyard Overview page on first launch, with no backlog synchronized yet and the sidebar listing the Work and Operate pages.](../assets/walkthrough/01-first-launch.png)

### 2. Set up the model

Open **Settings**, enter the model name and your API key, and select **Test**. The key is held in memory unless you tick *remember*.

![Halyard Settings page with the model name filled in, the API key field empty and the Test button.](../assets/walkthrough/02-settings-model.png)

### 3. Seed the sandbox

Open **Sandbox** and seed 100 synthetic items. About 80 are complete and 20 carry a gap.

![Halyard Sandbox page after seeding 100 synthetic items, about 20 of them with gaps.](../assets/walkthrough/03-sandbox-seed.png)

### 4. Synchronize

Open **Connection**, test the token and synchronize the sandbox.

![Halyard Connection page after a successful token test and sandbox synchronization.](../assets/walkthrough/04-connection-sync.png)

### 5. Read the overview

Back on **Overview**, see the hierarchy levels, items needing review, sizing coverage and next actions.

![Halyard Overview page for the demo sandbox: hierarchy levels, items needing review, sizing coverage and next actions.](../assets/walkthrough/05-overview.png)

### 6. Review the gaps

Open **Review**. Every item is checked against its definition of ready. Select rows to use the bulk bar.

![Halyard Review page listing items with gaps, three rows selected and the bulk action bar showing.](../assets/walkthrough/06-review-gaps.png)

### 7. Approve a proposal

Open **Proposals**, open a row to read its before and after diff, approve it, then **Apply to sandbox**. Delete the run from **Sandbox** when you are done.

![Halyard Proposals page with one proposal open, showing its before and after diff and the Approve button.](../assets/walkthrough/07-proposal-diff.png)

## About these images

Images live in [`docs/assets/walkthrough/`](../assets/walkthrough/), named `NN-<screen>.png` after the step they belong to. They are 1440x900 at 100% scale in the Night watch (dark) theme, and show demo data only.
