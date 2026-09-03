# Improvement plan

This is the canonical ordered plan for turning PiDesk from a functional RPC chat client into a credible graphical alternative to Pi's TUI. It defines implementation order and milestone acceptance; the point-in-time assessment remains in [the design review](design-review.md).

Work must proceed in milestone order. Visual polish must not outrun protocol correctness, recoverability, and observability.

## Current status

**Current milestone:** Milestone 2 — Sessions and branching (entry investigation complete; blocked on upstream RPC listing and navigation)

Milestones 0 and 1 are complete against Pi 0.84.4. The shared transport is bounded and generation-safe; `PiSessionService` owns typed protocol and lifecycle handling, atomic snapshots, candidate project replacement, prompt acceptance, queue recovery, serialized state-changing commands, and generation-safe latest-selection-wins selectors. Conversation rendering reduces only typed RPC activity into distinct user, assistant, thinking, tool, diff, retry, compaction, and error presentation models. It correlates interleaved argument streams, restores thinking, tool arguments/results, and diffs from typed `get_messages`, exposes expandable selectable detail surfaces with bounded automation summaries, and defers collapsed detail creation so a 10,000-line output remains one responsive control. Markdown headings, lists, emphasis, inline code, and fenced code are rendered under a documented side-effect-free link/image/HTML policy. The deterministic protocol suite has 45 tests. A retained 17-scenario fake-RPC UI run covers keyboard expansion, streaming and final tool states, edit diffs, failed tools, selection/copy, bounded automation names, 10,000-line output and Stop responsiveness, safe Markdown, and clean shutdown; the earlier retained 16-scenario real-Pi smoke continues to cover startup, selectors, prompt/settle, abort, new session, project replacement, and parent/child shutdown.

| Slice | Status | Evidence |
|---|---|---|
| 0.1 Protocol test harness | Complete | Shared `PiDesk.Protocol` library, bounded strict JSONL, retained diagnostics, observer isolation, fake child, and boundary/normalization mutation tests. |
| 0.2 Reader and process lifecycle | Complete | Cancellation, timeout, EOF, crash, oversized stderr/stdout, stale-generation, repeated replacement, PID-exit tests, and generation/retention mutation evidence. |
| 0.3 Typed session service | Complete | Typed command/response/event coverage, pre-launch Pi 0.84.4 validation, explicit lifecycle tests, and no raw protocol traversal in UI code. |
| 0.4 Atomic startup and session replacement | Complete | Typed message restoration, atomic snapshot application, cancelled-session preservation, candidate load failure recovery, and candidate-before-current-stop process tests. |
| 0.5 Prompt, queue, and abort correctness | Complete | Pending/accepted/failed prompt state, failed-prompt composer recovery, typed queue updates, delivery-order abort restoration, and ambiguous clear-timeout recovery tests. |
| 0.6 Selector and command concurrency | Complete | Serialized mutation gate, selection/session versions, atomic selector results, delayed-response and replacement tests, explicit UI command policy, and real-Pi rapid-selection/abort smoke evidence. |

A milestone is complete only when its listed automated evidence passes and the relevant architecture or development documentation has been updated.

## Product boundary

PiDesk will remain a native Windows front end over Pi's supported RPC mode. It will not fork Pi, duplicate its agent loop, manage provider credentials independently, or reproduce the terminal UI pixel-for-pixel.

PiDesk owns graphical presentation and the atomic translation of confirmed backend state into visible UI state. Pi continues to own models, credentials, the agent loop, tools, extensions, queues, and persistent sessions.

## Correctness rules

These rules apply to every milestone:

- Do not commit a visible state replacement until Pi confirms it.
- Preserve the previous visible state when an operation is cancelled or fails.
- Represent optimistic operations, such as prompt submission, as pending or failed until Pi confirms them.
- Tag process and session activity with a generation so late responses and events cannot update a replacement session.
- Keep commands responsive without allowing concurrent operations to publish stale state.
- Treat malformed protocol input, subprocess stderr, extension requests, and restored session data as untrusted input.
- Never discard queued or composed user text merely because an operation failed or was aborted.
- Start behavioural changes at the typed `PiDesk.Protocol` boundary and add deterministic service or protocol evidence; do not reproduce the behaviour in the ViewModel.
- Keep presentational changes native to WinUI, prefer declarative XAML, and expose interactive state through accessible names and stable automation identifiers.

## Change verification

Before implementation, classify the change as behavioural, presentational, or both, and identify the affected typed operations and visible workflows. Behavioural work must preserve Pi as the authority and update protocol or service tests before UI verification. Pure presentation work need not manufacture protocol tests, but must continue to consume typed state rather than introduce a parallel behaviour path.

Before completion, run the deterministic protocol suite and analyzer build when their layers are affected. Exercise each changed visible workflow through the running application, inspect UI Automation state and logs, and use screenshots where layout matters. Report the commands, scenarios, themes, and input modes actually verified, together with any relevant path that was not tested. Real-provider prompts remain an explicit, bounded smoke test rather than part of the default deterministic suite.

## Milestone 0 — Protocol correctness and lifecycle

**Objective:** keep visible conversation, queue, selector, process, and backend session state synchronized during startup, ordinary operations, failure, and recovery.

Implement the following slices in order. Each slice must leave all earlier tests passing.

### 0.1 Protocol test harness

- Add a unit-test project that does not require WinUI startup or a real model request.
- Separate LF-delimited JSON parsing and response correlation from the concrete child process in a production library that the app and tests both reference; do not compile linked copies of production source into the test assembly.
- Add a deterministic fake RPC child or equivalent stream fixture for process integration tests.
- Impose a documented maximum JSONL record size and fail the affected generation cleanly when it is exceeded.
- Record timestamp, command type, correlation ID, and process generation in a bounded diagnostic sink without recording credentials, prompts, or model output; add session generation when the typed session service introduces it.
- Isolate diagnostic and error observers so a subscriber exception cannot leak a pending request, terminate the stdout reader, or alter process lifecycle.

**Slice evidence**

- Concurrent out-of-order responses reach the matching requests.
- An unknown event is retained in the bounded diagnostic sink without terminating the reader.
- An oversized unterminated or terminated record cannot cause unbounded memory growth.
- A throwing diagnostic or error observer cannot prevent request cleanup or stop subsequent records.
- Tests run locally without Pi authentication or provider cost.

### 0.2 Reader and process lifecycle

- Preserve malformed-record diagnostics and continue with the next valid stdout record.
- Distinguish intentional stop, project replacement, clean unexpected EOF, and process failure.
- Complete every pending request exactly once on response, timeout, cancellation, EOF, or process exit.
- Read and retain a bounded amount of stderr without first allocating an unbounded line, instead of presenting each line as a separate application error.
- Report an unexpected exit once, with exit code, relevant stderr context, and a reconnect action.
- Prevent output readers from a stopped generation from reporting events, diagnostics, or errors into a newer process.
- Track fake-child PIDs so tests can prove graceful exit or forced tree termination rather than inferring cleanup from client state.

**Slice evidence**

- A malformed record followed by a valid response does not lose the response.
- Intentional stop and repeated project replacement produce no error banner, and every recorded fake-child PID exits.
- Unexpected EOF and non-zero exit each produce one fault containing recovery guidance.
- Timeout, caller cancellation, and process exit complete all affected requests exactly once without hanging the test.
- Delayed output from a stopped generation is ignored after replacement.
- Oversized stderr remains bounded while preserving the newest useful context.

### 0.3 Typed session service

- Introduce typed commands, responses, and events for the protocol surface PiDesk currently uses.
- Keep JSON serialization, response validation, and Pi error handling inside the session service.
- Remove raw protocol parsing from `MainPageViewModel`; extension UI may use typed request payloads rather than exposing arbitrary records.
- Detect the installed Pi package version before launch from the resolved runtime metadata, then validate it against the explicitly supported protocol baseline before loading a session; do not assume RPC exposes a version command.
- Introduce explicit starting, connected, busy, stopping, disconnected, and faulted states in the service rather than deriving lifecycle from `Process.HasExited`.
- Log unknown additive fields and events without failing; fail closed with upgrade guidance when a required field or supported command is unavailable.

**Slice evidence**

- Unit tests cover every currently emitted command and every currently handled event.
- Unsupported Pi versions fail before a prompt can be sent and identify the supported version range.
- The ViewModel contains no `JsonElement` protocol traversal.

### 0.4 Atomic startup and session replacement

- Load session state and `get_messages` before publishing a started or replaced session to the UI.
- Honour `cancelled` from `new_session` and later replacement commands before clearing visible state.
- Replace the destructive `StartAsync` flow with prepare/commit replacement: start and validate the candidate process before stopping the currently usable process.
- Preserve the existing process, conversation, session summary, and composer when candidate startup or loading fails.
- Apply project, new-session, and future switch/clone/fork results as one visible state transition.
- Ignore responses and events belonging to an older process or session generation.

**Slice evidence**

- Starting in a persistent session renders its active message path rather than an empty conversation.
- An extension-cancelled new session leaves conversation, summary, usage, and composer untouched.
- A failed project change leaves the prior usable session visible and connected.
- A late event from the replaced process cannot alter the current conversation or error state.

### 0.5 Prompt, queue, and abort correctness

- Distinguish pending, accepted, and failed user messages instead of treating local insertion as backend acceptance.
- Preserve composed text when prompt submission fails.
- Capture text returned by `clear_queue` and restore it to the composer when aborting.
- Keep full steer/follow-up queue presentation in Milestone 3, but prevent queue data loss here.

**Slice evidence**

- A rejected prompt is visibly failed or restored and is never presented as successfully accepted.
- Abort restores all unsent cleared queue text in delivery order.
- Timeout or disconnect during send/abort does not silently discard user text.

### 0.6 Selector and command concurrency

- Serialize model and thinking changes with latest-selection-wins semantics.
- Refresh thinking levels only for the model whose selection remains current.
- Define which operations are disabled, cancelled, or queued during startup, replacement, and shutdown.
- Observe and report every asynchronous command failure; do not use untracked fire-and-forget mutations.

**Slice evidence**

- Deliberately reordered model responses finish on the last selected model and its valid thinking level.
- A stale selector completion cannot overwrite a replacement session's state.
- Repeated send, abort, new-session, and project-change attempts follow the documented operation policy without deadlock.

### Milestone 0 acceptance

Completed against Pi 0.84.4: all six slices pass in the 37-test deterministic suite, the x64 analyzer build is warning-free, and the 16-scenario real-Pi UI smoke confirms startup, prompt, abort, new session, project replacement, and clean shutdown. Retained evidence names no prompt content or credentials.

## Milestone 1 — Faithful agent activity rendering

**Objective:** expose enough of Pi's work that a user can supervise a coding task without returning to the terminal.

- Introduce distinct view models for user text, assistant text, thinking, tool calls, tool results, diffs, retry notices, compaction notices, and errors.
- Render streaming tool updates, validated arguments, final output, and error details.
- Add expandable thinking and tool-result surfaces.
- Render Markdown and code blocks with selection and copy support.
- Define Markdown handling for external links, local file links, images, raw HTML, and unsupported content before enabling it.
- Render edit patches/diffs as first-class content rather than plain text.
- Give each conversation item a UI Automation name containing its role or tool, state, and a bounded content summary.
- Collapse or incrementally render large tool output while retaining access to the complete textual result.

**Acceptance criteria**

- A deterministic read/edit/build fixture shows every tool transition, arguments, streaming update, final result, and edit patch in order.
- Failed tools expose the tool name, failed state, and copyable diagnostic output.
- Keyboard and screen-reader users can identify role, tool, state, and content and can expand, collapse, select, and copy without pointer input.
- A fixture containing at least 10,000 output lines remains operable during streaming, does not block Stop, and does not create one visible control per collapsed line.
- External or unsupported Markdown content follows the documented content policy rather than executing or opening silently.

### Milestone 1 acceptance

Completed with 45 passing deterministic protocol tests, a warning-free x64 analyzer build, and the retained 17-scenario deterministic UI Automation evidence in `docs/evidence/milestone1-ui-test-results.json`. The UI fixture launches a Debug-only fake Pi RPC child and reaches the presentation exclusively through `PiSessionService`, typed events, typed `get_messages`, and `PiActivityReducer`. No model request, Pi session-file access, or ViewModel-synthesized backend outcome is used.

## Milestone 2 — Sessions and branching

**Objective:** provide the session operations that make Pi's interactive mode useful for sustained work.

### Entry investigation

Before implementation, verify switch, fork, clone, naming, export, and tree-navigation semantics against the supported Pi source and tests. Resolve session listing by either securing an upstream RPC command or documenting and testing a read-only index against the supported session format. Record the decision and fallback before building the browser UI.

Investigation completed against the installed Pi 0.84.4 documentation, declarations, compiled source, source maps, and the corresponding upstream session/RPC tests:

- `switch_session` accepts a session-file path, can be cancelled by `session_before_switch`, and rebinds RPC only after successful runtime replacement. `fork` accepts an active-branch user entry, creates a new session before that prompt, and returns the prompt text. `clone` forks at the current leaf and fails for an empty or not-yet-persisted session. Both can be cancelled by `session_before_fork`.
- `get_entries` returns the append-only history, `get_tree` returns all branches plus resolved labels, and both report the active `leafId`. Pi's tested `navigateTree` operation preserves abandoned entries, optionally appends a branch summary, and returns editor text when selecting a user message, but **Pi 0.84.4 exposes no RPC command for it**.
- `set_session_name` trims and rejects an empty name; the confirmed name must be read from `get_state`. `export_html` returns the written path but uses an overwriting write, so PiDesk must check the selected destination and obtain explicit overwrite confirmation before sending the command.
- `SessionManager.list(cwd)` is the supported current-project discovery API and is tested with isolated temporary storage, but **Pi 0.84.4 exposes no session-listing RPC command**. Its RPC surface contains `switch_session` but requires a path the client cannot discover through RPC.

**Decision:** do not build a PiDesk session-file parser or inspect `~/.pi/agent/sessions`. That would couple PiDesk to Pi-owned persistence, duplicate `SessionManager` policy, and violate the process boundary. Before session-browser UI work, secure upstream RPC support for (1) current-project session listing with the existing `SessionInfo` fields and (2) `navigate_tree` with `targetId`, summary options, and the tested `editorText`/cancellation result. The fallback is to keep Milestone 2 blocked on Pi 0.84.4 rather than silently weaken the boundary. Naming, export, fork, clone, and read-only tree protocol adapters may be developed and tested independently, but they do not satisfy the milestone without those two commands.

### Implementation

- Add current-session naming, HTML export, clone, fork, and tree navigation.
- Provide a session-open flow using `switch_session`.
- Reuse Milestone 0's atomic replacement and cancellation behavior for every operation.
- Display loading, cancellation, conflict, and failure states without replacing the active conversation prematurely.

**Acceptance criteria**

- A saved session can be opened and its active path rendered correctly.
- Fork and clone create exactly the backend session Pi reports and update the UI atomically.
- Tree navigation preserves abandoned branches and labels.
- Naming and export report the confirmed session and destination and do not overwrite an existing file without explicit confirmation.
- Integration tests use temporary session storage and leave the user's real sessions untouched.

## Milestone 3 — Input, queues, commands, and extension UI

**Objective:** reach practical interaction parity with Pi's editor and queue model.

- Display steering and follow-up queues from `queue_update`.
- Let the user explicitly choose steer versus follow-up while Pi is running.
- Add image attachment support with type, size, and read-failure validation.
- Discover skills, prompts, and extension commands with `get_commands` and provide slash-command completion.
- Implement fire-and-forget extension requests for widgets, title, status, and editor text without collapsing all status keys into one string.
- Isolate extension request lifetimes by process/session generation.
- Handle malformed, simultaneous, duplicate, late, cancelled, and timed-out extension requests without blocking the stdout reader.

**Acceptance criteria**

- Queue contents, order, and delivery mode always match the latest backend update.
- Abort restores unsent queued text to the composer.
- Command completion reflects the selected project's discovered commands and invalidates on project replacement.
- A deterministic extension fixture exercises select, confirm, input, editor, notify, status, widget, title, and editor-text requests.
- Dialog timeout, project replacement, and a late extension response cannot deadlock event processing or update the wrong session.

## Milestone 4 — Responsive and accessible Windows design

**Objective:** make the application robust across window sizes, themes, and input modes.

- Add small, medium, and large layout states; move secondary controls into overflow at narrow widths.
- Add Light, Dark, and High Contrast visual tests.
- Give loading, empty, error, disconnected, permission/trust, and reconnecting states distinct recovery paths.
- Add keyboard accelerators and visible focus verification.
- Review touch target sizes and screen-reader announcements for streaming updates.
- Decide whether sessions use a collapsible rail, master-detail list, or `TabView` based on tested workflows.

**Acceptance criteria**

- No content or recovery action is clipped at the documented minimum supported window size.
- Automated accessibility audit finds no unnamed interactive controls or conversation items and reports no critical violations.
- Core startup, prompt, stop, recovery, and session flows pass in Light, Dark, and a Contrast theme.
- All core flows are operable by keyboard alone with visible focus.

## Milestone 5 — Distribution and compatibility

**Objective:** make installation, upgrades, and Pi compatibility predictable.

- Document supported Windows versions and processor architectures.
- Stop relying on an incidental global npm directory layout; resolve a documented Pi RPC entry point.
- Define whether Node and Pi are prerequisites or packaged dependencies.
- Extend the early protocol check into a tested supported Pi version range and compatibility policy.
- Add release packaging, signing, upgrade, rollback, and uninstallation documentation, including session/settings preservation.
- Add automated builds only after repository Actions are deliberately enabled and reviewed.

**Acceptance criteria**

- A clean supported machine can install and run PiDesk without a development checkout.
- Unsupported or missing Pi installations fail before a session starts, with specific installation or upgrade guidance.
- Upgrade and uninstall tests preserve Pi-owned sessions and handle PiDesk-owned settings as documented.
- Release artifacts are reproducible, signed, and produced for every documented architecture.

## Cross-cutting test strategy

Every milestone extends rather than replaces prior evidence.

### Unit tests

- protocol serialization and parsing
- response correlation and timeouts
- state reducers and generation guards
- command concurrency and cancellation
- rendering model transformation

### Process integration tests

- clean startup and shutdown
- malformed JSONL, stderr, EOF, and crashes
- process and session replacement, including candidate-start failure and stale-generation output
- fake-child PID cleanup and caller cancellation
- maximum stdout-record and stderr-buffer boundaries
- prompt, steering, follow-up, abort, and queue restoration
- tool success, tool failure, retry, and compaction
- extension request lifecycle
- session cancellation, switch, fork, clone, and tree navigation

### UI Automation tests

- narrow, default, and wide windows
- Light, Dark, and High Contrast
- keyboard-only and accessibility paths
- disconnected and recovery states
- one bounded real-model smoke test where deterministic fixtures cannot prove integration

Tests must use deterministic protocol fixtures by default. Real provider requests are reserved for the smallest end-to-end smoke test because they incur cost and can vary independently of PiDesk.

## Diagnostics and recovery

PiDesk will use explicit lifecycle states: starting, connected, busy, stopping, disconnected, and faulted. Unexpected failures must retain enough bounded diagnostic context to distinguish protocol errors, Pi rejection, timeout, and process exit. Recovery must be explicit: retry the operation, reconnect the same project/session, choose another project, or copy diagnostics as appropriate.

Diagnostics must include timestamps, command types, correlation IDs, process/session generations, exit codes, and bounded stderr context. They must not persist credentials and should avoid prompt or model-output content unless the user deliberately copies it.

## Upstream opportunities

These do not block the current milestone unless promoted by an entry investigation:

- Propose a Pi RPC command for listing sessions rather than coupling clients to session-directory internals.
- Propose a curated AI/coding-agent conversation sample for `winapp find-ui`.
- Extend `microsoft/win-dev-skills` session reporting to support Pi JSONL sessions.
- Add WinUI review guidance for DispatcherQueue initialization, same-channel RPC deadlocks, async selector races, and subprocess shutdown.
