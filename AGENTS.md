# AGENTS.md

## Commands

| Task | Command |
|---|---|
| Analyzer build | `./BuildAndRun.ps1 . --no-launch --arch x64` |
| Protocol tests | `dotnet test tests/PiDesk.Tests/PiDesk.Tests.csproj` |
| Run attached | `./BuildAndRun.ps1` |
| UI tests | `./ui-tests.ps1 -AppPid <PID>` |

Run commands from the repository root in PowerShell 7. `BuildAndRun.ps1` remains attached while PiDesk is open; elapsed shell time includes application runtime. UI tests send a real model prompt and may incur provider cost.

## Sources of truth

- `docs/architecture.md` defines the current process boundary.
- `docs/improvement-plan.md` is the canonical ordered plan.
- Pi RPC behaviour is owned by the tagged upstream sources linked from `docs/architecture.md`; the current baseline is Pi 0.84.4.
- Update the relevant document when a change alters architecture, supported behaviour, or milestone acceptance criteria.

## Boundaries

- Keep Pi's agent loop, credentials, tools, extensions, and session persistence in Pi; PiDesk is a graphical RPC client.
- Keep protocol handling in `PiDesk.Protocol`; WinUI code consumes typed state and operations rather than raw RPC JSON.
- Load the relevant `winui-*` skill before WinUI design, implementation, review, testing, or packaging work.
- Do not commit credentials, session JSONL files, model output, or build/package artifacts.
- Do not run packaged executables directly; use `BuildAndRun.ps1` or project-mode `winapp run`.
- GitHub Actions are intentionally disabled until a workflow is explicitly reviewed and enabled.

## Gotchas

- RPC uses strict LF-delimited JSONL over redirected stdin/stdout.
- Pi sessions and resources are bound to the selected project working directory.
- Honour `cancelled` results from session replacement commands before changing visible state.
- Expected child-process shutdown must not be surfaced as an error.
- UI state updates from RPC readers must be marshalled through `DispatcherQueue`.
- Do not wait for a new RPC response inside the stdout event-reader callback; that deadlocks response processing.
