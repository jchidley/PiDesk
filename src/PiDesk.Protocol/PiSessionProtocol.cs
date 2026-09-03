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
    Tool,
    Activity,
}

public sealed record PiConversationItem(
    PiConversationItemKind Kind,
    string Text,
    string? CorrelationId = null,
    bool IsError = false);

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
public sealed record AssistantMessageEndedEvent(string Text, string? StopReason) : PiSessionEvent;
public sealed record ToolStartedEvent(string? Id, string Name) : PiSessionEvent;
public sealed record ToolEndedEvent(string? Id, string Name, bool IsError) : PiSessionEvent;
public sealed record RetryStartedEvent(int DelayMilliseconds) : PiSessionEvent;
public sealed record CompactionStartedEvent : PiSessionEvent;
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
                OptionalString(message, "toolCallId"), RequiredString(message, "toolName")),
            "tool_execution_end" => new ToolEndedEvent(
                OptionalString(message, "toolCallId"), RequiredString(message, "toolName"), RequiredBoolean(message, "isError")),
            "auto_retry_start" => new RetryStartedEvent(RequiredInt32(message, "delayMs")),
            "compaction_start" => new CompactionStartedEvent(),
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
            PiConversationItem? item = role switch
            {
                "user" => new(PiConversationItemKind.User, ParseContent(message)),
                "assistant" => new(PiConversationItemKind.Assistant, ParseContent(message)),
                "toolResult" => new(
                    PiConversationItemKind.Tool,
                    RequiredBoolean(message, "isError")
                        ? $"{RequiredString(message, "toolName")} failed"
                        : $"{RequiredString(message, "toolName")} completed",
                    OptionalString(message, "toolCallId"),
                    RequiredBoolean(message, "isError")),
                "bashExecution" => new(PiConversationItemKind.Tool, OptionalString(message, "output") ?? string.Empty),
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
        return OptionalString(update, "type") == "text_delta"
            ? new AssistantTextDeltaEvent(RequiredString(update, "delta"))
            : new UnknownSessionEvent($"message_update:{OptionalString(update, "type") ?? "unknown"}");
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
        return new AssistantMessageEndedEvent(text, OptionalString(completed, "stopReason"));
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

    private static double RequiredDouble(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.TryGetDouble(out var result)
            ? result : throw Invalid($"required number '{name}' is missing");

    private static InvalidDataException Invalid(string detail) => new($"Pi RPC record is incompatible: {detail}.");
}
