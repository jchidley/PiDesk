namespace PiDesk.Services;

public enum PiActivityKind
{
    UserText,
    AssistantText,
    Thinking,
    Tool,
    Retry,
    Compaction,
    Error,
}

public enum PiActivityState
{
    Pending,
    Streaming,
    Running,
    Completed,
    Failed,
}

public sealed record PiActivityItem(
    string Key,
    PiActivityKind Kind,
    string Title,
    string Text,
    PiActivityState State,
    string? ToolName = null,
    string? ArgumentsJson = null,
    PiDiff? Diff = null)
{
    public bool IsExpandable => Kind is PiActivityKind.Thinking or PiActivityKind.Tool || Diff is not null;
}

/// <summary>
/// Reduces only typed state received from Pi RPC. It does not infer tool outcomes or agent activity.
/// </summary>
public sealed class PiActivityReducer
{
    private readonly List<PiActivityItem> _items = [];
    private long _nextKey;
    private string? _assistantKey;
    private string? _thinkingKey;
    private string? _retryKey;
    private string? _summarizationRetryKey;
    private string? _compactionKey;
    private readonly Dictionary<int, string> _toolCallIds = [];

    public IReadOnlyList<PiActivityItem> Items => _items;

    public void Reset(IEnumerable<PiConversationItem> restoredMessages)
    {
        Reset(Array.Empty<PiActivityItem>());
        foreach (var message in restoredMessages)
        {
            var key = message.CorrelationId ?? $"restored-{++_nextKey}";
            switch (message.Kind)
            {
                case PiConversationItemKind.User:
                    _items.Add(new PiActivityItem(key, PiActivityKind.UserText, "You", message.Text, PiActivityState.Completed));
                    break;
                case PiConversationItemKind.Assistant:
                    _items.Add(new PiActivityItem(
                        key,
                        message.IsError ? PiActivityKind.Error : PiActivityKind.AssistantText,
                        message.IsError ? "Pi error" : "Pi",
                        message.Text,
                        message.IsError ? PiActivityState.Failed : PiActivityState.Completed));
                    break;
                case PiConversationItemKind.Thinking:
                    _items.Add(new PiActivityItem(key, PiActivityKind.Thinking, "Thinking", message.Text,
                        PiActivityState.Completed));
                    break;
                case PiConversationItemKind.ToolCall:
                    UpsertTool(key, message.ToolName ?? "Tool", message.ArgumentsJson, null, PiActivityState.Pending, null);
                    break;
                case PiConversationItemKind.Tool:
                    UpsertTool(key, message.ToolName ?? "Tool", message.ArgumentsJson,
                        message.ResultText ?? message.Text,
                        message.IsError ? PiActivityState.Failed : PiActivityState.Completed, message.Diff);
                    break;
                default:
                    _items.Add(new PiActivityItem(key, PiActivityKind.Compaction, "Activity", message.Text,
                        message.IsError ? PiActivityState.Failed : PiActivityState.Completed));
                    break;
            }
        }
    }

    public void Reset(IEnumerable<PiActivityItem> restoredItems)
    {
        _items.Clear();
        _items.AddRange(restoredItems);
        _assistantKey = null;
        _thinkingKey = null;
        _retryKey = null;
        _summarizationRetryKey = null;
        _compactionKey = null;
        _toolCallIds.Clear();
    }

    public void Apply(PiSessionEvent activity)
    {
        switch (activity)
        {
            case AgentStartedEvent:
                _assistantKey = null;
                _thinkingKey = null;
                _toolCallIds.Clear();
                break;
            case AssistantTextDeltaEvent text:
                ApplyAssistantDelta(text.Delta);
                break;
            case AssistantMessageEndedEvent message:
                ApplyAssistantEnd(message);
                break;
            case AssistantThinkingStartedEvent:
                _thinkingKey = Add(PiActivityKind.Thinking, "Thinking", string.Empty, PiActivityState.Streaming);
                break;
            case AssistantThinkingDeltaEvent thinking:
                Append(_thinkingKey, thinking.Delta);
                break;
            case AssistantThinkingEndedEvent thinking:
                Complete(_thinkingKey, thinking.Thinking);
                _thinkingKey = null;
                break;
            case AssistantToolCallStartedEvent tool:
                _toolCallIds[tool.ContentIndex] = tool.Id;
                AddToolIfMissing(tool.Id, tool.Name, string.Empty, PiActivityState.Streaming);
                break;
            case AssistantToolArgumentsDeltaEvent arguments:
                AppendArguments(arguments.ContentIndex, arguments.Delta);
                break;
            case AssistantToolCallEndedEvent tool:
                _toolCallIds[tool.ContentIndex] = tool.Id;
                UpsertTool(tool.Id, tool.Name, tool.Arguments.Json, null, PiActivityState.Pending, null);
                break;
            case ToolStartedEvent tool:
                UpsertTool(tool.Id, tool.Name, tool.Arguments.Json, null, PiActivityState.Running, null);
                break;
            case ToolUpdatedEvent tool:
                UpsertTool(tool.Id, tool.Name, tool.Arguments.Json, tool.PartialResult.Text, PiActivityState.Running,
                    tool.PartialResult.Diff);
                break;
            case ToolEndedEvent tool:
                UpsertTool(tool.Id, tool.Name, null, tool.Result.Text,
                    tool.IsError ? PiActivityState.Failed : PiActivityState.Completed, tool.Result.Diff);
                break;
            case RetryStartedEvent retry:
                _retryKey = Add(PiActivityKind.Retry, $"Retry {retry.Attempt} of {retry.MaxAttempts}",
                    retry.ErrorMessage, PiActivityState.Running);
                break;
            case RetryEndedEvent retry:
                FinishNotice(ref _retryKey, PiActivityKind.Retry, "Retry", retry.FinalError ?? string.Empty,
                    retry.Success ? PiActivityState.Completed : PiActivityState.Failed);
                break;
            case SummarizationRetryScheduledEvent retry:
                _summarizationRetryKey = Add(PiActivityKind.Retry,
                    $"Summary retry {retry.Attempt} of {retry.MaxAttempts}", retry.ErrorMessage, PiActivityState.Pending);
                break;
            case SummarizationRetryAttemptStartedEvent retry:
                UpdateNotice(_summarizationRetryKey, $"Retrying {retry.Source}", retry.Reason ?? string.Empty,
                    PiActivityState.Running);
                break;
            case SummarizationRetryFinishedEvent:
                FinishNotice(ref _summarizationRetryKey, PiActivityKind.Retry, "Summary retry", string.Empty,
                    PiActivityState.Completed);
                break;
            case CompactionStartedEvent compaction:
                _compactionKey = Add(PiActivityKind.Compaction, "Compacting context", compaction.Reason,
                    PiActivityState.Running);
                break;
            case CompactionEndedEvent compaction:
                FinishCompaction(compaction);
                break;
            case ExtensionFailedEvent error:
                Add(PiActivityKind.Error, "Extension error", error.Error, PiActivityState.Failed);
                break;
        }
    }

    private void ApplyAssistantDelta(string delta)
    {
        _assistantKey ??= Add(PiActivityKind.AssistantText, "Pi", string.Empty, PiActivityState.Streaming);
        Append(_assistantKey, delta);
    }

    private void ApplyAssistantEnd(AssistantMessageEndedEvent message)
    {
        if (_assistantKey is null && (message.Text.Length > 0 || message.ErrorMessage is not null))
        {
            _assistantKey = Add(PiActivityKind.AssistantText, "Pi", string.Empty, PiActivityState.Streaming);
        }
        if (_assistantKey is not null)
        {
            var state = message.StopReason == "error" ? PiActivityState.Failed : PiActivityState.Completed;
            Replace(_assistantKey, item => item with
            {
                Text = message.ErrorMessage ?? message.Text,
                State = state,
                Kind = state == PiActivityState.Failed ? PiActivityKind.Error : item.Kind,
            });
        }
        _assistantKey = null;
        _thinkingKey = null;
    }

    private void FinishCompaction(CompactionEndedEvent compaction)
    {
        var text = compaction.ErrorMessage
            ?? (compaction.Aborted ? "Compaction aborted" : compaction.Result?.Summary ?? string.Empty);
        var state = compaction.ErrorMessage is not null
            ? PiActivityState.Failed
            : compaction.Aborted ? PiActivityState.Failed : PiActivityState.Completed;
        if (_compactionKey is null)
        {
            _compactionKey = Add(PiActivityKind.Compaction, "Compaction", text, state);
        }
        else
        {
            UpdateNotice(_compactionKey, "Compaction", text, state);
        }
        _compactionKey = null;
    }

    private void AddToolIfMissing(string id, string name, string arguments, PiActivityState state)
    {
        if (Find(id) < 0)
        {
            _items.Add(new PiActivityItem(id, PiActivityKind.Tool, name, string.Empty, state, name, arguments));
        }
    }

    private void UpsertTool(
        string id,
        string name,
        string? arguments,
        string? output,
        PiActivityState state,
        PiDiff? diff)
    {
        AddToolIfMissing(id, name, arguments ?? string.Empty, state);
        Replace(id, item => item with
        {
            Title = name,
            ToolName = name,
            ArgumentsJson = arguments ?? item.ArgumentsJson,
            Text = output ?? item.Text,
            State = state,
            Diff = diff ?? item.Diff,
        });
    }

    private void AppendArguments(int contentIndex, string delta)
    {
        if (_toolCallIds.TryGetValue(contentIndex, out var id))
        {
            Replace(id, item => item with { ArgumentsJson = (item.ArgumentsJson ?? string.Empty) + delta });
        }
    }

    private void Append(string? key, string delta)
    {
        if (key is not null)
        {
            Replace(key, item => item with { Text = item.Text + delta });
        }
    }

    private void Complete(string? key, string text)
    {
        if (key is not null)
        {
            Replace(key, item => item with { Text = text, State = PiActivityState.Completed });
        }
    }

    private void FinishNotice(
        ref string? key,
        PiActivityKind kind,
        string title,
        string text,
        PiActivityState state)
    {
        if (key is null)
        {
            key = Add(kind, title, text, state);
        }
        else
        {
            UpdateNotice(key, title, text.Length == 0 ? null : text, state);
        }
        key = null;
    }

    private void UpdateNotice(string? key, string title, string? text, PiActivityState state)
    {
        if (key is not null)
        {
            Replace(key, item => item with { Title = title, Text = text ?? item.Text, State = state });
        }
    }

    private string Add(PiActivityKind kind, string title, string text, PiActivityState state)
    {
        var key = $"activity-{++_nextKey}";
        _items.Add(new PiActivityItem(key, kind, title, text, state));
        return key;
    }

    private void Replace(string key, Func<PiActivityItem, PiActivityItem> update)
    {
        var index = Find(key);
        if (index >= 0)
        {
            _items[index] = update(_items[index]);
        }
    }

    private int Find(string key) => _items.FindIndex(item => item.Key == key);
}
