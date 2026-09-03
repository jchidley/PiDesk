# Design review

This review records the current quality and completeness of PiDesk against Pi 0.84.4 and the upstream Microsoft WinUI development skills. It is a point-in-time assessment, not the implementation plan; implementation status and planned work belong in [the improvement plan](improvement-plan.md).

The latest implementation review verified a warning-free analyzer build, 37 deterministic protocol/process tests, and a 16-scenario real-Pi UI smoke test against Pi 0.84.4. The shared production library combines bounded transport handling with a typed session service, explicit lifecycle states, restored session snapshots, non-destructive candidate project startup, prompt and queue recovery, serialized mutations, and generation-safe latest-selection-wins selectors. Focused mutation trials from the preceding slices killed six semantic defects across exact record-size handling, CRLF normalization, stale-generation suppression, newest-stderr retention, version validation, and required response fields. One recorded survivor identified a fixture-unreachable large-single-chunk path rather than a missing externally exercised contract.

## Verdict

PiDesk is a sound native WinUI proof of concept and a useful Pi RPC chat client. It does not yet provide full graphical feature parity with Pi's interactive TUI.

| Area | Assessment |
|---|---:|
| WinUI presentation | 8/10 |
| Pi RPC integration | 6/10 |
| Feature parity with Pi TUI | 4/10 |
| Microsoft WinUI skills effectiveness | 7/10 |

## What works well

- The process boundary is correct: PiDesk replaces presentation while Pi retains the agent loop, tools, models, extensions, and sessions.
- The UI uses platform controls, Mica, theme resources, explicit `x:Bind` modes, a virtualized `ListView`, visible labels, and AutomationIds.
- MVVM boundaries are generally appropriate: state and commands live in the ViewModel; picker, keyboard, scrolling, and dialog coordination remain in code-behind.
- The shared RPC transport correlates responses by process generation, uses bounded strict LF-delimited framing, continues after malformed records, retains timestamped diagnostics and recent stderr safely, and distinguishes intentional shutdown from unexpected exit.
- The ViewModel and extension-dialog path now consume typed protocol models rather than traversing `JsonElement` records.
- Startup and session replacement restore the active message path and usage before applying visible state; failed candidate projects leave the prior session usable.
- Prompt cards expose pending and failed submission state, rejected text returns to the composer, and abort restores cleared steering then follow-up text before any newer draft.
- The analyzer build completes with no warnings; 37 deterministic protocol/process tests pass, and the current end-to-end UI suite passes 16 scenarios including rapid selection, abort, new session, project replacement, and clean shutdown.

## High-priority findings

### Session functionality is too limited

Pi RPC exposes switching, entries, tree navigation, fork, clone, naming, and export. PiDesk currently exposes only atomic New session and project replacement, although both now restore the active message path.

### Coding activity is reduced to plain text

The conversation currently presents assistant text plus `tool completed` or `tool failed`. It omits thinking, tool arguments, streaming output, final results, diffs, structured retry and compaction state, Markdown, and code formatting. This is the largest gap for a coding-agent UI.

## Other findings

- The fixed header columns have no responsive breakpoints and can clip at narrow widths.
- Full queue state is retained for abort recovery but is not yet displayed.
- Transport diagnostics are retained in memory and publicly readable but are not yet exposed through a user-copyable UI.
- Conversation items expose their CLR type rather than useful role/content names to UI Automation.
- The non-dismissible error bar lacks a retry or reconnect action.
- Pi command discovery, images, follow-up messages, session tree, and rich extension widgets are absent.

## Assessment of the Microsoft WinUI skills

The local `winui-design`, `winui-dev-workflow`, `winui-code-review`, and `winui-ui-testing` files exactly matched `microsoft/win-dev-skills/main` when reviewed.

### Effective parts

- `winui-dev-workflow` kept the project on the official MVVM template and packaged `winapp run` path.
- The bundled analyzer caught missing explicit `x:Bind` modes.
- The design checklist drove platform brushes, spacing, corner resources, window sizing, and accessibility identifiers.
- UI Automation tests found defects that compilation missed: Windows command-shim launching, absent streamed output, stale usage, and DispatcherQueue initialization order.

### Gaps exposed by this build

- `winapp find-ui` had no chat or coding-agent shell sample, so the central interaction was designed without a grounded reference.
- The first generated response test was a false positive because its expected token also appeared in the prompt.
- The skills did not prevent lifecycle and concurrency defects: late DispatcherQueue initialization, a same-channel RPC deadlock, async selector races, or cancellation-based subprocess shutdown.
- UI Automation coverage remains happy-path and Dark-theme biased; it does not yet cover narrow layout, High Contrast, extension UI, folder switching, queues, or cancelled session changes.
- `winui-session-report` supports Copilot CLI and Claude Code but not the Pi session format, even though the other skills run successfully under Pi.

## Upstream references

### Pi 0.84.4

- [RPC documentation](https://github.com/earendil-works/pi/blob/v0.84.4/packages/coding-agent/docs/rpc.md)
- [RPC mode](https://github.com/earendil-works/pi/blob/v0.84.4/packages/coding-agent/src/modes/rpc/rpc-mode.ts)
- [RPC client](https://github.com/earendil-works/pi/blob/v0.84.4/packages/coding-agent/src/modes/rpc/rpc-client.ts)
- [Protocol types](https://github.com/earendil-works/pi/blob/v0.84.4/packages/coding-agent/src/modes/rpc/rpc-types.ts)
- [JSONL framing](https://github.com/earendil-works/pi/blob/v0.84.4/packages/coding-agent/src/modes/rpc/jsonl.ts)

### Windows development sources

- [microsoft/win-dev-skills](https://github.com/microsoft/win-dev-skills)
- [WinUI design skill](https://github.com/microsoft/win-dev-skills/blob/main/plugins/winui/agent-plugin/skills/winui-design/SKILL.md)
- [WinUI code-review skill](https://github.com/microsoft/win-dev-skills/blob/main/plugins/winui/agent-plugin/skills/winui-code-review/SKILL.md)
- [WinUI UI-testing skill](https://github.com/microsoft/win-dev-skills/blob/main/plugins/winui/agent-plugin/skills/winui-ui-testing/SKILL.md)
- [WinUI analyzer source](https://github.com/microsoft/win-dev-skills/tree/main/src/tools/winui-analyzer)
- [WinUI Gallery](https://github.com/microsoft/WinUI-Gallery)
