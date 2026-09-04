# Develop PiDesk

This guide describes the supported local development loop.

## Prerequisites

- Windows with Developer Mode enabled
- .NET SDK 10
- WinApp CLI 0.6 or later
- For the default backend, Pi installed and authenticated on Windows, with `pi.cmd` on `PATH`
- Optionally, WSL and Pi installed in one or more distributions for their default users

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

## Protocol and process tests

Run the deterministic unit and fake-child process suite without launching WinUI or making a provider request:

```powershell
dotnet test tests/PiDesk.Tests/PiDesk.Tests.csproj
```

The fake RPC child requires `node.exe` on `PATH`, matching PiDesk's Pi runtime prerequisite. The 72-test suite covers Windows and WSL launch construction, injected WSL discovery and preflight, bounded command timeout/cancellation/output handling with child cleanup, path mapping and rejection, backend version/install failures, atomic cross-backend replacement, stale-backend isolation, and strict and bounded JSONL framing, response correlation, malformed records, caller cancellation, expected replacement shutdown, stale-reader isolation, child PID cleanup, unexpected EOF, bounded stderr context, observer failures, typed protocol parsing and commands, installed-version rejection, lifecycle transitions, message restoration, cancelled session replacement, candidate startup ordering, candidate load failure recovery, prompt rejection, delivery-order queue restoration, ambiguous clear-queue timeout recovery, request timeout, delayed model and thinking selection, cross-selector refresh preservation, selector invalidation during replacement, repeated serialized mutations, an ordered Pi 0.84.4 activity fixture covering thinking, tool arguments and updates, results, errors, retries, compaction, and diffs, plus RPC-derived rendering reduction for interleaved calls, typed restoration, replacement reset, and 10,000-line output. It also retains killed-mutation evidence from the preceding slices for the exact record-size boundary, CRLF normalization, stale-generation guard, newest-stderr retention, version gate, and required state fields. The mutation ledger records one additional survivor whose intended large-single-chunk branch was not reachable through the process fixture because the reader delivered smaller chunks.

## Backend selection

PiDesk starts with the Windows backend. The Backend combo also lists the names returned by `wsl.exe --list --quiet`; selecting one deliberately replaces the current RPC process only after that distribution passes preflight and supplies a complete authoritative snapshot. The distribution's default user is used. Preflight resolves native Linux Node/Pi from its non-interactive PATH or FNM default alias without sourcing shell profiles; Windows executables inherited through WSL interop are rejected.

For a WSL backend, choose either an ordinary absolute Windows drive path or a canonical `\\wsl.localhost\<selected-distribution>\...` path. Drive paths are translated by `wslpath` inside the selected distribution so custom automount configuration is respected. A path from another distribution, an unavailable mount, a relative or malformed path, or an unrelated network share is rejected rather than guessed. Install a Pi 0.85.x release (0.85.0 or later, but earlier than 0.86.0) separately in every backend you intend to select. PiDesk advances this audited range only after release adoption. A failed backend change identifies the attempted backend in its error while preserving the previously connected backend and conversation. Backend discovery and preflight fail after 30 seconds, bound captured output, and terminate the command process tree on timeout or cancellation.

## UI tests

Conversation activity is sourced only from typed Pi RPC events and `get_messages` snapshots. The deterministic suite verifies event ordering, interleaved tool correlation, restored thinking and tool state, diff retention, and a 10,000-line tool update before UI testing. Markdown rendering is deliberately non-navigating; see [the conversation content policy](content-policy.md).

For deterministic Milestone 1 acceptance, launch the Debug app with the fake RPC child, copy the reported PID, then run the dedicated suite:

```powershell
$env:NuGetAudit = 'false'
$fixture = (Resolve-Path tests/ui-fixtures/milestone1-rpc.js).Path
./BuildAndRun.ps1 . --detach --json --arch x64 --args "--ui-test-rpc=$fixture"
./milestone1-ui-tests.ps1 -AppPid <PID> -CloseApp
```

The `--ui-test-rpc` override is compiled only in Debug builds. It also exposes deterministic Windows, successful WSL, and unavailable WSL backend choices without invoking the machine's real WSL installations. Run the focused backend selector coverage with:

```powershell
$env:NuGetAudit = 'false'
$fixture = (Resolve-Path tests/ui-fixtures/milestone1-rpc.js).Path
$result = ./BuildAndRun.ps1 . --detach --json --arch x64 --args "--ui-test-rpc=$fixture" | ConvertFrom-Json
./backend-ui-tests.ps1 -AppPid $result.ProcessId -CloseApp
```

The focused suite verifies the accessible default selection, keyboard-driven confirmed WSL replacement, and failed replacement preserving the prior backend, project, conversation, and composer. The 17-scenario activity suite makes no provider request: its JSONL records pass through the production RPC client, typed session service, activity reducer, ViewModel, and XAML. It covers safe Markdown, streaming and final tool states, failed tools, first-class diffs, keyboard expansion, selection/copy, bounded automation names, responsive 10,000-line output, Stop, and clean shutdown. Retained results are in `docs/evidence/milestone1-ui-test-results.json`; the reviewed screenshot is `docs/images/milestone1-activity.png`.

For the bounded real-Pi smoke, launch PiDesk normally and run:

```powershell
./ui-tests.ps1 -AppPid <PID>
```

The real-Pi test sends one small deterministic-answer prompt plus one prompt that is immediately aborted and may incur a small provider charge. It verifies connection, rapid thinking selection, atomic new session, prompt streaming and settlement, usage reporting, abort recovery, same-folder project replacement with a new Pi child, AutomationId coverage, and optional clean parent/child shutdown. Pass `-CloseApp` to include shutdown verification. Generated results and screenshots are written under `artifacts/ui-tests/`; the repository keeps only deliberately reviewed evidence under `docs/evidence/` and `docs/images/`.

## Diagnostic output

Attached runs enable WinApp CLI debug output. Native first-chance framework messages may appear in the full log; a managed CLR exception is identified by code `0xE0434352`. Normal PiDesk shutdown is designed to close Pi's stdin and await clean stream EOF rather than cancelling redirected reads.
