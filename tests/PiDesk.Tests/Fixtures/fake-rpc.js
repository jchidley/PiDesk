const fs = require("fs");
const behavior = process.argv[2] ?? "normal";
const lifecyclePath = process.argv[3];
let buffer = "";
let pendingNewSession = null;
let currentModel = { provider: "test", id: "model", name: "Test Model" };
let currentThinkingLevel = "medium";

function respond(command, data = undefined) {
  const response = { type: "response", id: command.id, command: command.type, success: true };
  if (data !== undefined) response.data = data;
  process.stdout.write(JSON.stringify(response) + "\n");
}

if (lifecyclePath) {
  fs.appendFileSync(lifecyclePath, `start ${process.pid}\n`);
  process.on("exit", () => fs.appendFileSync(lifecyclePath, `exit ${process.pid}\n`));
}

process.stdin.setEncoding("utf8");
process.stdin.on("data", chunk => {
  buffer += chunk;
  while (true) {
    const newline = buffer.indexOf("\n");
    if (newline < 0) break;
    const line = buffer.slice(0, newline).replace(/\r$/, "");
    buffer = buffer.slice(newline + 1);
    if (!line.trim()) continue;

    const command = JSON.parse(line);
    if (lifecyclePath) fs.appendFileSync(lifecyclePath, `command ${command.type} ${JSON.stringify(command)}\n`);
    if (command.type === "get_state") {
      if (behavior === "malformed") process.stdout.write("not-json\n");
      process.stdout.write(JSON.stringify({
        type: "response", id: command.id, command: command.type,
        success: true, data: {
          model: currentModel,
          thinkingLevel: currentThinkingLevel, sessionId: "test-session", sessionName: "Test", isStreaming: false
        }
      }) + "\n");
      if (behavior === "unknown") {
        process.stdout.write(JSON.stringify({ type: "future_event" }) + "\n");
      } else if (behavior === "eof") {
        setTimeout(() => process.exit(0), 20);
      } else if (behavior === "oversized-stdout") {
        setTimeout(() => process.stdout.write("x".repeat(16 * 1024 * 1024 + 1)), 20);
      }
    } else if (command.type === "get_available_models") {
      const models = behavior === "selector-delays"
        ? [
            { provider: "test", id: "model", name: "Test Model" },
            { provider: "test", id: "model-a", name: "Model A" },
            { provider: "test", id: "model-b", name: "Model B" }
          ]
        : [{ provider: "test", id: "model", name: "Test Model" }];
      respond(command, { models });
    } else if (command.type === "get_available_thinking_levels") {
      const levels = behavior === "selector-delays" && currentModel.id === "model-b"
        ? ["off", "high"]
        : ["off", "medium"];
      respond(command, { levels });
    } else if (command.type === "set_model" && behavior === "selector-delays") {
      const delay = command.modelId === "model-a" ? 180 : 15;
      setTimeout(() => {
        currentModel = { provider: command.provider, id: command.modelId,
          name: command.modelId === "model-a" ? "Model A" : "Model B" };
        currentThinkingLevel = command.modelId === "model-b" ? "high" : "medium";
        respond(command);
      }, delay);
    } else if (command.type === "set_thinking_level" && behavior === "selector-delays") {
      const delay = command.level === "off" ? 180 : 15;
      setTimeout(() => {
        currentThinkingLevel = command.level;
        respond(command);
      }, delay);
    } else if (command.type === "prompt" && behavior === "operation-delays") {
      setTimeout(() => respond(command), 80);
    } else if (command.type === "prompt" && behavior === "prompt-rejected") {
      process.stdout.write(JSON.stringify({ type: "response", id: command.id, command: command.type, success: false,
        error: "prompt rejected" }) + "\n");
    } else if (command.type === "prompt" && behavior === "abort-clear-timeout") {
      process.stdout.write(JSON.stringify({ type: "queue_update", steering: ["first steer", "second steer"],
        followUp: ["later follow-up"] }) + "\n");
      process.stdout.write(JSON.stringify({ type: "response", id: command.id, command: command.type, success: true }) + "\n");
    } else if (command.type === "clear_queue" && behavior === "abort-clear-timeout") {
      process.stdout.write(JSON.stringify({ type: "queue_update", steering: [], followUp: [] }) + "\n");
    } else if (command.type === "clear_queue") {
      process.stdout.write(JSON.stringify({ type: "response", id: command.id, command: command.type, success: true,
        data: { steering: ["steer"], followUp: ["follow"] } }) + "\n");
    } else if (command.type === "new_session") {
      if (behavior === "cancel-new-session") {
        pendingNewSession = command;
        process.stdout.write(JSON.stringify({ type: "extension_ui_request", id: "cancel-new", method: "confirm",
          title: "New session", message: "Allow session replacement?" }) + "\n");
      } else {
        process.stdout.write(JSON.stringify({ type: "response", id: command.id, command: command.type, success: true,
          data: { cancelled: false } }) + "\n");
      }
    } else if (command.type === "get_messages") {
      if (behavior === "candidate-load-failure") {
        process.stdout.write(JSON.stringify({ type: "response", id: command.id, command: command.type, success: false,
          error: "candidate messages failed" }) + "\n");
      } else {
        process.stdout.write(JSON.stringify({ type: "response", id: command.id, command: command.type, success: true,
          data: { messages: behavior === "persistent" ? [
            { role: "user", content: "restored question", timestamp: 1, attachments: [] },
            { role: "assistant", content: [{ type: "text", text: "restored answer" }], stopReason: "stop", timestamp: 2 }
          ] : [] } }) + "\n");
      }
    } else if (command.type === "get_session_stats") {
      process.stdout.write(JSON.stringify({ type: "response", id: command.id, command: command.type, success: true,
        data: { cost: 0.25, contextUsage: { percent: 12.5 } } }) + "\n");
    } else if (command.type === "extension_ui_response") {
      if (pendingNewSession) {
        process.stdout.write(JSON.stringify({ type: "response", id: pendingNewSession.id, command: pendingNewSession.type,
          success: true, data: { cancelled: true } }) + "\n");
        pendingNewSession = null;
      }
    } else if (behavior === "lifecycle-events") {
      process.stdout.write(JSON.stringify({ type: "response", id: command.id, command: command.type, success: true }) + "\n");
      process.stdout.write(JSON.stringify({ type: "agent_start" }) + "\n");
      process.stdout.write(JSON.stringify({ type: "agent_settled" }) + "\n");
    } else if (behavior === "crash") {
      process.stderr.write("fake child failure\n");
      process.exit(7);
    } else if (behavior === "oversized-stderr") {
      process.stderr.write("HEAD-MARKER" + "x".repeat(20000) + "TAIL-MARKER");
      process.exit(7);
    } else if (behavior !== "timeout") {
      process.stdout.write(JSON.stringify({
        type: "response", id: command.id, command: command.type, success: true
      }) + "\n");
    }
  }
});

process.stdin.on("end", () => {
  if (behavior === "stale") {
    process.stdout.write(JSON.stringify({ type: "stale_event" }) + "\n");
    setTimeout(() => process.exit(0), 20);
  } else {
    process.exit(0);
  }
});
