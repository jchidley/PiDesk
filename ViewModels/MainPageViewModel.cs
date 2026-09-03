using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PiDesk.Models;
using PiDesk.Services;

namespace PiDesk.ViewModels;

public partial class MainPageViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly PiSessionService _session = new();
    private ChatMessage? _streamingMessage;
    private bool _syncingSelectors;
    private long _visibleSessionGeneration;
    private long _modelSelectionVersion;
    private long _thinkingSelectionVersion;
    private ModelOption? _confirmedModel;
    private string? _confirmedThinkingLevel;
    private Task _statsRefreshTask = Task.CompletedTask;

    public MainPageViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _session.EventReceived += HandleEventAsync;
        _session.ErrorReceived += error => _dispatcher.TryEnqueue(() => ShowError(error));
    }

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<ModelOption> Models { get; } = [];
    public ObservableCollection<string> ThinkingLevels { get; } = [];
    public Func<ExtensionUiRequest, Task<ExtensionUiResponse>>? ExtensionUiHandler { get; set; }

    public bool IsEmpty => Messages.Count == 0;
    public string SendButtonText => IsStreaming ? "Queue" : "Send";
    public bool CanChangeSelectors => IsConnected && !IsSessionOperationInProgress;
    public bool CanChangeThinking => CanChangeSelectors && !IsModelSelectionInProgress;
    public bool CanChooseProject => !IsSessionOperationInProgress;

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
    public partial bool IsSessionOperationInProgress { get; set; }

    [ObservableProperty]
    public partial bool IsModelSelectionInProgress { get; set; }

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
        NotifyCommandStates();
        StatusText = value ? "Pi is working…" : IsConnected ? "Ready" : "Pi is not connected";
    }

    partial void OnIsConnectedChanged(bool value) => NotifyInteractionStateChanged();

    partial void OnIsSessionOperationInProgressChanged(bool value) => NotifyInteractionStateChanged();

    partial void OnIsModelSelectionInProgressChanged(bool value) => OnPropertyChanged(nameof(CanChangeThinking));

    public async Task StartAsync() => await RestartAsync(WorkingDirectory);

    public async Task RestartAsync(string workingDirectory)
    {
        if (!TryBeginSessionOperation())
        {
            return;
        }

        var hadUsableSession = IsConnected;
        HasError = false;
        StatusText = hadUsableSession ? "Preparing project…" : "Starting Pi…";

        try
        {
            var snapshot = await _session.StartAsync(workingDirectory);
            ApplySnapshot(snapshot);
            WorkingDirectory = workingDirectory;
            IsConnected = true;
            StatusText = IsStreaming ? "Pi is working…" : "Ready";
        }
        catch (Exception ex)
        {
            IsConnected = hadUsableSession;
            ShowError(ex.Message);
        }
        finally
        {
            IsSessionOperationInProgress = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (!TryBeginSessionOperation())
        {
            return;
        }

        var text = PromptText.Trim();
        if (text.Length == 0)
        {
            IsSessionOperationInProgress = false;
            return;
        }

        PromptText = string.Empty;
        var message = new ChatMessage(
            "You", text, "\uE77B", deliveryState: MessageDeliveryState.Pending);
        Messages.Add(message);
        NotifyMessagesChanged();

        try
        {
            var receipt = await _session.PromptAsync(text, steer: IsStreaming);
            message.DeliveryState = receipt.Accepted
                ? MessageDeliveryState.Accepted
                : MessageDeliveryState.Failed;
            if (!receipt.Accepted)
            {
                PromptText = PiComposerRecovery.Restore(PromptText, [text]);
            }
            if (IsStreaming)
            {
                StatusText = "Message queued to steer the current run";
            }
        }
        catch (Exception ex)
        {
            message.DeliveryState = MessageDeliveryState.Failed;
            PromptText = PiComposerRecovery.Restore(PromptText, [text]);
            ShowError(ex.Message);
        }
        finally
        {
            IsSessionOperationInProgress = false;
        }
    }

    private bool CanSend() => CanChangeSelectors && !string.IsNullOrWhiteSpace(PromptText);

    [RelayCommand(CanExecute = nameof(CanAbort))]
    private async Task AbortAsync()
    {
        if (!TryBeginSessionOperation())
        {
            return;
        }

        try
        {
            var result = await _session.ClearQueueAndAbortAsync();
            var restoredCount = result.ClearedQueue.InDeliveryOrder.Count();
            PromptText = PiComposerRecovery.Restore(PromptText, result.ClearedQueue.InDeliveryOrder);

            if (result.Succeeded)
            {
                var restored = $"Restored {restoredCount} queued message{(restoredCount == 1 ? string.Empty : "s")}";
                StatusText = IsStreaming
                    ? restoredCount == 0 ? "Stopping Pi…" : $"Stopping Pi… {restored}"
                    : restoredCount == 0 ? "Ready" : $"Ready · {restored}";
                return;
            }

            var errors = new[] { result.ClearQueueError, result.AbortError }
                .Where(error => error is not null)
                .Select(error => error!.Message);
            ShowError(string.Join(" ", errors));
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsSessionOperationInProgress = false;
        }
    }

    private bool CanAbort() => IsStreaming && CanChangeSelectors;

    [RelayCommand(CanExecute = nameof(CanStartNewSession))]
    private async Task NewSessionAsync()
    {
        if (!TryBeginSessionOperation())
        {
            return;
        }

        try
        {
            var replacement = await _session.NewSessionAsync();
            if (replacement.Cancelled)
            {
                StatusText = "Session change cancelled";
                return;
            }

            ApplySnapshot(replacement.Snapshot ?? throw new InvalidOperationException("Pi did not return the new session state."));
            HasError = false;
            StatusText = IsStreaming ? "Pi is working…" : "Ready";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsSessionOperationInProgress = false;
        }
    }

    private bool CanStartNewSession() => CanChangeSelectors;

    private void ApplySnapshot(PiSessionSnapshot snapshot)
    {
        _visibleSessionGeneration = snapshot.SessionGeneration;
        _syncingSelectors = true;
        try
        {
            Models.Clear();
            foreach (var model in snapshot.Models)
            {
                Models.Add(new ModelOption(model.Provider, model.Id, model.Name));
            }

            SelectedModel = snapshot.State.Model is { } current
                ? Models.FirstOrDefault(model => model.Provider == current.Provider && model.Id == current.Id)
                : null;
            ThinkingLevels.Clear();
            foreach (var level in snapshot.ThinkingLevels)
            {
                ThinkingLevels.Add(level);
            }
            SelectedThinkingLevel = snapshot.State.ThinkingLevel;
            _confirmedModel = SelectedModel;
            _confirmedThinkingLevel = SelectedThinkingLevel;
        }
        finally
        {
            _syncingSelectors = false;
        }

        Messages.Clear();
        foreach (var message in snapshot.Messages)
        {
            Messages.Add(ToChatMessage(message));
        }
        _streamingMessage = null;
        NotifyMessagesChanged();
        IsStreaming = snapshot.State.IsStreaming;
        SessionSummary = !string.IsNullOrWhiteSpace(snapshot.State.SessionName)
            ? snapshot.State.SessionName
            : $"Session {snapshot.State.SessionId[..Math.Min(8, snapshot.State.SessionId.Length)]}";
        UsageSummary = FormatUsage(snapshot.Stats);
    }

    private static ChatMessage ToChatMessage(PiConversationItem message) => message.Kind switch
    {
        PiConversationItemKind.User => new ChatMessage("You", message.Text, "\uE77B"),
        PiConversationItemKind.Assistant => new ChatMessage("Pi", message.Text, "\uE8BD"),
        PiConversationItemKind.Tool => new ChatMessage("Tool", message.Text, "\uE756", isActivity: true, correlationId: message.CorrelationId),
        _ => new ChatMessage("Activity", message.Text, "\uE7BA", isActivity: true),
    };

    private static string FormatUsage(PiSessionStats stats)
    {
        var summary = $"${stats.Cost:0.0000}";
        if (stats.ContextPercent is { } percent)
        {
            summary += $" · {percent:0.#}% context";
        }
        return summary;
    }

    public async Task ChangeModelAsync(ModelOption? model)
    {
        if (_syncingSelectors || model is null || !CanChangeSelectors)
        {
            return;
        }

        var selectionVersion = ++_modelSelectionVersion;
        IsModelSelectionInProgress = true;
        try
        {
            StatusText = "Switching model…";
            var update = await _session.SelectModelAsync(model.Provider, model.Id);
            if (update is null || update.SessionGeneration != _visibleSessionGeneration)
            {
                RestoreConfirmedModel(selectionVersion);
                return;
            }

            ApplySelectorUpdate(update, replaceThinkingLevels: true);
            StatusText = "Ready";
        }
        catch (Exception ex)
        {
            RestoreConfirmedModel(selectionVersion);
            ShowError(ex.Message);
        }
        finally
        {
            if (selectionVersion == _modelSelectionVersion)
            {
                IsModelSelectionInProgress = false;
            }
        }
    }

    public async Task ChangeThinkingLevelAsync(string? level)
    {
        if (_syncingSelectors || level is null || !CanChangeThinking)
        {
            return;
        }

        var selectionVersion = ++_thinkingSelectionVersion;
        try
        {
            var update = await _session.SelectThinkingLevelAsync(level);
            if (update is null || update.SessionGeneration != _visibleSessionGeneration)
            {
                RestoreConfirmedThinkingLevel(selectionVersion);
                return;
            }

            ApplySelectorUpdate(update, replaceThinkingLevels: false);
            StatusText = $"Thinking: {update.ThinkingLevel}";
        }
        catch (Exception ex)
        {
            RestoreConfirmedThinkingLevel(selectionVersion);
            ShowError(ex.Message);
        }
    }

    private void ApplySelectorUpdate(PiSelectorUpdate update, bool replaceThinkingLevels)
    {
        _syncingSelectors = true;
        try
        {
            if (update.Model is { } current)
            {
                SelectedModel = Models.FirstOrDefault(model =>
                    model.Provider == current.Provider && model.Id == current.Id);
            }

            if (replaceThinkingLevels)
            {
                ThinkingLevels.Clear();
                foreach (var level in update.ThinkingLevels)
                {
                    ThinkingLevels.Add(level);
                }
            }
            SelectedThinkingLevel = update.ThinkingLevel;
            _confirmedModel = SelectedModel;
            _confirmedThinkingLevel = SelectedThinkingLevel;
        }
        finally
        {
            _syncingSelectors = false;
        }
    }

    private void RestoreConfirmedModel(long selectionVersion)
    {
        if (selectionVersion != _modelSelectionVersion)
        {
            return;
        }

        _syncingSelectors = true;
        try
        {
            SelectedModel = _confirmedModel;
        }
        finally
        {
            _syncingSelectors = false;
        }
    }

    private void RestoreConfirmedThinkingLevel(long selectionVersion)
    {
        if (selectionVersion != _thinkingSelectionVersion)
        {
            return;
        }

        _syncingSelectors = true;
        try
        {
            SelectedThinkingLevel = _confirmedThinkingLevel;
        }
        finally
        {
            _syncingSelectors = false;
        }
    }

    private async Task HandleEventAsync(PiSessionEvent message)
    {
        if (message is ExtensionUiRequestedEvent extension)
        {
            if (extension.Request.Method is ExtensionUiMethod.Select or ExtensionUiMethod.Confirm or ExtensionUiMethod.Input or ExtensionUiMethod.Editor &&
                ExtensionUiHandler is not null)
            {
                var response = await RunOnUiAsync(() => ExtensionUiHandler(extension.Request));
                await _session.SendExtensionResponseAsync(response);
            }
            else
            {
                await RunOnUiAsync(() => HandleFireAndForgetUi(extension.Request));
            }
            return;
        }

        await RunOnUiAsync(() =>
        {
            switch (message)
            {
                case AgentStartedEvent:
                    IsStreaming = true;
                    HasError = false;
                    break;
                case AgentSettledEvent:
                    IsStreaming = false;
                    _streamingMessage = null;
                    _statsRefreshTask = RefreshStatsAfterAsync(_statsRefreshTask, _visibleSessionGeneration);
                    break;
                case AssistantTextDeltaEvent update:
                    _streamingMessage ??= AddAssistantMessage();
                    _streamingMessage.Text += update.Delta;
                    break;
                case AssistantMessageEndedEvent completed:
                    HandleMessageEnd(completed);
                    break;
                case ToolStartedEvent tool:
                    Messages.Add(new ChatMessage("Tool", $"Running {tool.Name}…", "\uE756", isActivity: true, correlationId: tool.Id));
                    NotifyMessagesChanged();
                    break;
                case ToolEndedEvent tool:
                    HandleToolEnd(tool);
                    break;
                case RetryStartedEvent retry:
                    StatusText = $"Retrying in {retry.DelayMilliseconds / 1000.0:0.#} seconds…";
                    break;
                case CompactionStartedEvent:
                    StatusText = "Compacting session context…";
                    break;
                case ExtensionFailedEvent failed:
                    ShowError(failed.Error);
                    break;
            }
            return Task.CompletedTask;
        });
    }

    private void HandleMessageEnd(AssistantMessageEndedEvent completed)
    {
        if (!string.IsNullOrEmpty(completed.Text))
        {
            _streamingMessage ??= AddAssistantMessage();
            _streamingMessage.Text = completed.Text;
        }

        if (completed.StopReason == "error")
        {
            ShowError(string.IsNullOrEmpty(completed.Text) ? "Pi could not complete the response." : completed.Text);
        }
    }

    private ChatMessage AddAssistantMessage()
    {
        var item = new ChatMessage("Pi", string.Empty, "\uE8BD");
        Messages.Add(item);
        NotifyMessagesChanged();
        return item;
    }

    private void HandleToolEnd(ToolEndedEvent tool)
    {
        var item = Messages.LastOrDefault(candidate => candidate.CorrelationId == tool.Id);
        if (item is not null)
        {
            item.Text = tool.IsError ? $"{tool.Name} failed" : $"{tool.Name} completed";
        }
    }

    private async Task RefreshStatsAfterAsync(Task previousRefresh, long generation)
    {
        try
        {
            await previousRefresh;
            var stats = await _session.GetStatsAsync();
            await RunOnUiAsync(() =>
            {
                if (generation == _visibleSessionGeneration)
                {
                    UsageSummary = FormatUsage(stats);
                }
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                if (generation == _visibleSessionGeneration)
                {
                    ShowError(ex.Message);
                }
                return Task.CompletedTask;
            });
        }
    }

    private Task HandleFireAndForgetUi(ExtensionUiRequest request)
    {
        if (request.Method == ExtensionUiMethod.Notify)
        {
            if (request.NotifyType == "error")
            {
                ShowError(request.Message ?? "Pi reported an error.");
            }
            else
            {
                StatusText = request.Message ?? "Pi notification";
            }
        }
        else if (request.Method == ExtensionUiMethod.SetStatus)
        {
            StatusText = request.StatusText ?? "Ready";
        }
        return Task.CompletedTask;
    }
    private void ShowError(string message)
    {
        ErrorText = message;
        HasError = true;
        StatusText = "Action needed";
    }

    private bool TryBeginSessionOperation()
    {
        if (IsSessionOperationInProgress)
        {
            return false;
        }
        IsSessionOperationInProgress = true;
        return true;
    }

    private void NotifyInteractionStateChanged()
    {
        OnPropertyChanged(nameof(CanChangeSelectors));
        OnPropertyChanged(nameof(CanChangeThinking));
        OnPropertyChanged(nameof(CanChooseProject));
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        SendCommand.NotifyCanExecuteChanged();
        AbortCommand.NotifyCanExecuteChanged();
        NewSessionCommand.NotifyCanExecuteChanged();
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
        if (!_dispatcher.TryEnqueue(async () =>
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
        }))
        {
            completion.SetException(new InvalidOperationException("The PiDesk window is no longer available."));
        }
        return completion.Task;
    }

    private Task<T> RunOnUiAsync<T>(Func<Task<T>> action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("The PiDesk window is no longer available."));
        }
        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        await _statsRefreshTask;
        await _session.DisposeAsync();
    }
}
