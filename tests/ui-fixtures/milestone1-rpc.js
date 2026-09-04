const readline = require("readline");

let isStreaming = false;
let promptCount = 0;
let runVersion = 0;
const backendArgument = process.argv.indexOf("--fixture-backend");
const backendName = backendArgument >= 0 ? process.argv[backendArgument + 1] : "Windows";

function write(record) {
  process.stdout.write(JSON.stringify(record) + "\n");
}

function respond(command, data) {
  const record = { type: "response", id: command.id, command: command.type, success: true };
  if (data !== undefined) record.data = data;
  write(record);
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

async function runActivity(version) {
  isStreaming = true;
  write({ type: "agent_start" });
  write({ type: "message_update", assistantMessageEvent: { type: "thinking_start", contentIndex: 0 } });
  write({ type: "message_update", assistantMessageEvent: { type: "thinking_delta", contentIndex: 0, delta: "Inspecting deterministic fixture" } });
  await delay(350);
  if (version !== runVersion) return;
  write({ type: "message_update", assistantMessageEvent: { type: "thinking_end", contentIndex: 0, content: "Inspecting deterministic fixture" } });

  write({ type: "message_update", assistantMessageEvent: { type: "toolcall_start", contentIndex: 1, id: "call-read", toolName: "read" } });
  write({ type: "message_update", assistantMessageEvent: { type: "toolcall_delta", contentIndex: 1, delta: "{\"path\":\"fixture.txt\"}" } });
  write({ type: "message_update", assistantMessageEvent: { type: "toolcall_end", contentIndex: 1, toolCall: { type: "toolCall", id: "call-read", name: "read", arguments: { path: "fixture.txt" } } } });
  write({ type: "tool_execution_start", toolCallId: "call-read", toolName: "read", args: { path: "fixture.txt" } });
  await delay(700);
  if (version !== runVersion) return;
  write({ type: "tool_execution_update", toolCallId: "call-read", toolName: "read", args: { path: "fixture.txt" }, partialResult: { content: [{ type: "text", text: "STREAMING-CHECKPOINT" }], details: { phase: "read" } } });
  await delay(2500);
  if (version !== runVersion) return;
  write({ type: "tool_execution_end", toolCallId: "call-read", toolName: "read", args: { path: "fixture.txt" }, result: { content: [{ type: "text", text: "READ-FINAL-SUCCESS" }], details: { lines: 1 } }, isError: false });

  write({ type: "tool_execution_start", toolCallId: "call-edit", toolName: "edit", args: { path: "fixture.txt" } });
  write({ type: "tool_execution_update", toolCallId: "call-edit", toolName: "edit", args: { path: "fixture.txt" }, partialResult: { content: [{ type: "text", text: "Applying deterministic edit" }], details: { phase: "write" } } });
  await delay(350);
  if (version !== runVersion) return;
  write({ type: "tool_execution_end", toolCallId: "call-edit", toolName: "edit", args: { path: "fixture.txt" }, result: { content: [{ type: "text", text: "EDIT-FINAL-SUCCESS" }], details: { diff: "-before\n+after", patch: "--- fixture.txt\n+++ fixture.txt", firstChangedLine: 1 } }, isError: false });

  write({ type: "tool_execution_start", toolCallId: "call-build", toolName: "bash", args: { command: "exit 7" } });
  write({ type: "tool_execution_end", toolCallId: "call-build", toolName: "bash", args: { command: "exit 7" }, result: { content: [{ type: "text", text: "BUILD-FINAL-ERROR" }], details: { exitCode: 7 } }, isError: true });
  write({ type: "message_end", message: { role: "assistant", content: [{ type: "text", text: "Fixture activity complete" }], stopReason: "stop" } });
  isStreaming = false;
  write({ type: "agent_settled" });
}

async function runLargeOutput(version) {
  isStreaming = true;
  write({ type: "agent_start" });
  write({ type: "tool_execution_start", toolCallId: "call-large", toolName: "bash", args: { command: "large-output" } });
  const output = Array.from({ length: 10000 }, (_, index) => `large line ${index + 1}`).join("\n");
  write({ type: "tool_execution_update", toolCallId: "call-large", toolName: "bash", args: { command: "large-output" }, partialResult: { content: [{ type: "text", text: output }], details: { lineCount: 10000 } } });
  await delay(15000);
  if (version !== runVersion) return;
  write({ type: "tool_execution_end", toolCallId: "call-large", toolName: "bash", args: { command: "large-output" }, result: { content: [{ type: "text", text: output }], details: { lineCount: 10000 } }, isError: false });
  isStreaming = false;
  write({ type: "agent_settled" });
}

const markdown = "# Safe Markdown\n- **bold item** with `inline code`\n[external](https://example.invalid/path)\n[local](C:\\\\private\\\\file.txt)\n![remote](https://example.invalid/image.png)\n<img src=\"https://example.invalid/tracker.png\">\n```powershell\nWrite-Output SAFE-CODE\n```\n~~unsupported~~";

readline.createInterface({ input: process.stdin }).on("line", line => {
  if (!line.trim()) return;
  const command = JSON.parse(line);
  switch (command.type) {
    case "get_state":
      respond(command, { model: { provider: "fixture", id: "deterministic", name: "Deterministic fixture" }, thinkingLevel: "medium", sessionId: `ui-fixture-${backendName}`, sessionName: `${backendName} fixture`, isStreaming });
      break;
    case "get_available_models":
      respond(command, { models: [{ provider: "fixture", id: "deterministic", name: "Deterministic fixture" }] });
      break;
    case "get_available_thinking_levels":
      respond(command, { levels: ["off", "medium"] });
      break;
    case "get_messages":
      respond(command, { messages: [{ role: "assistant", content: [{ type: "text", text: backendName === "Windows" ? markdown : `CONFIRMED-BACKEND: ${backendName}` }], stopReason: "stop" }] });
      break;
    case "get_session_stats":
      respond(command, { cost: 0, contextUsage: { percent: 1 } });
      break;
    case "prompt": {
      respond(command);
      const version = ++runVersion;
      promptCount += 1;
      if (promptCount === 1) void runActivity(version);
      else void runLargeOutput(version);
      break;
    }
    case "abort":
      respond(command);
      runVersion += 1;
      if (isStreaming) {
        isStreaming = false;
        write({ type: "agent_settled" });
      }
      break;
    case "clear_queue":
      respond(command, { steering: [], followUp: [] });
      break;
    default:
      respond(command, command.type === "new_session" ? { cancelled: true } : undefined);
      break;
  }
});
