# Windows PowerShell 5.1 compatibility

This build adapts the new AgentRuntime for the Windows PowerShell 5.1 baseline already declared in Cascade.psd1. It replaces PowerShell 7-era `::new()` constructor usage in the new runtime and changes its documentation and launcher to `powershell.exe`.

## Run

Double-click `Test-Cascade-PS51.bat` or use:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-AgentRuntime.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Invoke-AgentDemo.ps1
```

The demo operates on local fixtures and cannot modify GitLab. `-ExecutionPolicy Bypass` applies only to that process; follow organizational policy before using it.

## Verification limits

PowerShell is unavailable in the build environment, so Windows PowerShell 5.1 execution and broader project compatibility have not been verified. Report errors with their stack traces for follow-up.
