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

PiDesk launches the installed Pi CLI in RPC mode through the platform-neutral `src/PiDesk.Protocol/PiRpcClient.cs` library. The selected project folder becomes the child process working directory, preserving Pi's cwd-bound context, tools, settings, trust decisions, and persistent sessions.

The transport parses strict LF-delimited JSONL through `src/PiDesk.Protocol/RpcProtocol.cs`, correlates requests by ID and process generation, continues after malformed records, and separates intentional shutdown from unexpected process exit. JSONL records are limited to 16,777,216 characters; stderr is consumed in chunks while retaining only the newest 8,192 characters. Timestamped diagnostics are retained in a bounded in-memory sink, and observer failures cannot interrupt transport processing.

`src/PiDesk.Protocol/PiSessionService.cs` owns the typed local mapping of commands, responses, events, and lifecycle state consumed by the ViewModel. It validates installed package metadata against Pi 0.84.4 before launch and maps missing required fields to explicit compatibility failures. Startup loads state, models, thinking levels, active-path messages, and usage into a typed snapshot before the ViewModel publishes it. Project replacement prepares and loads a separate candidate process, commits it only when usable, then stops the previous process; failed candidates leave that previous process connected. Process-source checks prevent detached readers from publishing into the replacement session. Prompt responses are treated as authoritative acceptance, while typed queue updates retain unsent steering and follow-up text so abort can restore it even when the `clear_queue` response is ambiguous.

## Layer responsibilities

Pi's tagged RPC sources are the canonical contract; PiDesk must not create a second independently authoritative protocol specification or domain core. `PiDesk.Protocol` is the adapter boundary: it owns transport, serialization, validation, compatibility checks, and typed state for the protocol surface PiDesk consumes. WinUI code consumes that typed boundary and must not traverse raw protocol JSON or reach into Pi-owned session storage.

The ViewModel owns presentation state and translates user intent into typed service operations. XAML owns presentation structure, styling, templates, visual states, accessibility metadata, and automation identifiers; code-behind is reserved for genuinely view-specific Windows behaviour. Prefer declarative XAML over programmatically constructed controls when either can express the interface cleanly.

PiDesk has one presentation surface: WinUI 3. Pi's CLI and TUI are upstream front ends, not adapters maintained by this repository. Additional web, terminal, mobile, or Apple presentation layers are out of scope unless the product boundary is deliberately changed.

PiDesk owns graphical presentation, Windows interaction, and translation between typed UI state and RPC records. It does not reimplement model authentication, the agent loop, tools, compaction, retries, extensions, or session persistence.

## Why RPC mode

Pi's interactive TUI and RPC mode are alternative front ends over the same session runtime. A C# WinUI process cannot directly embed Pi's TypeScript SDK, so the supported language-neutral integration is Pi's newline-delimited JSON protocol.

The application sends commands such as `prompt`, `abort`, `new_session`, `set_model`, and `get_session_stats`. It consumes lifecycle, message, tool, queue, retry, compaction, and extension UI events.

## Source baseline

PiDesk currently targets `@earendil-works/pi-coding-agent` 0.84.4:

- [RPC documentation](https://github.com/earendil-works/pi/blob/v0.84.4/packages/coding-agent/docs/rpc.md)
- [RPC mode](https://github.com/earendil-works/pi/blob/v0.84.4/packages/coding-agent/src/modes/rpc/rpc-mode.ts)
- [Official TypeScript RPC client](https://github.com/earendil-works/pi/blob/v0.84.4/packages/coding-agent/src/modes/rpc/rpc-client.ts)
- [RPC protocol types](https://github.com/earendil-works/pi/blob/v0.84.4/packages/coding-agent/src/modes/rpc/rpc-types.ts)
- [Strict JSONL framing](https://github.com/earendil-works/pi/blob/v0.84.4/packages/coding-agent/src/modes/rpc/jsonl.ts)

The installed package declares `https://github.com/earendil-works/pi` as its repository. Historic `badlogic/pi-mono` links currently redirect there.

## Current boundaries

The current implementation is an RPC chat client and does not yet provide full graphical feature parity with Pi's interactive TUI. It supports acceptance-aware prompts, basic steering, abort queue recovery, model and thinking selection, tool status, extension dialogs, atomic new/project session replacement, restored active-path messages, and usage display. Full queue presentation, selector concurrency, session browsing, tree navigation, rich tool output, diffs, Markdown, follow-up controls, and command discovery remain planned work.
