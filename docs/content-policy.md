# Conversation content policy

This reference defines how PiDesk displays Markdown and linked content received through Pi RPC. Pi's output is untrusted presentation input; displaying it never grants permission to execute code, read files, fetch images, or open a destination.

## Supported Markdown

PiDesk renders these constructs in assistant and user text:

- headings
- unordered list markers
- bold text
- inline code
- fenced code blocks

Text remains selectable so it can be copied with standard keyboard commands. Tool arguments, tool output, and diffs use literal monospace text rather than Markdown interpretation.

## Links

Markdown links are displayed as their label followed by the literal destination. They are not active controls and PiDesk does not open them automatically.

This rule applies equally to:

- `https:` and other external destinations
- local and relative file paths
- custom URI schemes
- fragment links

A future explicit open action must validate the scheme and require a deliberate user invocation; rendering alone must remain side-effect free.

## Images

Markdown images render only a textual `[Image: alternative text]` placeholder. PiDesk does not fetch remote images, decode data URLs, or read local image paths while rendering conversation text.

## Raw HTML and unsupported syntax

Raw HTML is shown literally and is never hosted in a browser control. Unsupported Markdown syntax is retained as selectable text rather than discarded, interpreted as XAML, or executed.

## Large content

Thinking, tool results, arguments, and diffs are collapsed by default. Expanding a tool result exposes its complete RPC-provided text in one selectable control; PiDesk does not create one control per output line.
