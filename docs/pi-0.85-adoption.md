# Pi 0.85.0 adoption audit

PiDesk moved its minimum supported RPC baseline from Pi 0.84.4 to 0.85.0 on 2026-09-04. Evidence came from the installed 0.85.0 package, its changelog and linked documentation, `rpc-types.d.ts`, `rpc-mode.js`, `rpc-client.js`, the PiDesk typed protocol/service code, and deterministic tests. The active Windows CLI and registry target were both 0.85.0.

The 0.85.0 RPC command and response shapes consumed by PiDesk are unchanged. `abort` now correctly waits for manual compaction cancellation upstream. `list_sessions` and `navigate_tree` remain absent, so the Milestone 2 blocker is unchanged.

| Release item | Disposition | Evidence |
|---|---|---|
| `0.85.0:7` Persistent Claude thinking effort | checked—no change | Provider/session behavior remains Pi-owned; PiDesk already renders typed thinking blocks and ignores additive model metadata. |
| `0.85.0:8` Fullscreen transcript controls | no local surface | TUI fullscreen behavior is outside the WinUI RPC client. |
| `0.85.0:9` Restorable in-memory sessions | no local surface | PiDesk does not embed the SDK or externally store session entries. |
| `0.85.0:13` `SessionManager.inMemory()` restoration | no local surface | PiDesk uses persistent sessions through RPC and does not inspect session files. |
| `0.85.0:14` vLLM/OpenAI model settings | already covered | `get_available_models` is authoritative and PiDesk ignores unknown additive model fields. |
| `0.85.0:15` Relational-algebra LaTeX rendering | no local surface | This is inherited terminal Markdown rendering; PiDesk owns its separate safe renderer. |
| `0.85.0:16` Jump-to-latest label | no local surface | TUI-only transcript control. |
| `0.85.0:20` Embedded working indicator | no local surface | TUI editor presentation is not consumed over RPC. |
| `0.85.0:21` Fullscreen search performance | no local surface | TUI-only search implementation. |
| `0.85.0:25` musl managed-tool downloads | no local surface | Tool acquisition remains Pi-owned. |
| `0.85.0:26` Removed Grok model | already covered | PiDesk lists only models returned by `get_available_models`. |
| `0.85.0:27` Provider stream sequence fixes | checked—no change | Target RPC streaming declarations retain the event fields parsed by `PiSessionProtocol`; provider repair is upstream. |
| `0.85.0:28` Restored client entry point | no local surface | PiDesk is a C# subprocess client and imports no TypeScript client package. |
| `0.85.0:29` Qwen catalog addition | already covered | Available models are populated from Pi's RPC snapshot. |
| `0.85.0:30` OpenAI Codex SSE terminal event | no local surface | Provider transport remains inside Pi. |
| `0.85.0:31` Copilot reasoning levels | already covered | PiDesk uses Pi's available-thinking-level and set-thinking-level RPC commands. |
| `0.85.0:32` Baseten image capability | checked—no change | PiDesk does not yet submit image attachments and ignores additive model capability fields. |
| `0.85.0:33` Skills with Bash-only tools | no local surface | Skill discovery and execution remain Pi-owned. |
| `0.85.0:34` Concurrent session shares | no local surface | PiDesk does not expose sharing. |
| `0.85.0:35` EXIF orientation | no local surface | Image attachment/rendering is not implemented. |
| `0.85.0:36` Imported-session filename collision | no local surface | PiDesk does not import or parse sessions. |
| `0.85.0:37` Fork compaction boundary | follow-up required | Fork UI remains planned and blocked with Milestone 2; no current command surface changes. |
| `0.85.0:38` In-memory fork before settle | no local surface | PiDesk does not use in-memory SDK sessions. |
| `0.85.0:39` Fireworks GLM adapter | no local surface | Provider adapter remains Pi-owned. |
| `0.85.0:40` `NO_PROXY` matching | no local surface | Provider networking remains Pi-owned. |
| `0.85.0:41` Built-in tool `ctx.cwd` | already covered | PiDesk sets the selected project as backend cwd; fixed tool behavior is inherited from Pi. |
| `0.85.0:42` seccomp terminal startup | no local surface | TUI terminal startup is not used by direct RPC launch. |
| `0.85.0:43` Zed image detection | no local surface | TUI terminal capability detection is not used. |
| `0.85.0:44` Fullscreen drag selection | no local surface | TUI-only interaction. |
| `0.85.0:45` managed downloads without Releases API | no local surface | Tool acquisition remains Pi-owned. |
| `0.85.0:46` Branch-summary output cap | already covered | PiDesk consumes the existing summary events/messages; generation is Pi-owned. |
| `0.85.0:47` Write-tool byte count removal | checked—no change | Tool result text is rendered without parsing the removed prose count. |
| `0.85.0:48` Proxied HTTP after tool calls | no local surface | Provider networking remains Pi-owned. |
| `0.85.0:49` RPC abort during manual compaction | already covered | PiDesk already sends `abort`, awaits its correlated response, and restores queue text first. The upstream fix strengthens that existing path. |

## Result

The required repository change was advancing the audited compatibility range, current source links, and version assertions to Pi 0.85.0 or later but earlier than 0.86.0. Patch releases in the 0.85.x line are accepted; a later minor line requires a separate compatibility audit before the exclusive upper bound advances. No compatibility shim or protocol parser change was justified. Session listing and tree navigation still require upstream RPC commands.
