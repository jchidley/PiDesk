using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiDesk.Services;

public sealed class PiSessionService : IAsyncDisposable
{
    public const string SupportedPiVersion = "0.84.4";
    private readonly Func<PiRpcClient> _rpcFactory;
    private readonly Func<PiRuntimeInfo> _runtimeResolver;
    private readonly SemaphoreSlim _replacementLock = new(1, 1);
    private PiRpcClient? _rpc;
    private Func<JsonElement, Task>? _eventHandler;
    private Action<string>? _errorHandler;
    private readonly object _queueLock = new();
    private PiClearedQueue _knownQueue = new([], []);
    private long _sessionGeneration;

    public PiSessionService() : this(() => new PiRpcClient(), PiRuntimeResolver.Resolve)
    {
    }

    internal PiSessionService(PiRpcClient rpc, Func<PiRuntimeInfo> runtimeResolver)
        : this(() => rpc, runtimeResolver)
    {
    }

    internal PiSessionService(Func<PiRpcClient> rpcFactory, Func<PiRuntimeInfo> runtimeResolver)
    {
        _rpcFactory = rpcFactory;
        _runtimeResolver = runtimeResolver;
    }

    public event Func<PiSessionEvent, Task>? EventReceived;
    public event Action<string>? ErrorReceived;
    public event Action<PiSessionLifecycleState>? LifecycleChanged;
    public PiSessionLifecycleState LifecycleState { get; private set; } = PiSessionLifecycleState.Disconnected;
    public IReadOnlyList<RpcDiagnostic> Diagnostics => _rpc?.Diagnostics ?? [];
    internal long SessionGeneration => Volatile.Read(ref _sessionGeneration);

    public async Task<PiSessionSnapshot> StartAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        await _replacementLock.WaitAsync(cancellationToken);
        try
        {
            var previousLifecycle = LifecycleState;
            SetLifecycle(PiSessionLifecycleState.Starting);
            PiRpcClient? candidate = null;
            try
            {
                ValidateRuntime();
                candidate = _rpcFactory();
                await candidate.StartAsync(workingDirectory, cancellationToken);
                var snapshot = await LoadSnapshotAsync(candidate, cancellationToken);
                await CommitCandidateAsync(candidate, snapshot);
                return snapshot;
            }
            catch
            {
                if (candidate is not null)
                {
                    await candidate.DisposeAsync();
                }
                SetLifecycle(_rpc is null ? PiSessionLifecycleState.Faulted : previousLifecycle);
                throw;
            }
        }
        finally
        {
            _replacementLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _replacementLock.WaitAsync();
        try
        {
            SetLifecycle(PiSessionLifecycleState.Stopping);
            var rpc = DetachCurrentRpc();
            if (rpc is not null)
            {
                await rpc.DisposeAsync();
            }
            SetLifecycle(PiSessionLifecycleState.Disconnected);
        }
        finally
        {
            _replacementLock.Release();
        }
    }

    public async Task<PiSessionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        await LoadSnapshotAsync(GetRpc(), cancellationToken);

    public async Task<PiPromptReceipt> PromptAsync(string message, bool steer, CancellationToken cancellationToken = default)
    {
        var command = new JsonObject { ["type"] = "prompt", ["message"] = message };
        if (steer)
        {
            command["streamingBehavior"] = "steer";
        }
        await GetRpc().SendAsync(command, cancellationToken);
        return new PiPromptReceipt(true);
    }

    public async Task<PiClearedQueue> ClearQueueAsync(CancellationToken cancellationToken = default)
    {
        var queue = PiProtocolParser.ParseClearedQueue(await SendAsync("clear_queue", cancellationToken));
        SetKnownQueue(new PiClearedQueue([], []));
        return queue;
    }

    public async Task AbortAsync(CancellationToken cancellationToken = default) =>
        await SendAsync("abort", cancellationToken);

    public async Task<PiAbortResult> ClearQueueAndAbortAsync(CancellationToken cancellationToken = default)
    {
        var queue = GetKnownQueue();
        Exception? clearError = null;
        Exception? abortError = null;
        try
        {
            queue = await ClearQueueAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            clearError = ex;
        }

        try
        {
            await AbortAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            abortError = ex;
        }

        return new PiAbortResult(queue, clearError, abortError);
    }

    public async Task<PiSessionReplacement> NewSessionAsync(CancellationToken cancellationToken = default)
    {
        await _replacementLock.WaitAsync(cancellationToken);
        try
        {
            var rpc = GetRpc();
            var response = await rpc.SendAsync(new JsonObject { ["type"] = "new_session" }, cancellationToken);
            if (PiProtocolParser.ParseCancelled(response))
            {
                return new PiSessionReplacement(true, null);
            }

            var snapshot = await LoadSnapshotAsync(rpc, cancellationToken);
            SetKnownQueue(new PiClearedQueue([], []));
            Interlocked.Increment(ref _sessionGeneration);
            SetLifecycle(snapshot.State.IsStreaming ? PiSessionLifecycleState.Busy : PiSessionLifecycleState.Connected);
            return new PiSessionReplacement(false, snapshot);
        }
        finally
        {
            _replacementLock.Release();
        }
    }

    public async Task SetModelAsync(string provider, string modelId, CancellationToken cancellationToken = default) =>
        await GetRpc().SendAsync(new JsonObject
        {
            ["type"] = "set_model",
            ["provider"] = provider,
            ["modelId"] = modelId,
        }, cancellationToken);

    public async Task<IReadOnlyList<string>> GetThinkingLevelsAsync(CancellationToken cancellationToken = default) =>
        PiProtocolParser.ParseThinkingLevels(await SendAsync("get_available_thinking_levels", cancellationToken));

    public async Task SetThinkingLevelAsync(string level, CancellationToken cancellationToken = default) =>
        await GetRpc().SendAsync(new JsonObject { ["type"] = "set_thinking_level", ["level"] = level }, cancellationToken);

    public async Task<PiSessionStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        PiProtocolParser.ParseStats(await SendAsync("get_session_stats", cancellationToken));

    public async Task SendExtensionResponseAsync(ExtensionUiResponse response, CancellationToken cancellationToken = default)
    {
        var command = new JsonObject { ["type"] = "extension_ui_response", ["id"] = response.Id };
        if (response.Value is not null)
        {
            command["value"] = response.Value;
        }
        if (response.Confirmed is not null)
        {
            command["confirmed"] = response.Confirmed.Value;
        }
        if (response.Cancelled)
        {
            command["cancelled"] = true;
        }
        await GetRpc().SendNotificationAsync(command, cancellationToken);
    }

    private void ValidateRuntime()
    {
        var runtime = _runtimeResolver();
        if (!string.Equals(runtime.Version, SupportedPiVersion, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Pi {runtime.Version} is not supported. PiDesk currently requires Pi {SupportedPiVersion}.");
        }
    }

    private static async Task<PiSessionSnapshot> LoadSnapshotAsync(PiRpcClient rpc, CancellationToken cancellationToken)
    {
        var models = PiProtocolParser.ParseModels(await SendAsync(rpc, "get_available_models", cancellationToken));
        var state = PiProtocolParser.ParseState(await SendAsync(rpc, "get_state", cancellationToken));
        var levels = PiProtocolParser.ParseThinkingLevels(await SendAsync(rpc, "get_available_thinking_levels", cancellationToken));
        var messages = PiProtocolParser.ParseMessages(await SendAsync(rpc, "get_messages", cancellationToken));
        var stats = PiProtocolParser.ParseStats(await SendAsync(rpc, "get_session_stats", cancellationToken));
        return new PiSessionSnapshot(state, models, levels, messages, stats);
    }

    private async Task CommitCandidateAsync(PiRpcClient candidate, PiSessionSnapshot snapshot)
    {
        var previous = DetachCurrentRpc();
        AttachRpc(candidate);
        SetKnownQueue(new PiClearedQueue([], []));
        Interlocked.Increment(ref _sessionGeneration);
        SetLifecycle(snapshot.State.IsStreaming ? PiSessionLifecycleState.Busy : PiSessionLifecycleState.Connected);
        if (previous is not null)
        {
            try
            {
                await previous.DisposeAsync();
            }
            catch (Exception ex)
            {
                candidate.RecordDiagnostic(RpcDiagnosticKind.StderrFailure, candidate.CurrentGeneration,
                    $"Could not finish replaced Pi process shutdown: {ex.Message}");
            }
        }
    }

    private void AttachRpc(PiRpcClient rpc)
    {
        _rpc = rpc;
        _eventHandler = message => HandleRawEventAsync(rpc, message);
        _errorHandler = message => HandleTransportError(rpc, message);
        rpc.EventReceived += _eventHandler;
        rpc.ErrorReceived += _errorHandler;
    }

    private PiRpcClient? DetachCurrentRpc()
    {
        var rpc = _rpc;
        _rpc = null;
        if (rpc is not null)
        {
            if (_eventHandler is not null)
            {
                rpc.EventReceived -= _eventHandler;
            }
            if (_errorHandler is not null)
            {
                rpc.ErrorReceived -= _errorHandler;
            }
        }
        _eventHandler = null;
        _errorHandler = null;
        return rpc;
    }

    private PiRpcClient GetRpc() => _rpc ?? throw new InvalidOperationException("Pi is not connected.");

    private async Task<JsonElement> SendAsync(string type, CancellationToken cancellationToken) =>
        await SendAsync(GetRpc(), type, cancellationToken);

    private static async Task<JsonElement> SendAsync(PiRpcClient rpc, string type, CancellationToken cancellationToken) =>
        await rpc.SendAsync(new JsonObject { ["type"] = type }, cancellationToken);

    private async Task HandleRawEventAsync(PiRpcClient source, JsonElement message)
    {
        if (!ReferenceEquals(_rpc, source))
        {
            return;
        }

        var parsed = PiProtocolParser.ParseEvent(message);
        switch (parsed)
        {
            case AgentStartedEvent:
                SetLifecycle(PiSessionLifecycleState.Busy);
                break;
            case AgentSettledEvent:
                SetLifecycle(PiSessionLifecycleState.Connected);
                break;
            case QueueUpdatedEvent queue:
                SetKnownQueue(queue.Queue);
                break;
            case UnknownSessionEvent unknown:
                source.RecordDiagnostic(RpcDiagnosticKind.UnknownEvent, source.CurrentGeneration,
                    $"Unhandled typed Pi event '{unknown.Type}'.");
                break;
        }

        if (EventReceived is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Func<PiSessionEvent, Task>>())
            {
                await handler(parsed);
            }
        }
    }

    private PiClearedQueue GetKnownQueue()
    {
        lock (_queueLock)
        {
            return _knownQueue;
        }
    }

    private void SetKnownQueue(PiClearedQueue queue)
    {
        lock (_queueLock)
        {
            _knownQueue = queue;
        }
    }

    private void HandleTransportError(PiRpcClient source, string message)
    {
        if (!ReferenceEquals(_rpc, source))
        {
            return;
        }
        SetLifecycle(PiSessionLifecycleState.Faulted);
        ErrorReceived?.Invoke(message);
    }

    private void SetLifecycle(PiSessionLifecycleState state)
    {
        if (LifecycleState == state)
        {
            return;
        }
        LifecycleState = state;
        LifecycleChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _replacementLock.Dispose();
    }
}
