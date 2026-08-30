# Improvement plan

This is the canonical ordered plan for turning PiDesk from a functional RPC chat client into a credible graphical replacement for Pi's TUI. Work should proceed in milestone order; visual polish must not outrun protocol correctness and observability.

## Product boundary

PiDesk will remain a native Windows front end over Pi's supported RPC mode. It will not fork Pi, duplicate its agent loop, manage provider credentials independently, or reproduce the terminal UI pixel-for-pixel.

## Milestone 0 — Protocol correctness and lifecycle

**Objective:** make UI and backend state impossible to desynchronise during ordinary operations.

- Honour `cancelled` from `new_session` before clearing the visible conversation.
- Suppress expected process-exit reporting during project changes and application shutdown.
- Replace raw `JsonElement` use in the ViewModel with typed commands, responses, and events in a dedicated session service.
- Serialize model and thinking changes so rapid selection cannot publish stale results.
- Preserve malformed-record diagnostics without permanently losing the stdout reader.
- Report process exit once, with buffered stderr context and a clear recovery action.

**Acceptance criteria**

- An extension-cancelled session change leaves the current conversation untouched.
- Repeated project switching produces no false error banner or orphaned Node process.
- Rapid model selection ends on the last selected model.
- Protocol unit tests cover response correlation, malformed records, clean EOF, timeout, and process exit.

## Milestone 1 — Faithful agent activity rendering

**Objective:** expose enough of Pi's work that a user can supervise a coding task without returning to the terminal.

- Introduce distinct view models for user text, assistant text, thinking, tool calls, tool results, diffs, retry notices, compaction notices, and errors.
- Render streaming tool updates, arguments, final output, and error details.
- Add expandable thinking and tool-result surfaces.
- Render Markdown and code blocks with selection and copy support.
- Render edit patches/diffs as first-class content rather than plain text.
- Give each conversation item a meaningful UI Automation name.

**Acceptance criteria**

- A read/edit/build task shows every tool transition and its final result.
- Failed tools show actionable output.
- Keyboard and screen-reader users can identify role, tool, state, and content.
- Long output is virtualized or collapsed without freezing the UI.

## Milestone 2 — Sessions and branching

**Objective:** provide the session operations that make Pi's interactive mode useful for sustained work.

- Restore messages after any session replacement with `get_messages`.
- Add current-session naming, HTML export, clone, fork, and tree navigation.
- Provide a session-open flow using `switch_session`.
- Decide how to list project sessions: request an upstream RPC listing command or implement a documented, read-only session index against Pi's session format.
- Surface extension cancellation without changing visible state.

**Acceptance criteria**

- A saved session can be opened and its active path rendered correctly.
- Fork and clone create the backend session Pi reports and update the UI atomically.
- Tree navigation preserves abandoned branches and labels.
- Session operations are covered by integration tests using temporary session storage.

## Milestone 3 — Input, queues, commands, and extension UI

**Objective:** reach practical interaction parity with Pi's editor and queue model.

- Display steering and follow-up queues from `queue_update`.
- Let the user explicitly choose steer versus follow-up while Pi is running.
- Restore cleared queue text to the composer when aborting.
- Add image attachment support.
- Discover skills, prompts, and extension commands with `get_commands` and provide slash-command completion.
- Implement fire-and-forget extension requests for widgets, title, status, and editor text without collapsing all status keys into one string.
- Add timeout-aware extension dialogs.

**Acceptance criteria**

- Queue contents and delivery mode are always visible.
- Abort restores unsent queued text.
- A representative RPC extension can use select, confirm, input, editor, notify, status, widget, title, and editor-text requests.

## Milestone 4 — Responsive and accessible Windows design

**Objective:** make the application robust across window sizes, themes, and input modes.

- Add small, medium, and large layout states; move secondary controls into overflow at narrow widths.
- Add Light, Dark, and High Contrast visual tests.
- Give loading, empty, error, disconnected, and permission/trust states distinct recovery paths.
- Add keyboard accelerators and visible focus verification.
- Review touch target sizes and screen-reader announcements for streaming updates.
- Decide whether sessions use a collapsible rail, master-detail list, or `TabView` based on tested workflows.

**Acceptance criteria**

- No clipping at supported minimum width.
- Automated accessibility audit finds no unnamed interactive controls or message items.
- Core flows pass in Light, Dark, and a Contrast theme.

## Milestone 5 — Distribution and compatibility

**Objective:** make installation and Pi compatibility predictable.

- Stop relying on an incidental global npm directory layout; resolve a documented Pi RPC entry point.
- Define and test the supported Pi version range and protocol compatibility behavior.
- Add release packaging, signing, and installation documentation.
- Add automated builds only after repository Actions are deliberately enabled and reviewed.

**Acceptance criteria**

- A clean supported machine can install and run PiDesk without a development checkout.
- Unsupported Pi versions fail before a session starts, with upgrade guidance.
- Release artifacts are reproducible and signed.

## Cross-cutting test matrix

Every milestone should extend tests rather than replace prior coverage:

- clean startup and shutdown
- project switching
- prompt, steering, follow-up, abort, and queue restoration
- tool success, tool failure, retry, and compaction
- extension dialog and fire-and-forget UI requests
- session cancellation, switch, fork, clone, and tree navigation
- narrow, default, and wide windows
- Light, Dark, and High Contrast
- keyboard-only and UI Automation paths

## Upstream opportunities

- Propose a Pi RPC command for listing sessions rather than coupling clients to session-directory internals.
- Propose a curated AI/coding-agent conversation sample for `winapp find-ui`.
- Extend `microsoft/win-dev-skills` session reporting to support Pi JSONL sessions.
- Add WinUI review guidance for DispatcherQueue initialization, same-channel RPC deadlocks, async selector races, and subprocess shutdown.
