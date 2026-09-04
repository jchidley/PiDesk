# Architecture

This page explains why PiDesk is a separate native process and which responsibilities remain with Pi. It is not an RPC protocol reference; Pi's tagged source remains authoritative for that protocol.

## Process boundary

```text
PiDesk (WinUI 3 / C#)
  └─ JSONL over redirected stdin/stdout
      └─ Pi CLI (Node.js, --mode rpc)
          ├─ AgentSessionRuntime
          ├─ sessions, tools, skills and extensions
          ├─ pi-agent-core
          └─ pi-ai model providers and authentication
```

PiDesk launches the installed Pi CLI in RPC mode through the platform-neutral `src/PiDesk.Protocol/PiRpcClient.cs` library. The backend may be the native Windows installation (the default) or an explicitly selected WSL distribution discovered with `wsl.exe --list --quiet`. Each WSL selection uses that distribution's default user; PiDesk does not choose or hardcode a Linux user. The selected project folder becomes the child process working directory, preserving Pi's cwd-bound context, tools, settings, trust decisions, and persistent sessions.

`PiDesk.Protocol` owns backend discovery, side-effect-free preflight, path translation, version validation, and launch construction. Windows preflight resolves the global package's Node entry point and package metadata without running Pi. WSL preflight uses non-login `wsl.exe --exec` commands to resolve native Linux `node` and the real Pi entry point from the default user's non-interactive PATH or FNM default alias, reads package metadata with that resolved Node executable, validates the project directory, and requires a version in the audited Pi 0.85.x line before RPC starts. Discovery and preflight commands have a 30-second timeout, bounded output, and process-tree cleanup. RPC itself is launched directly as `wsl.exe --distribution <name> --cd <linux-cwd> --exec <node> <cli> --mode rpc`; no profile, login shell, wrapper, banner, or startup script can write onto its JSONL stdout.

Backend replacement uses the same candidate/commit rule as project replacement: preflight and start a separate candidate, load its authoritative state and messages, publish backend, project, conversation, session summary, and selectors atomically while leaving composed text unchanged, then stop the prior process. A missing backend, path or Pi installation, unsupported version, cancellation, or candidate load failure leaves the previous process and visible state intact. Event handlers are bound to the committed process instance, so detached WSL or Windows readers cannot publish late events.

### Backend project paths

- The Windows backend accepts an existing absolute Windows filesystem path. WSL-owned UNC paths require selecting the owning WSL distribution.
- A WSL backend accepts an absolute drive path such as `C:\work\repo`; preflight passes it to that distribution's `wslpath` rather than assuming `/mnt/c`, then proves the translated directory exists.
- A WSL backend also accepts canonical `\\wsl.localhost\<distribution>\...` paths when `<distribution>` matches the selected backend. The UNC remainder maps directly to the distribution's absolute Linux path.
- Relative paths, traversal segments, malformed UNC paths, network shares, unavailable drive mounts, and paths owned by another WSL distribution fail with corrective guidance. PiDesk does not guess a mount root or silently switch distributions.

The transport parses strict LF-delimited JSONL through `src/PiDesk.Protocol/RpcProtocol.cs`, correlates requests by ID and process generation, continues after malformed records, and separates intentional shutdown from unexpected process exit. JSONL records are limited to 16,777,216 characters; stderr is consumed in chunks while retaining only the newest 8,192 characters. Timestamped diagnostics are retained in a bounded in-memory sink, and observer failures cannot interrupt transport processing.

`src/PiDesk.Protocol/PiSessionService.cs` owns the typed local mapping of commands, responses, events, and lifecycle state consumed by the ViewModel. Activity events distinguish assistant text and thinking streams, streamed tool-call arguments, validated execution arguments, accumulated tool updates, final results and errors, edit diffs, retries, and compaction outcomes before any presentation mapping. `PiActivityReducer` then correlates those typed RPC events by tool-call ID and content index into ordered immutable rendering state; replacement snapshots reset it from typed `get_messages` results without reading Pi session files. It validates installed package metadata against the audited supported range, Pi 0.85.0 or later but earlier than 0.86.0, before launch and maps missing required fields to explicit compatibility failures. Startup loads state, models, thinking levels, active-path messages, and usage into a typed snapshot before the ViewModel publishes it. Project replacement prepares and loads a separate candidate process, commits it only when usable, then stops the previous process; failed candidates leave that previous process connected. Process-source checks prevent detached readers from publishing into the replacement session. Prompt responses are treated as authoritative acceptance, while typed queue updates retain unsent steering and follow-up text so abort can restore it even when the `clear_queue` response is ambiguous.

The service serializes state-changing commands through one asynchronous operation gate. Model and thinking requests additionally carry independent monotonic selection versions, a session-intent version, and the committed session generation. Superseded model or thinking requests may finish at the transport boundary, but they cannot publish state; the latest request runs last and returns one atomic typed result containing the confirmed model, its thinking levels, and active thinking level. Starting, replacing, or stopping a session invalidates outstanding selector results before waiting for the operation gate. Extension UI responses deliberately bypass that gate because Pi can require a response to complete the command that currently owns it.

The visible command policy is:

| State | Send/abort/new/project | Model and thinking |
|---|---|---|
| Starting, replacing, or stopping | Disabled; repeated UI invocation is ignored | Disabled and outstanding results invalidated |
| Connected | One UI mutation at a time; direct typed callers are serialized | Enabled; rapid changes use latest-selection-wins |
| Busy | Send becomes steer and Abort is enabled; new/project remain single-operation commands | Enabled until another session operation starts |
| Disconnected or faulted | Disabled except project selection for recovery | Disabled |

RPC requests started from an event must not be awaited by the stdout event callback. Post-settle usage refresh is therefore retained as a serialized background task, with failures observed and the task awaited during disposal.

Conversation presentation consumes `PiActivityReducer` output through distinct user, assistant, thinking, tool, diff, retry, compaction, and error models. Thinking, tool output, arguments, and diffs are expandable and remain one selectable text surface regardless of line count; collapsed detail controls are not created until expansion. Assistant Markdown follows the side-effect-free [conversation content policy](content-policy.md): links and images never open or load during rendering, raw HTML remains literal, and unsupported syntax remains selectable text.

Deterministic UI acceptance uses the same process boundary. Debug builds accept an explicit `--ui-test-rpc=<script>` launch argument, which selects a fake JSONL child while leaving `PiSessionService`, typed parsing, reduction, and presentation unchanged. Release builds do not compile this override.

## Layer responsibilities

Pi's tagged RPC sources are the canonical contract; PiDesk must not create a second independently authoritative protocol specification or domain core. `PiDesk.Protocol` is the adapter boundary: it owns transport, serialization, validation, compatibility checks, and typed state for the protocol surface PiDesk consumes. WinUI code consumes that typed boundary and must not traverse raw protocol JSON or reach into Pi-owned session storage.

The ViewModel owns presentation state and translates user intent into typed service operations. XAML owns presentation structure, styling, templates, visual states, accessibility metadata, and automation identifiers; code-behind is reserved for genuinely view-specific Windows behaviour. Prefer declarative XAML over programmatically constructed controls when either can express the interface cleanly.

PiDesk has one presentation surface: WinUI 3. Pi's CLI and TUI are upstream front ends, not adapters maintained by this repository. Additional web, terminal, mobile, or Apple presentation layers are out of scope unless the product boundary is deliberately changed.

PiDesk owns graphical presentation, Windows interaction, and translation between typed UI state and RPC records. It does not reimplement model authentication, the agent loop, tools, compaction, retries, extensions, or session persistence.

## Why RPC mode

Pi's interactive TUI and RPC mode are alternative front ends over the same session runtime. A C# WinUI process cannot directly embed Pi's TypeScript SDK, so the supported language-neutral integration is Pi's newline-delimited JSON protocol.

The application sends commands such as `prompt`, `abort`, `new_session`, `set_model`, and `get_session_stats`. It consumes lifecycle, message, tool, queue, retry, compaction, and extension UI events.

## Source baseline

PiDesk currently targets `@earendil-works/pi-coding-agent` 0.85.0:

- [RPC documentation](https://github.com/earendil-works/pi/blob/v0.85.0/packages/coding-agent/docs/rpc.md)
- [RPC mode](https://github.com/earendil-works/pi/blob/v0.85.0/packages/coding-agent/src/modes/rpc/rpc-mode.ts)
- [Official TypeScript RPC client](https://github.com/earendil-works/pi/blob/v0.85.0/packages/coding-agent/src/modes/rpc/rpc-client.ts)
- [RPC protocol types](https://github.com/earendil-works/pi/blob/v0.85.0/packages/coding-agent/src/modes/rpc/rpc-types.ts)
- [Strict JSONL framing](https://github.com/earendil-works/pi/blob/v0.85.0/packages/coding-agent/src/modes/rpc/jsonl.ts)

The installed package declares `https://github.com/earendil-works/pi` as its repository. Historic `badlogic/pi-mono` links currently redirect there.

## Current boundaries

The current implementation is an RPC chat client and does not yet provide full graphical feature parity with Pi's interactive TUI. It supports acceptance-aware prompts, basic steering, abort queue recovery, model and thinking selection, extension dialogs, atomic new/project session replacement, restored active-path messages, usage display, and typed rendering of thinking, tools, diffs, retries, compaction, errors, and safe Markdown. Session browsing and branching, full queue presentation, explicit follow-up controls, and command discovery remain planned work.
