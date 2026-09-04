# Pi TUI capability gap review

This review records what PiDesk does and does not expose compared with Pi 0.85.0 interactive mode. It is a point-in-time capability assessment, not the implementation schedule. The canonical work order and acceptance criteria remain in [the improvement plan](improvement-plan.md).

## Evidence and comparison boundary

The comparison was made against the installed `@earendil-works/pi-coding-agent` 0.85.0 package, especially its README and `docs/rpc.md`, `docs/sessions.md`, `docs/settings.md`, `docs/keybindings.md`, and `docs/tui.md`, plus the compiled interactive and RPC controllers. PiDesk source and its typed protocol tests were checked for the corresponding commands and events.

PiDesk is not intended to emulate a terminal. The relevant parity target is the work a user can complete and supervise, using native Windows interaction where appropriate. Pi continues to own models, credentials, settings, tools, extensions, resources, the agent loop, and persistent sessions.

## How the roadmap is classified

Not every improvement in the roadmap is a TUI feature to migrate. Each item belongs to one of four classes:

| Class | Meaning | Examples | Direction |
|---|---|---|---|
| TUI workflow parity | A useful interactive-mode outcome that PiDesk does not yet provide. | Resume/fork/tree, follow-up queues, images, command completion, compaction, transcript search. | Implement through typed RPC and native WinUI. |
| Native desktop adaptation | The TUI outcome matters, but its terminal interaction does not. | Session browser, file picker, drag/drop, keyboard accelerators, status and transcript surfaces. | Design a Windows-native equivalent rather than porting TUI components. |
| PiDesk engineering | Required because PiDesk is a separate Windows RPC process, not because the TUI has it. | Atomic replacement, backend selection, diagnostics, responsive accessibility, packaging, compatibility checks. | Implement and test locally in PiDesk. |
| Deliberate exclusion | Terminal mechanics or Pi administration that should remain upstream. | ANSI/fullscreen terminal behavior, custom TUI components, credentials, package management, arbitrary settings editing. | Do not migrate unless Pi later exposes a dedicated safe integration contract. |

The milestone plan intentionally contains the first three classes. Only TUI workflow parity is a direct capability migration; native adaptations and PiDesk engineering are separate product work.

## Current coverage

PiDesk already covers the central agent loop:

- prompt acceptance, steering while busy, abort, and queue recovery;
- model and thinking-level selection;
- streamed assistant text, thinking, tool arguments and results, diffs, retries, compaction, and errors;
- expandable and selectable detail with bounded large-output presentation;
- new sessions and atomic project or Windows/WSL backend replacement;
- session name summary, cost and context percentage;
- extension select, confirm, input, and multiline editor dialogs;
- basic extension notifications and status text.

This is enough to supervise an ordinary coding turn, but not enough to replace the TUI for sustained session management or its richer input and operational workflows.

## RPC capabilities not yet exposed by PiDesk

These can be implemented without reading Pi-owned files or moving domain logic into PiDesk.

| Workflow | Pi 0.85.0 RPC surface | PiDesk gap |
|---|---|---|
| Name current session | `set_session_name`, `get_state` | No naming action. |
| Export session | `export_html` | No destination picker or overwrite confirmation. |
| Fork and clone | `get_fork_messages`, `fork`, `clone` | No selector or atomic replacement flow. |
| Inspect history and branches | `get_entries`, `get_tree` | No typed tree model or branch display. |
| Open a known session | `switch_session` | No typed operation or known-path flow. |
| Follow-up input | `follow_up` or `prompt` with `followUp` | Busy Send always steers. |
| Queue supervision | `queue_update`, `clear_queue` | Queue is retained only for abort recovery and is not visible. |
| Delivery policy | `set_steering_mode`, `set_follow_up_mode` | Current modes are ignored and cannot be changed. |
| Image input | image blocks on prompt, steer, and follow-up | No picker, clipboard paste, drag/drop, validation, or preview. |
| Skills, prompts, extension commands | `get_commands` | No slash completion or command catalogue. |
| Manual compaction | `compact` | Events render, but the user cannot start compaction. |
| Automatic compaction | `set_auto_compaction`, `get_state` | State is ignored and no toggle is exposed. |
| Retry policy | `set_auto_retry`, `abort_retry` | Retry events render, but controls are absent. |
| Direct shell execution | `bash`, `abort_bash`, `bash_execution_update` | No equivalent of the TUI's `!` command workflow. |
| Full session information | `get_session_stats`, `get_state` | Only cost and context percentage are retained. |
| Copy latest response | `get_last_assistant_text` | No explicit copy-last-response command. |
| Quick model/thinking cycling | `cycle_model`, `cycle_thinking_level` | Selectors exist, but no accelerator-driven cycling. |

The richer state currently discarded includes session file and ID, message and pending-message counts, steering and follow-up modes, auto-compaction state, token/cache breakdowns, tool counts, context tokens, and context-window size.

## Incomplete extension UI support

PiDesk recognizes every documented RPC extension UI method name, but only part of the contract reaches visible state.

Dialog methods `select`, `confirm`, `input`, and `editor` are implemented. `notify` and `setStatus` are reduced to one status string. The following remain incomplete or ignored:

- keyed status entries rather than one value overwriting another;
- `setWidget` content, key, and above/below-editor placement;
- `setTitle` window title changes;
- `set_editor_text` composer replacement;
- distinct informational and warning notifications;
- malformed, duplicate, late, timed-out, and replacement-generation request handling.

The typed request model does not yet retain `statusKey`, `widgetKey`, `widgetLines`, or widget placement. Completing this documented RPC subset is appropriate. Arbitrary `ctx.ui.custom()` components, overlays, custom editors, headers, footers, and working indicators are explicitly unavailable in RPC mode and are not a PiDesk compatibility target.

## Upstream RPC blockers

### Session discovery

`switch_session` requires a session path, but RPC cannot list current-project sessions. PiDesk must not inspect `~/.pi/agent/sessions` or reproduce `SessionManager` policy. The planned additive `list_sessions` command remains necessary.

### Branch navigation

`get_tree` can inspect all branches and labels, but RPC cannot move the active leaf. The planned `navigate_tree` command remains necessary to reproduce `/tree` selection, cancellation, optional abandoned-branch summary, and returned editor text.

### Other unavailable TUI operations

Exact TUI parity would require additional upstream decisions beyond the minimum Milestone 2 delta:

- retrieve one queued message back into the editor rather than clearing the whole queue;
- edit an arbitrary tree-entry label independently of navigation;
- delete or rename a session selected in the session browser;
- import and share sessions;
- report the complete set of loaded context and resource files;
- mutate general Pi settings at runtime.

These are deferred rather than implemented through direct file access.

## Desktop workflows without direct RPC equivalents

Some TUI behavior should become native PiDesk behavior rather than protocol behavior:

- transcript search, previous/next prompt navigation, and jump to latest;
- prompt history and richer multiline editing;
- project-file search and path insertion;
- clipboard image paste and image drag/drop;
- global expand/collapse commands for thinking and tool output;
- visible keyboard accelerators and copy commands;
- responsive layout, Light/Dark/High Contrast support, and screen-reader announcements.

Project trust needs special treatment. Interactive Pi can ask before loading project-local settings and resources, while RPC mode cannot prompt and may ignore them under the default `ask` policy. PiDesk can offer a candidate-launch choice between Pi's saved/default policy, one-run approval, and one-run rejection, mapping only the latter two to Pi's supported `--approve` and `--no-approve` options. It must not inspect, interpret, or modify Pi's trust store itself.

## Deliberate exclusions

PiDesk should not reproduce terminal mechanics such as ANSI themes, alternate-screen modes, terminal image protocols, Kitty key events, terminal mouse capture, or terminal custom components. Native WinUI controls should provide equivalent user outcomes where useful.

Provider login/logout, llama.cpp management, Pi package installation, arbitrary settings editing, and resource reload remain Pi-owned. They should stay external until Pi exposes a dedicated safe integration surface. PiDesk must not parse credential, settings, package, trust, or session files to fill those gaps.

## Product implications

The comparison supports five remaining implementation milestones:

1. sessions and branching;
2. input, queues, commands, images, and extension UI;
3. operational supervision and desktop productivity;
4. responsive and accessible Windows design;
5. distribution and compatibility.

Narrowly scoped upstream RPC additions are enabling work rather than a separate PiDesk milestone. The first two remain `list_sessions` and `navigate_tree`. Other protocol proposals should be justified by a concrete workflow and kept separate from that minimum patch.
