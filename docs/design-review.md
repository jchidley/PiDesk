# Design review

This review records the current quality and completeness of PiDesk against Pi 0.84.4 and the upstream Microsoft WinUI development skills. It is a point-in-time assessment, not the implementation plan; planned work belongs in [the improvement plan](improvement-plan.md).

## Verdict

PiDesk is a sound native WinUI proof of concept and a useful Pi RPC chat client. It is not yet a full graphical replacement for Pi's interactive TUI.

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
- The RPC client correlates responses, streams events, handles Pi extension dialogs, and now performs clean child-process shutdown.
- The analyzer build completes with no warnings, and the end-to-end UI suite passes 11 tests.

## High-priority findings

### Cancelled session changes are ignored

`MainPageViewModel.NewSessionAsync` clears UI state without inspecting the `cancelled` value returned by Pi. An extension can therefore keep the backend on the old session while PiDesk displays a blank new session.

### Session functionality is too limited

Pi RPC exposes switching, messages, entries, tree navigation, fork, clone, naming, and export. PiDesk exposes only New session and does not restore history after session replacement.

### Coding activity is reduced to plain text

The conversation currently presents assistant text plus `tool completed` or `tool failed`. It omits thinking, tool arguments, streaming output, final results, diffs, structured retry and compaction state, Markdown, and code formatting. This is the largest gap for a coding-agent UI.

### Expected exits can become errors

The reader reports any Pi stdout EOF as an error, including the intentional stop used when changing project. Expected and unexpected process exits need separate paths.

## Other findings

- The fixed header columns have no responsive breakpoints and can clip at narrow widths.
- Queue state is not displayed; queued messages cleared during abort are not restored.
- Rapid model or thinking selection can race because changes are fire-and-forget.
- Each stderr line becomes a user-visible error, and a malformed stdout record can terminate the reader.
- The ViewModel contains extensive raw JSON protocol parsing and should depend on a typed session service.
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
- Test coverage remains happy-path and Dark-theme biased; it does not yet cover narrow layout, High Contrast, extension UI, folder switching, queues, or session cancellation.
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
