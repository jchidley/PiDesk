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

PiDesk launches the installed Pi CLI in RPC mode from `Services/PiRpcClient.cs`. The selected project folder becomes the child process working directory, preserving Pi's cwd-bound context, tools, settings, trust decisions, and persistent sessions.

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

The current implementation is an RPC chat client, not yet a complete replacement for Pi's interactive mode. It supports basic prompts, steering, model and thinking selection, tool status, extension dialogs, new sessions, and usage display. Session browsing, tree navigation, rich tool output, diffs, markdown, follow-up queues, and command discovery remain planned work.
