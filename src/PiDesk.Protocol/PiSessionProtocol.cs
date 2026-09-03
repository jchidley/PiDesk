using System.Text.Json;

namespace PiDesk.Services;

public enum PiSessionLifecycleState
{
    Disconnected,
    Starting,
    Connected,
    Busy,
    Stopping,
    Faulted,
}

public sealed record PiModelInfo(string Provider, string Id, string Name);

public sealed record PiSessionState(
    PiModelInfo? Model,
    string ThinkingLevel,
    string SessionId,
    string? SessionName,
    bool IsStreaming);

public enum PiConversationItemKind
{
    User,
    Assistant,
    Thinking,
    ToolCall,
    Tool,
    Activity,
}

public sealed record PiConversationItem(
    PiConversationItemKind Kind,
    string Text,
    string? CorrelationId = null,
    bool IsError = false,
    string? ToolName = null,
    string? ResultText = null,
    string? ArgumentsJson = null,
    PiDiff? Diff = null);

public sealed record PiSessionStats(double Cost, double? ContextPercent);

public sealed record PiSessionSnapshot(
    PiSessionState State,
    IReadOnlyList<PiModelInfo> Models,
    IReadOnlyList<string> ThinkingLevels,
    IReadOnlyList<PiConversationItem> Messages,
    PiSessionStats Stats,
    long SessionGeneration = 0);

public sealed record PiSelectorUpdate(
    long SessionGeneration,
    PiModelInfo? Model,
    IReadOnlyList<string> ThinkingLevels,
    string ThinkingLevel);

public sealed record PiSessionReplacement(bool Cancelled, PiSessionSnapshot? Snapshot);
public sealed record PiPromptReceipt(bool Accepted);
public sealed record PiClearedQueue(IReadOnlyList<string> Steering, IReadOnlyList<string> FollowUp)
{
    public IEnumerable<string> InDeliveryOrder => Steering.Concat(FollowUp);
}

public sealed record PiAbortResult(
    PiClearedQueue ClearedQueue,
    Exception? ClearQueueError = null,
    Exception? AbortError = null)
{
    public bool Succeeded => ClearQueueError is null && AbortError is null;
}

public static class PiComposerRecovery
{
    public static string Restore(string currentText, IEnumerable<string> earlierText)
    {
        var parts = earlierText.Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        if (!string.IsNullOrWhiteSpace(currentText))
        {
            parts.Add(currentText);
        }
        return string.Join("\n\n", parts);
    }
}

public enum ExtensionUiMethod
{
    Select,
    Confirm,
    Input,
    Editor,
    Notify,
    SetStatus,
    SetWidget,
    SetTitle,
    SetEditorText,
    Unknown,
}

public sealed record ExtensionUiRequest(
    string Id,
    ExtensionUiMethod Method,
    string? Title,
    string? Message,
    IReadOnlyList<string> Options,
    string? Prefill,
    string? Placeholder,
    string? NotifyType,
    string? StatusText,
    string? Text);

public sealed record ExtensionUiResponse(
    string Id,
    string? Value = null,
    bool? Confirmed = null,
    bool Cancelled = false);

public abstract record PiSessionEvent;
public sealed record AgentStartedEvent : PiSessionEvent;
public sealed record AgentSettledEvent : PiSessionEvent;
public sealed record QueueUpdatedEvent(PiClearedQueue Queue) : PiSessionEvent;
public sealed record AssistantTextDeltaEvent(string Delta) : PiSessionEvent;
public sealed record AssistantThinkingStartedEvent(int ContentIndex) : PiSessionEvent;
public sealed record AssistantThinkingDeltaEvent(int ContentIndex, string Delta) : PiSessionEvent;
public sealed record AssistantThinkingEndedEvent(int ContentIndex, string Thinking) : PiSessionEvent;
public sealed record AssistantToolCallStartedEvent(int ContentIndex, string Id, string Name) : PiSessionEvent;
public sealed record AssistantToolArgumentsDeltaEvent(int ContentIndex, string Delta) : PiSessionEvent;
public sealed record PiToolArguments(string Json);
public sealed record AssistantToolCallEndedEvent(int ContentIndex, string Id, string Name, PiToolArguments Arguments) : PiSessionEvent;
public sealed record AssistantMessageEndedEvent(string Text, string? StopReason, string? ErrorMessage) : PiSessionEvent;
public sealed record PiDiff(string Diff, string? Patch, int? FirstChangedLine);
public sealed record PiToolResult(string Text, string? DetailsJson, PiDiff? Diff);
public sealed record ToolStartedEvent(string Id, string Name, PiToolArguments Arguments) : PiSessionEvent;
public sealed record ToolUpdatedEvent(string Id, string Name, PiToolArguments Arguments, PiToolResult PartialResult) : PiSessionEvent;
public sealed record ToolEndedEvent(string Id, string Name, PiToolResult Result, bool IsError) : PiSessionEvent;
public sealed record RetryStartedEvent(int Attempt, int MaxAttempts, int DelayMilliseconds, string ErrorMessage) : PiSessionEvent;
public sealed record RetryEndedEvent(bool Success, int Attempt, string? FinalError) : PiSessionEvent;
public sealed record SummarizationRetryScheduledEvent(
    int Attempt,
    int MaxAttempts,
    int DelayMilliseconds,
    string ErrorMessage) : PiSessionEvent;
public sealed record SummarizationRetryAttemptStartedEvent(string Source, string? Reason) : PiSessionEvent;
public sealed record SummarizationRetryFinishedEvent : PiSessionEvent;
public sealed record CompactionStartedEvent(string Reason) : PiSessionEvent;
public sealed record PiCompactionResult(
    string Summary,
    string FirstKeptEntryId,
    int TokensBefore,
    int? EstimatedTokensAfter);
public sealed record CompactionEndedEvent(
    string Reason,
    PiCompactionResult? Result,
    bool Aborted,
    bool WillRetry,
    string? ErrorMessage) : PiSessionEvent;
public sealed record ExtensionFailedEvent(string Error) : PiSessionEvent;
public sealed record ExtensionUiRequestedEvent(ExtensionUiRequest Request) : PiSessionEvent;
public sealed record UnknownSessionEvent(string Type) : PiSessionEvent;

internal static class PiProtocolParser
{
    public static PiSessionEvent ParseEvent(JsonElement message)
    {
        var type = RequiredString(message, "type");
        return type switch
        {
            "agent_start" => new AgentStartedEvent(),
            "agent_settled" => new AgentSettledEvent(),
            "queue_update" => new QueueUpdatedEvent(new PiClearedQueue(
                ParseStringArray(message, "steering"), ParseStringArray(message, "followUp"))),
            "message_update" => ParseMessageUpdate(message),
            "message_end" => ParseMessageEnd(message),
            "tool_execution_start" => new ToolStartedEvent(
                RequiredString(message, "toolCallId"), RequiredString(message, "toolName"), ParseToolArguments(message, "args")),
            "tool_execution_update" => new ToolUpdatedEvent(
                RequiredString(message, "toolCallId"), RequiredString(message, "toolName"), ParseToolArguments(message, "args"),
                ParseToolResult(RequiredObject(message, "partialResult"))),
            "tool_execution_end" => new ToolEndedEvent(
                RequiredString(message, "toolCallId"), RequiredString(message, "toolName"),
                ParseToolResult(RequiredObject(message, "result")), RequiredBoolean(message, "isError")),
            "auto_retry_start" => new RetryStartedEvent(
                RequiredInt32(message, "attempt"), RequiredInt32(message, "maxAttempts"),
                RequiredInt32(message, "delayMs"), RequiredString(message, "errorMessage")),
            "auto_retry_end" => new RetryEndedEvent(
                RequiredBoolean(message, "success"), RequiredInt32(message, "attempt"), OptionalString(message, "finalError")),
            "summarization_retry_scheduled" => new SummarizationRetryScheduledEvent(
                RequiredInt32(message, "attempt"), RequiredInt32(message, "maxAttempts"),
                RequiredInt32(message, "delayMs"), RequiredString(message, "errorMessage")),
            "summarization_retry_attempt_start" => new SummarizationRetryAttemptStartedEvent(
                RequiredString(message, "source"), OptionalString(message, "reason")),
            "summarization_retry_finished" => new SummarizationRetryFinishedEvent(),
            "compaction_start" => new CompactionStartedEvent(RequiredString(message, "reason")),
            "compaction_end" => ParseCompactionEnd(message),
            "extension_error" => new ExtensionFailedEvent(RequiredString(message, "error")),
            "extension_ui_request" => new ExtensionUiRequestedEvent(ParseExtensionRequest(message)),
            _ => new UnknownSessionEvent(type),
        };
    }

    public static PiSessionState ParseState(JsonElement response)
    {
        var data = RequiredObject(response, "data");
        PiModelInfo? model = null;
        if (data.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.Object)
        {
            model = ParseModel(modelValue);
        }

        return new PiSessionState(
            model,
            RequiredString(data, "thinkingLevel"),
            RequiredString(data, "sessionId"),
            OptionalString(data, "sessionName"),
            data.TryGetProperty("isStreaming", out var streaming) && streaming.ValueKind == JsonValueKind.True);
    }

    public static IReadOnlyList<PiModelInfo> ParseModels(JsonElement response) =>
        RequiredArray(RequiredObject(response, "data"), "models").EnumerateArray().Select(ParseModel).ToArray();

    public static IReadOnlyList<string> ParseThinkingLevels(JsonElement response) =>
        RequiredArray(RequiredObject(response, "data"), "levels").EnumerateArray()
            .Select(item => item.GetString() ?? throw Invalid("thinking level must be a string"))
            .ToArray();

    public static PiSessionStats ParseStats(JsonElement response)
    {
        var data = RequiredObject(response, "data");
        var cost = RequiredDouble(data, "cost");
        double? percent = null;
        if (data.TryGetProperty("contextUsage", out var context) && context.ValueKind == JsonValueKind.Object &&
            context.TryGetProperty("percent", out var value) && value.ValueKind == JsonValueKind.Number)
        {
            percent = value.GetDouble();
        }
        return new PiSessionStats(cost, percent);
    }

    public static IReadOnlyList<PiConversationItem> ParseMessages(JsonElement response)
    {
        var messages = RequiredArray(RequiredObject(response, "data"), "messages");
        var result = new List<PiConversationItem>();
        foreach (var message in messages.EnumerateArray())
        {
            var role = RequiredString(message, "role");
            if (role == "assistant")
            {
                ParseAssistantContent(message, result);
                continue;
            }

            PiConversationItem? item = role switch
            {
                "user" => new(PiConversationItemKind.User, ParseContent(message)),
                "toolResult" => ParseRestoredToolResult(message),
                "bashExecution" => new(
                    PiConversationItemKind.Tool,
                    OptionalString(message, "output") ?? string.Empty,
                    ToolName: "bash",
                    ResultText: OptionalString(message, "output")),
                "custom" when message.TryGetProperty("display", out var display) && display.ValueKind == JsonValueKind.True =>
                    new(PiConversationItemKind.Activity, ParseContent(message)),
                "branchSummary" => new(PiConversationItemKind.Activity, RequiredString(message, "summary")),
                "compactionSummary" => new(PiConversationItemKind.Activity, RequiredString(message, "summary")),
                _ => null,
            };
            if (item is not null && !string.IsNullOrEmpty(item.Text))
            {
                result.Add(item);
            }
        }
        return result;
    }

    private static void ParseAssistantContent(JsonElement message, List<PiConversationItem> result)
    {
        var content = RequiredArray(message, "content");
        var initialCount = result.Count;
        var isError = OptionalString(message, "stopReason") == "error";
        foreach (var block in content.EnumerateArray())
        {
            PiConversationItem? item = OptionalString(block, "type") switch
            {
                "text" => new(PiConversationItemKind.Assistant, RequiredString(block, "text"), IsError: isError),
                "thinking" => new(PiConversationItemKind.Thinking, RequiredString(block, "thinking")),
                "toolCall" => new(
                    PiConversationItemKind.ToolCall,
                    RequiredString(block, "name"),
                    RequiredString(block, "id"),
                    ToolName: RequiredString(block, "name"),
                    ArgumentsJson: ParseToolArguments(block, "arguments").Json),
                _ => null,
            };
            if (item is not null && !string.IsNullOrEmpty(item.Text))
            {
                result.Add(item);
            }
        }
        if (isError && !result.Skip(initialCount).Any(item => item.Kind == PiConversationItemKind.Assistant && item.IsError) &&
            OptionalString(message, "errorMessage") is { } errorMessage)
        {
            result.Add(new PiConversationItem(PiConversationItemKind.Assistant, errorMessage, IsError: true));
        }
    }

    private static PiConversationItem ParseRestoredToolResult(JsonElement message)
    {
        var name = RequiredString(message, "toolName");
        var isError = RequiredBoolean(message, "isError");
        PiDiff? diff = null;
        if (message.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object &&
            OptionalString(details, "diff") is { } diffText)
        {
            diff = new PiDiff(diffText, OptionalString(details, "patch"), OptionalInt32(details, "firstChangedLine"));
        }
        return new PiConversationItem(
            PiConversationItemKind.Tool,
            isError ? $"{name} failed" : $"{name} completed",
            RequiredString(message, "toolCallId"),
            isError,
            name,
            ParseContent(message),
            Diff: diff);
    }

    public static bool ParseCancelled(JsonElement response) =>
        RequiredBoolean(RequiredObject(response, "data"), "cancelled");

    public static PiClearedQueue ParseClearedQueue(JsonElement response)
    {
        var data = RequiredObject(response, "data");
        return new PiClearedQueue(ParseStringArray(data, "steering"), ParseStringArray(data, "followUp"));
    }

    private static PiSessionEvent ParseMessageUpdate(JsonElement message)
    {
        var update = RequiredObject(message, "assistantMessageEvent");
        var type = RequiredString(update, "type");
        return type switch
        {
            "text_delta" => new AssistantTextDeltaEvent(RequiredString(update, "delta")),
            "thinking_start" => new AssistantThinkingStartedEvent(RequiredInt32(update, "contentIndex")),
            "thinking_delta" => new AssistantThinkingDeltaEvent(
                RequiredInt32(update, "contentIndex"), RequiredString(update, "delta")),
            "thinking_end" => new AssistantThinkingEndedEvent(
                RequiredInt32(update, "contentIndex"), RequiredString(update, "content")),
            "toolcall_start" => new AssistantToolCallStartedEvent(
                RequiredInt32(update, "contentIndex"), RequiredString(update, "id"), RequiredString(update, "toolName")),
            "toolcall_delta" => new AssistantToolArgumentsDeltaEvent(
                RequiredInt32(update, "contentIndex"), RequiredString(update, "delta")),
            "toolcall_end" => ParseToolCallEnd(update),
            _ => new UnknownSessionEvent($"message_update:{type}"),
        };
    }

    private static PiSessionEvent ParseMessageEnd(JsonElement message)
    {
        var completed = RequiredObject(message, "message");
        if (OptionalString(completed, "role") != "assistant")
        {
            return new UnknownSessionEvent("message_end:non-assistant");
        }

        var text = string.Empty;
        if (completed.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            text = string.Concat(content.EnumerateArray()
                .Where(block => OptionalString(block, "type") == "text")
                .Select(block => OptionalString(block, "text") ?? string.Empty));
        }
        return new AssistantMessageEndedEvent(
            text, OptionalString(completed, "stopReason"), OptionalString(completed, "errorMessage"));
    }

    private static AssistantToolCallEndedEvent ParseToolCallEnd(JsonElement update)
    {
        var toolCall = RequiredObject(update, "toolCall");
        return new AssistantToolCallEndedEvent(
            RequiredInt32(update, "contentIndex"),
            RequiredString(toolCall, "id"),
            RequiredString(toolCall, "name"),
            ParseToolArguments(toolCall, "arguments"));
    }

    private static PiToolResult ParseToolResult(JsonElement result)
    {
        var text = string.Concat(RequiredArray(result, "content").EnumerateArray()
            .Where(item => OptionalString(item, "type") == "text")
            .Select(item => RequiredString(item, "text")));
        string? detailsJson = null;
        PiDiff? diff = null;
        if (result.TryGetProperty("details", out var details) && details.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            detailsJson = details.GetRawText();
            if (details.ValueKind == JsonValueKind.Object && OptionalString(details, "diff") is { } diffText)
            {
                diff = new PiDiff(
                    diffText,
                    OptionalString(details, "patch"),
                    OptionalInt32(details, "firstChangedLine"));
            }
        }
        return new PiToolResult(text, detailsJson, diff);
    }

    private static CompactionEndedEvent ParseCompactionEnd(JsonElement message)
    {
        PiCompactionResult? result = null;
        if (message.TryGetProperty("result", out var resultValue) && resultValue.ValueKind == JsonValueKind.Object)
        {
            result = new PiCompactionResult(
                RequiredString(resultValue, "summary"),
                RequiredString(resultValue, "firstKeptEntryId"),
                RequiredInt32(resultValue, "tokensBefore"),
                OptionalInt32(resultValue, "estimatedTokensAfter"));
        }
        return new CompactionEndedEvent(
            RequiredString(message, "reason"),
            result,
            RequiredBoolean(message, "aborted"),
            RequiredBoolean(message, "willRetry"),
            OptionalString(message, "errorMessage"));
    }

    private static string ParseContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("message content must be a string or array");
        }
        return string.Concat(content.EnumerateArray()
            .Where(block => OptionalString(block, "type") == "text")
            .Select(block => OptionalString(block, "text") ?? string.Empty));
    }

    private static ExtensionUiRequest ParseExtensionRequest(JsonElement message)
    {
        var methodText = RequiredString(message, "method");
        var method = methodText switch
        {
            "select" => ExtensionUiMethod.Select,
            "confirm" => ExtensionUiMethod.Confirm,
            "input" => ExtensionUiMethod.Input,
            "editor" => ExtensionUiMethod.Editor,
            "notify" => ExtensionUiMethod.Notify,
            "setStatus" => ExtensionUiMethod.SetStatus,
            "setWidget" => ExtensionUiMethod.SetWidget,
            "setTitle" => ExtensionUiMethod.SetTitle,
            "set_editor_text" => ExtensionUiMethod.SetEditorText,
            _ => ExtensionUiMethod.Unknown,
        };
        var options = message.TryGetProperty("options", out var optionValue) && optionValue.ValueKind == JsonValueKind.Array
            ? optionValue.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];
        return new ExtensionUiRequest(
            RequiredString(message, "id"), method, OptionalString(message, "title"), OptionalString(message, "message"),
            options, OptionalString(message, "prefill"), OptionalString(message, "placeholder"),
            OptionalString(message, "notifyType"), OptionalString(message, "statusText"), OptionalString(message, "text"));
    }

    private static PiModelInfo ParseModel(JsonElement model) => new(
        RequiredString(model, "provider"), RequiredString(model, "id"), OptionalString(model, "name") ?? RequiredString(model, "id"));

    private static IReadOnlyList<string> ParseStringArray(JsonElement owner, string name) =>
        RequiredArray(owner, name).EnumerateArray().Select(item => item.GetString() ?? throw Invalid($"{name} item must be a string")).ToArray();

    private static JsonElement RequiredObject(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : throw Invalid($"required object '{name}' is missing");

    private static JsonElement RequiredArray(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value : throw Invalid($"required array '{name}' is missing");

    private static string RequiredString(JsonElement owner, string name) =>
        OptionalString(owner, name) ?? throw Invalid($"required string '{name}' is missing");

    private static string? OptionalString(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool RequiredBoolean(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw Invalid($"required boolean '{name}' is missing");

    private static int RequiredInt32(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result : throw Invalid($"required integer '{name}' is missing");

    private static int? OptionalInt32(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static PiToolArguments ParseToolArguments(JsonElement owner, string name) =>
        new(RequiredObject(owner, name).GetRawText());

    private static double RequiredDouble(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.TryGetDouble(out var result)
            ? result : throw Invalid($"required number '{name}' is missing");

    private static InvalidDataException Invalid(string detail) => new($"Pi RPC record is incompatible: {detail}.");
}
