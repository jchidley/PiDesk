# Develop PiDesk

This guide describes the supported local development loop.

## Prerequisites

- Windows with Developer Mode enabled
- .NET SDK 10
- WinApp CLI 0.6 or later
- Pi installed and authenticated, with `pi.cmd` on `PATH`

## Build and run

From PowerShell 7 in the repository root:

```powershell
./BuildAndRun.ps1
```

The command remains attached while the application is open. Elapsed time printed by the shell therefore includes application runtime, not only compilation.

Build with the bundled WinUI analyzer without launching:

```powershell
./BuildAndRun.ps1 . --no-launch --arch x64
```

## UI tests

Launch PiDesk, copy the reported PID, then run:

```powershell
./ui-tests.ps1 -AppPid <PID>
```

The test sends one small prompt through the selected model and may incur a small provider charge. It verifies connection, model and thinking controls, the composer, a streamed assistant response, settled state, usage reporting, and AutomationId coverage. Generated results and screenshots are written under `artifacts/ui-tests/`; the repository keeps only deliberately reviewed evidence under `docs/evidence/` and `docs/images/`.

## Diagnostic output

Attached runs enable WinApp CLI debug output. Native first-chance framework messages may appear in the full log; a managed CLR exception is identified by code `0xE0434352`. Normal PiDesk shutdown is designed to close Pi's stdin and await clean stream EOF rather than cancelling redirected reads.
