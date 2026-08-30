using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PiDesk.Models;
using PiDesk.Services;

namespace PiDesk.ViewModels;

public partial class MainPageViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly PiRpcClient _rpc = new();
    private ChatMessage? _streamingMessage;
    private bool _syncingSelectors;

    public MainPageViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _rpc.EventReceived += HandleEventAsync;
        _rpc.ErrorReceived += error => _dispatcher.TryEnqueue(() => ShowError(error));
    }

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<ModelOption> Models { get; } = [];
    public ObservableCollection<string> ThinkingLevels { get; } = [];
    public Func<JsonElement, Task<JsonObject>>? ExtensionUiHandler { get; set; }

    public bool IsEmpty => Messages.Count == 0;
    public string SendButtonText => IsStreaming ? "Queue" : "Send";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string PromptText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorkingDirectory { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Starting Pi…";

    [ObservableProperty]
    public partial string SessionSummary { get; set; } = "New session";

    [ObservableProperty]
    public partial string UsageSummary { get; set; } = "No usage yet";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AbortCommand))]
    public partial bool IsStreaming { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ModelOption? SelectedModel { get; set; }

    [ObservableProperty]
    public partial string? SelectedThinkingLevel { get; set; }

    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(SendButtonText));
        StatusText = value ? "Pi is working…" : IsConnected ? "Ready" : "Pi is not connected";
    }

    partial void OnSelectedModelChanged(ModelOption? value)
    {
        if (!_syncingSelectors && value is not null && IsConnected)
        {
            _ = ChangeModelAsync(value);
        }
    }

    partial void OnSelectedThinkingLevelChanged(string? value)
    {
        if (!_syncingSelectors && value is not null && IsConnected)
        {
            _ = ChangeThinkingLevelAsync(value);
        }
    }

    public async Task StartAsync() => await RestartAsync(WorkingDirectory);

    public async Task RestartAsync(string workingDirectory)
    {
        IsConnected = false;
        IsStreaming = false;
        HasError = false;
        StatusText = "Starting Pi…";
        WorkingDirectory = workingDirectory;
        Messages.Clear();
        NotifyMessagesChanged();

        try
        {
            await _rpc.StartAsync(workingDirectory);
            IsConnected = true;
            StatusText = "Loading models…";
            await LoadStateAsync();
            StatusText = "Ready";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = PromptText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        PromptText = string.Empty;
        Messages.Add(new ChatMessage("You", text, "\uE77B"));
        NotifyMessagesChanged();

        var command = new JsonObject
        {
            ["type"] = "prompt",
            ["message"] = text,
        };
        if (IsStreaming)
        {
            command["streamingBehavior"] = "steer";
        }

        try
        {
            await _rpc.SendAsync(command);
            if (IsStreaming)
            {
                StatusText = "Message queued to steer the current run";
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private bool CanSend() => IsConnected && !string.IsNullOrWhiteSpace(PromptText);

    [RelayCommand(CanExecute = nameof(CanAbort))]
    private async Task AbortAsync()
    {
        try
        {
            await _rpc.SendAsync(new JsonObject { ["type"] = "clear_queue" });
            await _rpc.SendAsync(new JsonObject { ["type"] = "abort" });
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private bool CanAbort() => IsStreaming;

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        try
        {
            await _rpc.SendAsync(new JsonObject { ["type"] = "new_session" });
            Messages.Clear();
            NotifyMessagesChanged();
            SessionSummary = "New session";
            UsageSummary = "No usage yet";
            StatusText = "Ready";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task LoadStateAsync()
    {
        var modelsResponse = await _rpc.SendAsync(new JsonObject { ["type"] = "get_available_models" });
        var stateResponse = await _rpc.SendAsync(new JsonObject { ["type"] = "get_state" });
        var thinkingResponse = await _rpc.SendAsync(new JsonObject { ["type"] = "get_available_thinking_levels" });

        _syncingSelectors = true;
        try
        {
            Models.Clear();
            foreach (var model in modelsResponse.GetProperty("data").GetProperty("models").EnumerateArray())
            {
                Models.Add(new ModelOption(
                    model.GetProperty("provider").GetString() ?? string.Empty,
                    model.GetProperty("id").GetString() ?? string.Empty,
                    model.GetProperty("name").GetString() ?? model.GetProperty("id").GetString() ?? "Model"));
            }

            var state = stateResponse.GetProperty("data");
            if (state.TryGetProperty("model", out var currentModel) && currentModel.ValueKind == JsonValueKind.Object)
            {
                var provider = currentModel.GetProperty("provider").GetString();
                var id = currentModel.GetProperty("id").GetString();
                SelectedModel = Models.FirstOrDefault(model => model.Provider == provider && model.Id == id);
            }

            ThinkingLevels.Clear();
            foreach (var level in thinkingResponse.GetProperty("data").GetProperty("levels").EnumerateArray())
            {
                if (level.GetString() is { } value)
                {
                    ThinkingLevels.Add(value);
                }
            }
            SelectedThinkingLevel = state.GetProperty("thinkingLevel").GetString();
            SessionSummary = state.TryGetProperty("sessionName", out var name) && !string.IsNullOrWhiteSpace(name.GetString())
                ? name.GetString()!
                : $"Session {state.GetProperty("sessionId").GetString()?[..8]}";
        }
        finally
        {
            _syncingSelectors = false;
        }
    }

    private async Task ChangeModelAsync(ModelOption model)
    {
        try
        {
            StatusText = "Switching model…";
            await _rpc.SendAsync(new JsonObject
            {
                ["type"] = "set_model",
                ["provider"] = model.Provider,
                ["modelId"] = model.Id,
            });
            await RefreshThinkingLevelsAsync();
            StatusText = "Ready";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task ChangeThinkingLevelAsync(string level)
    {
        try
        {
            await _rpc.SendAsync(new JsonObject { ["type"] = "set_thinking_level", ["level"] = level });
            StatusText = $"Thinking: {level}";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task RefreshThinkingLevelsAsync()
    {
        var response = await _rpc.SendAsync(new JsonObject { ["type"] = "get_available_thinking_levels" });
        _syncingSelectors = true;
        try
        {
            ThinkingLevels.Clear();
            foreach (var item in response.GetProperty("data").GetProperty("levels").EnumerateArray())
            {
                if (item.GetString() is { } level)
                {
                    ThinkingLevels.Add(level);
                }
            }
            SelectedThinkingLevel = ThinkingLevels.FirstOrDefault();
        }
        finally
        {
            _syncingSelectors = false;
        }
    }

    private async Task HandleEventAsync(JsonElement message)
    {
        var type = message.GetProperty("type").GetString();
        if (type == "extension_ui_request" && ExtensionUiHandler is not null)
        {
            var method = message.GetProperty("method").GetString();
            if (method is "select" or "confirm" or "input" or "editor")
            {
                var response = await RunOnUiAsync(() => ExtensionUiHandler(message));
                await _rpc.SendNotificationAsync(response);
            }
            else
            {
                await RunOnUiAsync(() => HandleFireAndForgetUi(message));
            }
            return;
        }

        await RunOnUiAsync(() =>
        {
            switch (type)
            {
                case "agent_start":
                    IsStreaming = true;
                    HasError = false;
                    break;
                case "agent_settled":
                    IsStreaming = false;
                    _streamingMessage = null;
                    _ = RefreshStatsAsync();
                    break;
                case "message_update":
                    HandleMessageUpdate(message);
                    break;
                case "message_end":
                    HandleMessageEnd(message);
                    break;
                case "tool_execution_start":
                    HandleToolStart(message);
                    break;
                case "tool_execution_end":
                    HandleToolEnd(message);
                    break;
                case "auto_retry_start":
                    StatusText = $"Retrying in {message.GetProperty("delayMs").GetInt32() / 1000.0:0.#} seconds…";
                    break;
                case "compaction_start":
                    StatusText = "Compacting session context…";
                    break;
                case "extension_error":
                    ShowError(message.GetProperty("error").GetString() ?? "A Pi extension failed.");
                    break;
            }
            return Task.CompletedTask;
        });
    }

    private void HandleMessageUpdate(JsonElement message)
    {
        var update = message.GetProperty("assistantMessageEvent");
        if (update.GetProperty("type").GetString() != "text_delta")
        {
            return;
        }

        _streamingMessage ??= AddAssistantMessage();
        _streamingMessage.Text += update.GetProperty("delta").GetString();
    }

    private void HandleMessageEnd(JsonElement message)
    {
        var completed = message.GetProperty("message");
        if (!completed.TryGetProperty("role", out var role) || role.GetString() != "assistant")
        {
            return;
        }

        var text = string.Concat(completed.GetProperty("content").EnumerateArray()
            .Where(block => block.TryGetProperty("type", out var type) && type.GetString() == "text")
            .Select(block => block.TryGetProperty("text", out var value) ? value.GetString() : string.Empty));
        if (!string.IsNullOrEmpty(text))
        {
            _streamingMessage ??= AddAssistantMessage();
            _streamingMessage.Text = text;
        }

        if (completed.TryGetProperty("stopReason", out var stopReason) && stopReason.GetString() == "error")
        {
            ShowError(string.IsNullOrEmpty(text) ? "Pi could not complete the response." : text);
        }
    }

    private ChatMessage AddAssistantMessage()
    {
        var item = new ChatMessage("Pi", string.Empty, "\uE8BD");
        Messages.Add(item);
        NotifyMessagesChanged();
        return item;
    }

    private void HandleToolStart(JsonElement message)
    {
        var name = message.GetProperty("toolName").GetString() ?? "tool";
        var id = message.GetProperty("toolCallId").GetString();
        Messages.Add(new ChatMessage("Tool", $"Running {name}…", "\uE756", isActivity: true, correlationId: id));
        NotifyMessagesChanged();
    }

    private void HandleToolEnd(JsonElement message)
    {
        var id = message.GetProperty("toolCallId").GetString();
        var item = Messages.LastOrDefault(candidate => candidate.CorrelationId == id);
        if (item is not null)
        {
            var name = message.GetProperty("toolName").GetString() ?? "tool";
            item.Text = message.GetProperty("isError").GetBoolean() ? $"{name} failed" : $"{name} completed";
        }
    }

    private async Task RefreshStatsAsync()
    {
        try
        {
            var response = await _rpc.SendAsync(new JsonObject { ["type"] = "get_session_stats" });
            var data = response.GetProperty("data");
            var cost = data.GetProperty("cost").GetDouble();
            var summary = $"${cost:0.0000}";
            if (data.TryGetProperty("contextUsage", out var context) && context.ValueKind == JsonValueKind.Object &&
                context.TryGetProperty("percent", out var percentValue) && percentValue.ValueKind == JsonValueKind.Number)
            {
                summary += $" · {percentValue.GetDouble():0.#}% context";
            }

            await RunOnUiAsync(() =>
            {
                UsageSummary = summary;
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                ShowError(ex.Message);
                return Task.CompletedTask;
            });
        }
    }

    private Task HandleFireAndForgetUi(JsonElement message)
    {
        var method = message.GetProperty("method").GetString();
        if (method == "notify")
        {
            var text = message.TryGetProperty("message", out var value) ? value.GetString() : null;
            var severity = message.TryGetProperty("notifyType", out var type) ? type.GetString() : "info";
            if (severity == "error")
            {
                ShowError(text ?? "Pi reported an error.");
            }
            else
            {
                StatusText = text ?? "Pi notification";
            }
        }
        else if (method == "setStatus" && message.TryGetProperty("statusText", out var status))
        {
            StatusText = status.GetString() ?? "Ready";
        }
        return Task.CompletedTask;
    }

    private void ShowError(string message)
    {
        ErrorText = message;
        HasError = true;
        StatusText = "Action needed";
    }

    private void NotifyMessagesChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
    }

    private Task RunOnUiAsync(Func<Task> action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    private Task<T> RunOnUiAsync<T>(Func<Task<T>> action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    public async ValueTask DisposeAsync() => await _rpc.DisposeAsync();
}
