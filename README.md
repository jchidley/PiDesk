# PiDesk

PiDesk is a native WinUI 3 front end for the [Pi coding agent](https://pi.dev). It runs Pi's supported RPC mode as a child process and offers a graphical alternative to Pi's interactive TUI while retaining Pi's models, credentials, sessions, tools, skills, extensions, and project instructions.

![PiDesk conversation view](docs/images/current-ui.png)

## Quick start

1. Install and authenticate Pi, and ensure `pi.cmd` is on `PATH`.
2. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [WinApp CLI 0.6+](https://github.com/microsoft/winappcli), and enable Windows Developer Mode.
3. In PowerShell 7, run `./BuildAndRun.ps1`.
4. Choose a project folder, select a model and thinking level, then describe the task.

The command remains attached while PiDesk is open, so shell elapsed time includes application runtime.

## Current capabilities

- Streaming assistant text, thinking, tool arguments/results, diffs, retries, compaction, and errors
- Expandable selectable detail, safe Markdown, and bounded large-output presentation
- Project-folder selection with cwd-bound persistent Pi sessions
- Atomic selection of native Windows or discovered WSL Pi backends
- Model and thinking-level selection
- Acceptance-aware prompts, steering while Pi is working, abort, and queue recovery
- Stop, new-session, cost, and context controls
- Pi extension dialogs for select, confirm, input, and editor requests
- Keyboard access, UI Automation identifiers, and system theme resources

PiDesk is currently a functional RPC client but does not yet provide full graphical feature parity with Pi's TUI. It complements rather than deprecates Pi's upstream CLI and TUI. Session browsing and branching, visible steering/follow-up queues, images, command completion, complete extension UI, operational controls, responsive layouts, and release packaging are tracked in the improvement plan.

## Documentation

- [Architecture](docs/architecture.md)
- [Current design review](docs/design-review.md)
- [Pi TUI capability gap review](docs/tui-gap-review.md)
- [Improvement plan](docs/improvement-plan.md)
- [Development and testing](docs/development.md)

## Verification

Build with the bundled WinUI analyzer without launching:

```powershell
./BuildAndRun.ps1 . --no-launch --arch x64
```

Run the UI tests against a launched process:

```powershell
./ui-tests.ps1 -AppPid <PID>
```

The UI test sends one small model prompt and may incur a small provider charge. The latest recorded result is in [`docs/evidence/ui-test-results.json`](docs/evidence/ui-test-results.json).

## About This Code

Almost all of this code is AI/LLM-generated. It's best used as a source of
inspiration for your own AI/LLM efforts rather than as a traditional library.

**This is personal alpha software.** All my GitHub projects should be considered
experimental. If you want to use them:

- **Pin to a specific commit** — don't track `main`, it changes without warning
- **Use AI/LLM to adapt** — without AI assistance, these projects are hard to use
- **Treat as inspiration** — build your own version rather than depending on mine

**Suggestions welcome** — If you have ideas for improvements or changes, I'd be
delighted to read them and use them as inspiration for my own efforts.

**Why not a library?** These days it's often quicker to use AI/LLM to build your
own than to integrate traditional libraries. My use of AI/LLM is inspired by
these people and posts:

- [Simon Willison's Weblog](https://simonwillison.net/) — Essential reading on
  LLMs, prompt engineering, and building with AI
- [CLI over MCP](https://lucumr.pocoo.org/2025/8/18/code-mcps/) — Armin Ronacher
  on why command-line tools are better integration points than custom protocols
- [Build It Yourself](https://lucumr.pocoo.org/2025/12/22/a-year-of-vibes/) —
  Armin Ronacher: "With our newfound power from agentic coding tools, you can
  build much of this yourself..."
- [Stop Making TUIs](https://sockpuppet.org/blog/2026/08/20/stop-making-tuis/) —
  Thomas Ptacek on retaining CLIs for automation while making native graphical
  interfaces practical with coding agents
- [Shipping at Inference Speed](https://steipete.me/posts/2025/shipping-at-inference-speed) —
  Peter Steinberger on the new workflow of building with AI assistance
- [Year in Review 2025](https://mariozechner.at/posts/2025-12-22-year-in-review-2025/) —
  Mario Zechner on AI-assisted development

**What I use:** Currently Anthropic's Claude Opus, evaluating OpenAI's GPT Codex
as an alternative.

## License

This project is dual-licensed under the terms of both the MIT license and the
Apache License (Version 2.0).

See [LICENSE-APACHE](LICENSE-APACHE) and [LICENSE-MIT](LICENSE-MIT) for details.

### Contribution

Unless you explicitly state otherwise, any contribution intentionally submitted
for inclusion in this project by you, as defined in the Apache-2.0 license,
shall be dual licensed as above, without any additional terms or conditions.
