using System.Diagnostics;
using System.Text.Json;
using PiDesk.Services;

namespace PiDesk.Tests;

public sealed class PiSessionProtocolTests
{
    [Fact]
    public void ParsesEveryCurrentlyHandledEventType()
    {
        var cases = new (string Json, Type Expected)[]
        {
            ("""{"type":"agent_start"}""", typeof(AgentStartedEvent)),
            ("""{"type":"agent_settled"}""", typeof(AgentSettledEvent)),
            ("""{"type":"queue_update","steering":["s"],"followUp":["f"]}""", typeof(QueueUpdatedEvent)),
            ("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"hi"}}""", typeof(AssistantTextDeltaEvent)),
            ("""{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"done"}],"stopReason":"stop"}}""", typeof(AssistantMessageEndedEvent)),
            ("""{"type":"tool_execution_start","toolCallId":"1","toolName":"read","args":{"path":"a.txt"}}""", typeof(ToolStartedEvent)),
            ("""{"type":"tool_execution_end","toolCallId":"1","toolName":"read","result":{"content":[{"type":"text","text":"ok"}],"details":{}},"isError":false}""", typeof(ToolEndedEvent)),
            ("""{"type":"auto_retry_start","attempt":1,"maxAttempts":3,"delayMs":2000,"errorMessage":"busy"}""", typeof(RetryStartedEvent)),
            ("""{"type":"compaction_start","reason":"threshold"}""", typeof(CompactionStartedEvent)),
            ("""{"type":"extension_error","error":"failed"}""", typeof(ExtensionFailedEvent)),
            ("""{"type":"extension_ui_request","id":"ui-1","method":"select","title":"Pick","options":["a","b"]}""", typeof(ExtensionUiRequestedEvent)),
        };

        foreach (var (json, expected) in cases)
        {
            Assert.IsType(expected, PiProtocolParser.ParseEvent(Parse(json)));
        }
    }

    [Fact]
    public async Task AgentActivityFixtureParsesCompleteOrderedTypedState()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "agent-activity.jsonl");
        var events = (await File.ReadAllLinesAsync(fixturePath))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => PiProtocolParser.ParseEvent(Parse(line)))
            .ToArray();

        Assert.Equal(
            [
                typeof(AssistantThinkingStartedEvent), typeof(AssistantThinkingDeltaEvent), typeof(AssistantThinkingEndedEvent),
                typeof(AssistantToolCallStartedEvent), typeof(AssistantToolArgumentsDeltaEvent), typeof(AssistantToolCallEndedEvent),
                typeof(ToolStartedEvent), typeof(ToolUpdatedEvent), typeof(ToolEndedEvent), typeof(ToolEndedEvent),
                typeof(AssistantMessageEndedEvent), typeof(RetryStartedEvent), typeof(RetryEndedEvent),
                typeof(SummarizationRetryScheduledEvent), typeof(SummarizationRetryAttemptStartedEvent),
                typeof(SummarizationRetryFinishedEvent), typeof(CompactionStartedEvent),
                typeof(CompactionEndedEvent), typeof(CompactionEndedEvent),
            ],
            events.Select(item => item.GetType()));

        Assert.Equal("Inspecting ", Assert.IsType<AssistantThinkingDeltaEvent>(events[1]).Delta);
        Assert.Equal("Inspecting files", Assert.IsType<AssistantThinkingEndedEvent>(events[2]).Thinking);

        var streamedCall = Assert.IsType<AssistantToolCallEndedEvent>(events[5]);
        Assert.Equal(("call-edit", "edit"), (streamedCall.Id, streamedCall.Name));
        Assert.Contains("\"path\":\"a.txt\"", streamedCall.Arguments.Json);

        var started = Assert.IsType<ToolStartedEvent>(events[6]);
        Assert.Contains("\"edits\"", started.Arguments.Json);
        var update = Assert.IsType<ToolUpdatedEvent>(events[7]);
        Assert.Equal("Applying edit", update.PartialResult.Text);
        Assert.Equal("{\"phase\":\"write\"}", update.PartialResult.DetailsJson);

        var completed = Assert.IsType<ToolEndedEvent>(events[8]);
        Assert.False(completed.IsError);
        Assert.Equal("Updated a.txt", completed.Result.Text);
        Assert.Equal(" 1 old\n+1 new", completed.Result.Diff?.Diff);
        Assert.Equal("--- a.txt\n+++ a.txt", completed.Result.Diff?.Patch);
        Assert.Equal(1, completed.Result.Diff?.FirstChangedLine);
        var failed = Assert.IsType<ToolEndedEvent>(events[9]);
        Assert.True(failed.IsError);
        Assert.Equal("build failed", failed.Result.Text);

        var assistantError = Assert.IsType<AssistantMessageEndedEvent>(events[10]);
        Assert.Equal("provider unavailable", assistantError.ErrorMessage);
        var retry = Assert.IsType<RetryStartedEvent>(events[11]);
        Assert.Equal((1, 3, 2000, "overloaded"),
            (retry.Attempt, retry.MaxAttempts, retry.DelayMilliseconds, retry.ErrorMessage));
        Assert.Equal("overloaded after retries", Assert.IsType<RetryEndedEvent>(events[12]).FinalError);
        var summarizationRetry = Assert.IsType<SummarizationRetryScheduledEvent>(events[13]);
        Assert.Equal((1, 3, 1000),
            (summarizationRetry.Attempt, summarizationRetry.MaxAttempts, summarizationRetry.DelayMilliseconds));
        var summarizationAttempt = Assert.IsType<SummarizationRetryAttemptStartedEvent>(events[14]);
        Assert.Equal(("compaction", "threshold"), (summarizationAttempt.Source, summarizationAttempt.Reason));

        Assert.Equal("threshold", Assert.IsType<CompactionStartedEvent>(events[16]).Reason);
        var compacted = Assert.IsType<CompactionEndedEvent>(events[17]);
        Assert.Equal(("Retained work summary", "entry-1", 150000, 32000),
            (compacted.Result?.Summary, compacted.Result?.FirstKeptEntryId,
                compacted.Result?.TokensBefore, compacted.Result?.EstimatedTokensAfter));
        var compactionError = Assert.IsType<CompactionEndedEvent>(events[18]);
        Assert.Null(compactionError.Result);
        Assert.Equal("compaction quota exceeded", compactionError.ErrorMessage);
    }

    [Fact]
    public void NonObjectToolArgumentsFailAtTheTypedBoundary()
    {
        var exception = Assert.Throws<InvalidDataException>(() => PiProtocolParser.ParseEvent(Parse(
            """{"type":"tool_execution_start","toolCallId":"1","toolName":"read","args":"unchecked"}""")));

        Assert.Contains("required object 'args'", exception.Message);
    }

    [Fact]
    public void ParsesTypedResponsePayloads()
    {
        var state = PiProtocolParser.ParseState(Parse("""{"data":{"model":{"provider":"p","id":"m","name":"Model"},"thinkingLevel":"high","sessionId":"abcdef","sessionName":"Named","isStreaming":true}}"""));
        var models = PiProtocolParser.ParseModels(Parse("""{"data":{"models":[{"provider":"p","id":"m","name":"Model"}]}}"""));
        var levels = PiProtocolParser.ParseThinkingLevels(Parse("""{"data":{"levels":["off","high"]}}"""));
        var stats = PiProtocolParser.ParseStats(Parse("""{"data":{"cost":1.5,"contextUsage":{"percent":42}}}"""));
        var queue = PiProtocolParser.ParseClearedQueue(Parse("""{"data":{"steering":["s"],"followUp":["f"]}}"""));
        var messages = PiProtocolParser.ParseMessages(Parse("""{"data":{"messages":[{"role":"user","content":"question"},{"role":"assistant","content":[{"type":"thinking","thinking":"hidden"},{"type":"text","text":"answer"}]},{"role":"toolResult","toolCallId":"call-1","toolName":"read","content":[{"type":"text","text":"output"}],"isError":false},{"role":"custom","customType":"hidden","content":"not shown","display":false}]}}"""));

        Assert.Equal("abcdef", state.SessionId);
        Assert.True(state.IsStreaming);
        Assert.Equal("m", Assert.Single(models).Id);
        Assert.Equal(["off", "high"], levels);
        Assert.Equal(42, stats.ContextPercent);
        Assert.Equal("s", Assert.Single(queue.Steering));
        Assert.Collection(messages,
            item => Assert.Equal((PiConversationItemKind.User, "question"), (item.Kind, item.Text)),
            item => Assert.Equal((PiConversationItemKind.Thinking, "hidden"), (item.Kind, item.Text)),
            item => Assert.Equal((PiConversationItemKind.Assistant, "answer"), (item.Kind, item.Text)),
            item =>
            {
                Assert.Equal((PiConversationItemKind.Tool, "read completed"), (item.Kind, item.Text));
                Assert.Equal("output", item.ResultText);
            });
        Assert.True(PiProtocolParser.ParseCancelled(Parse("""{"data":{"cancelled":true}}""")));
    }

    [Fact]
    public void MissingRequiredFieldFailsWithCompatibilityError()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            PiProtocolParser.ParseState(Parse("""{"data":{"thinkingLevel":"off"}}""")));

        Assert.Contains("incompatible", exception.Message);
        Assert.Contains("sessionId", exception.Message);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

public sealed class PiSessionServiceTests
{
    [Fact]
    public async Task UnsupportedVersionFailsBeforeProcessLaunch()
    {
        var launched = false;
        var rpc = new PiRpcClient(_ =>
        {
            launched = true;
            throw new InvalidOperationException("must not launch");
        }, TimeSpan.FromSeconds(1));
        await using var session = new PiSessionService(rpc,
            () => new PiRuntimeInfo("node.exe", "fake.js", "0.84.4", PiBackend.Windows));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            session.StartAsync(Directory.GetCurrentDirectory()));

        Assert.False(launched);
        Assert.Equal(PiSessionLifecycleState.Faulted, session.LifecycleState);
        Assert.Contains("Windows backend", exception.Message);
        Assert.Contains(PiSessionService.MinimumSupportedPiVersion, exception.Message);
    }

    [Fact]
    public async Task NewerPatchVersionPassesAuditedVersionRange()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-newer-version-{Guid.NewGuid():N}.log");
        try
        {
            var rpc = CreateRpc("normal", logPath);
            await using var session = new PiSessionService(rpc,
                () => new PiRuntimeInfo("node.exe", "fake.js", "0.85.1", PiBackend.Windows));

            await session.StartAsync(Directory.GetCurrentDirectory());

            Assert.Equal(PiSessionLifecycleState.Connected, session.LifecycleState);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task AgentEventsDriveExplicitBusyAndConnectedLifecycle()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-lifecycle-{Guid.NewGuid():N}.log");
        try
        {
            var rpc = CreateRpc("lifecycle-events", logPath);
            await using var session = new PiSessionService(rpc,
                () => new PiRuntimeInfo("node.exe", "fake.js", PiSessionService.MinimumSupportedPiVersion));
            var states = new List<PiSessionLifecycleState>();
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.LifecycleChanged += states.Add;
            session.EventReceived += message =>
            {
                if (message is AgentSettledEvent)
                {
                    settled.TrySetResult();
                }
                return Task.CompletedTask;
            };

            await session.StartAsync(Directory.GetCurrentDirectory());
            await session.PromptAsync("hello", steer: false);
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains(PiSessionLifecycleState.Starting, states);
            Assert.Contains(PiSessionLifecycleState.Busy, states);
            Assert.Equal(PiSessionLifecycleState.Connected, session.LifecycleState);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task EmitsEveryCurrentlyUsedCommandAndReturnsTypedResults()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-session-{Guid.NewGuid():N}.log");
        try
        {
            var rpc = CreateRpc("normal", logPath);
            await using var session = new PiSessionService(rpc,
                () => new PiRuntimeInfo("node.exe", "fake.js", PiSessionService.MinimumSupportedPiVersion));

            await session.StartAsync(Directory.GetCurrentDirectory());
            var snapshot = await session.GetSnapshotAsync();
            var receipt = await session.PromptAsync("hello", steer: true);
            var queue = await session.ClearQueueAsync();
            await session.AbortAsync();
            Assert.False((await session.NewSessionAsync()).Cancelled);
            await session.SetModelAsync("test", "model");
            var levels = await session.GetThinkingLevelsAsync();
            await session.SetThinkingLevelAsync("medium");
            var stats = await session.GetStatsAsync();
            await session.SendExtensionResponseAsync(new ExtensionUiResponse("ui-1", Value: "yes"));
            await session.StopAsync();

            Assert.Equal("test-session", snapshot.State.SessionId);
            Assert.True(receipt.Accepted);
            Assert.Equal("steer", Assert.Single(queue.Steering));
            Assert.Contains("medium", levels);
            Assert.Equal(0.25, stats.Cost);
            Assert.Equal(PiSessionLifecycleState.Disconnected, session.LifecycleState);

            var commands = (await File.ReadAllLinesAsync(logPath))
                .Where(line => line.StartsWith("command ", StringComparison.Ordinal)).ToArray();
            var types = commands.Select(line => line.Split(' ', 3)[1]).ToArray();
            Assert.Contains("get_available_models", types);
            Assert.Contains("get_state", types);
            Assert.Contains("get_available_thinking_levels", types);
            Assert.Contains("prompt", types);
            Assert.Contains("clear_queue", types);
            Assert.Contains("abort", types);
            Assert.Contains("new_session", types);
            Assert.Contains("set_model", types);
            Assert.Contains("set_thinking_level", types);
            Assert.Contains("get_messages", types);
            Assert.Contains("get_session_stats", types);
            Assert.Contains("extension_ui_response", types);
            Assert.Contains(commands, line => line.Contains("\"streamingBehavior\":\"steer\"", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task RejectedPromptIsNotReportedAsAccepted()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-prompt-rejected-{Guid.NewGuid():N}.log");
        try
        {
            await using var session = new PiSessionService(CreateRpc("prompt-rejected", logPath), SupportedRuntime);
            await session.StartAsync(Directory.GetCurrentDirectory());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.PromptAsync("keep this text", steer: false));

            Assert.Contains("prompt rejected", exception.Message);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task AbortReturnsClearedQueueInDeliveryOrder()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-abort-queue-{Guid.NewGuid():N}.log");
        try
        {
            await using var session = new PiSessionService(CreateRpc("normal", logPath), SupportedRuntime);
            await session.StartAsync(Directory.GetCurrentDirectory());

            var result = await session.ClearQueueAndAbortAsync();

            Assert.True(result.Succeeded);
            Assert.Equal(["steer", "follow"], result.ClearedQueue.InDeliveryOrder);
            Assert.Equal("steer\n\nfollow\n\ndraft", PiComposerRecovery.Restore("draft", result.ClearedQueue.InDeliveryOrder));
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task ClearQueueTimeoutRetainsKnownQueueAndStillAborts()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-abort-timeout-{Guid.NewGuid():N}.log");
        try
        {
            await using var session = new PiSessionService(
                CreateRpc("abort-clear-timeout", logPath, TimeSpan.FromMilliseconds(150)), SupportedRuntime);
            await session.StartAsync(Directory.GetCurrentDirectory());
            await session.PromptAsync("queued", steer: true);

            var result = await session.ClearQueueAndAbortAsync();

            Assert.NotNull(result.ClearQueueError);
            Assert.Null(result.AbortError);
            Assert.Equal(["first steer", "second steer", "later follow-up"], result.ClearedQueue.InDeliveryOrder);
            Assert.Contains(await File.ReadAllLinesAsync(logPath), line => line.StartsWith("command abort ", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task PersistentSessionIsFullyLoadedBeforeStartupReturns()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-restore-{Guid.NewGuid():N}.log");
        try
        {
            await using var session = new PiSessionService(CreateRpc("persistent", logPath), SupportedRuntime);

            var snapshot = await session.StartAsync(Directory.GetCurrentDirectory());

            Assert.Collection(snapshot.Messages,
                item => Assert.Equal("restored question", item.Text),
                item => Assert.Equal("restored answer", item.Text));
            Assert.Equal(0.25, snapshot.Stats.Cost);
            Assert.Equal(PiSessionLifecycleState.Connected, session.LifecycleState);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task CancelledNewSessionDoesNotLoadOrAdvanceSession()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-cancel-{Guid.NewGuid():N}.log");
        try
        {
            await using var session = new PiSessionService(CreateRpc("cancel-new-session", logPath), SupportedRuntime);
            session.EventReceived += async message =>
            {
                if (message is ExtensionUiRequestedEvent request)
                {
                    await session.SendExtensionResponseAsync(new ExtensionUiResponse(request.Request.Id, Confirmed: false));
                }
            };
            var original = await session.StartAsync(Directory.GetCurrentDirectory());
            var generation = session.SessionGeneration;

            var replacement = await session.NewSessionAsync();

            Assert.True(replacement.Cancelled);
            Assert.Null(replacement.Snapshot);
            Assert.Equal(generation, session.SessionGeneration);
            Assert.Equal(original.State.SessionId, (await session.GetSnapshotAsync()).State.SessionId);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task FailedCandidateLoadPreservesConnectedSession()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-candidate-fail-{Guid.NewGuid():N}.log");
        try
        {
            var behaviors = new Queue<string>(["normal", "candidate-load-failure"]);
            await using var session = new PiSessionService(
                () => CreateRpc(behaviors.Dequeue(), logPath), SupportedRuntime);
            var original = await session.StartAsync(Directory.GetCurrentDirectory());
            var generation = session.SessionGeneration;

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.StartAsync(Directory.GetCurrentDirectory()));

            Assert.Contains("candidate messages failed", exception.Message);
            Assert.Equal(PiSessionLifecycleState.Connected, session.LifecycleState);
            Assert.Equal(generation, session.SessionGeneration);
            Assert.Equal(original.State.SessionId, (await session.GetSnapshotAsync()).State.SessionId);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task ReplacedProcessCannotPublishLateEventsOrErrors()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-stale-session-{Guid.NewGuid():N}.log");
        try
        {
            var behaviors = new Queue<string>(["stale", "normal"]);
            await using var session = new PiSessionService(
                () => CreateRpc(behaviors.Dequeue(), logPath), SupportedRuntime);
            var staleEvents = 0;
            var errors = new List<string>();
            session.EventReceived += message =>
            {
                if (message is UnknownSessionEvent { Type: "stale_event" })
                {
                    staleEvents++;
                }
                return Task.CompletedTask;
            };
            session.ErrorReceived += errors.Add;

            await session.StartAsync(Directory.GetCurrentDirectory());
            await session.StartAsync(Directory.GetCurrentDirectory());

            Assert.Equal(0, staleEvents);
            Assert.Empty(errors);
            Assert.Equal(PiSessionLifecycleState.Connected, session.LifecycleState);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task ReplacementStartsCandidateBeforeStoppingCurrentProcess()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-candidate-order-{Guid.NewGuid():N}.log");
        try
        {
            await using var session = new PiSessionService(() => CreateRpc("normal", logPath), SupportedRuntime);
            await session.StartAsync(Directory.GetCurrentDirectory());
            await session.StartAsync(Directory.GetCurrentDirectory());

            var records = await File.ReadAllLinesAsync(logPath);
            var starts = records.Select((line, index) => (line, index))
                .Where(record => record.line.StartsWith("start ", StringComparison.Ordinal)).ToArray();
            var firstPid = starts[0].line[6..];
            var firstExit = Array.FindIndex(records, line => line == $"exit {firstPid}");

            Assert.Equal(2, starts.Length);
            Assert.True(starts[1].index < firstExit, "The current process stopped before its candidate was ready.");
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task RapidModelSelectionsApplyOnlyTheLatestModelAndItsThinkingState()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-selector-model-{Guid.NewGuid():N}.log");
        try
        {
            await using var session = new PiSessionService(CreateRpc("selector-delays", logPath), SupportedRuntime);
            await session.StartAsync(Directory.GetCurrentDirectory());

            var first = session.SelectModelAsync("test", "model-a");
            await Task.Delay(25);
            var latest = session.SelectModelAsync("test", "model-b");

            Assert.Null(await first);
            var update = Assert.IsType<PiSelectorUpdate>(await latest);
            Assert.Equal("model-b", update.Model?.Id);
            Assert.Equal(["off", "high"], update.ThinkingLevels);
            Assert.Equal("high", update.ThinkingLevel);

            var state = (await session.GetSnapshotAsync()).State;
            Assert.Equal("model-b", state.Model?.Id);
            Assert.Equal("high", state.ThinkingLevel);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task ThinkingSelectionDoesNotSuppressTheCurrentModelsLevelRefresh()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-selector-cross-{Guid.NewGuid():N}.log");
        try
        {
            await using var session = new PiSessionService(CreateRpc("selector-delays", logPath), SupportedRuntime);
            await session.StartAsync(Directory.GetCurrentDirectory());

            var model = session.SelectModelAsync("test", "model-a");
            await Task.Delay(25);
            var thinking = session.SelectThinkingLevelAsync("high");

            var modelUpdate = Assert.IsType<PiSelectorUpdate>(await model);
            Assert.Equal("model-a", modelUpdate.Model?.Id);
            Assert.Equal(["off", "medium"], modelUpdate.ThinkingLevels);
            Assert.Equal("high", Assert.IsType<PiSelectorUpdate>(await thinking).ThinkingLevel);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task RapidThinkingSelectionsApplyOnlyTheLatestLevel()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-selector-thinking-{Guid.NewGuid():N}.log");
        try
        {
            await using var session = new PiSessionService(CreateRpc("selector-delays", logPath), SupportedRuntime);
            await session.StartAsync(Directory.GetCurrentDirectory());

            var first = session.SelectThinkingLevelAsync("off");
            await Task.Delay(25);
            var latest = session.SelectThinkingLevelAsync("high");

            Assert.Null(await first);
            var update = Assert.IsType<PiSelectorUpdate>(await latest);
            Assert.Equal("high", update.ThinkingLevel);
            Assert.Equal("high", (await session.GetSnapshotAsync()).State.ThinkingLevel);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task ProjectReplacementInvalidatesAnInFlightSelectorCompletion()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-selector-replace-{Guid.NewGuid():N}.log");
        try
        {
            var behaviors = new Queue<string>(["selector-delays", "normal"]);
            await using var session = new PiSessionService(
                () => CreateRpc(behaviors.Dequeue(), logPath), SupportedRuntime);
            var original = await session.StartAsync(Directory.GetCurrentDirectory());

            var selector = session.SelectModelAsync("test", "model-a");
            await Task.Delay(25);
            var replacement = session.StartAsync(Directory.GetCurrentDirectory());

            Assert.Null(await selector);
            var snapshot = await replacement;
            Assert.True(snapshot.SessionGeneration > original.SessionGeneration);
            Assert.Equal("model", snapshot.State.Model?.Id);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task RepeatedMutatingOperationsAreSerializedWithoutDeadlock()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-operation-policy-{Guid.NewGuid():N}.log");
        try
        {
            var behaviors = new Queue<string>(["operation-delays", "normal", "normal"]);
            await using var session = new PiSessionService(
                () => CreateRpc(behaviors.Dequeue(), logPath), SupportedRuntime);
            await session.StartAsync(Directory.GetCurrentDirectory());

            var operations = new Task[]
            {
                session.PromptAsync("one", steer: false),
                session.PromptAsync("two", steer: false),
                session.ClearQueueAndAbortAsync(),
                session.ClearQueueAndAbortAsync(),
                session.NewSessionAsync(),
                session.NewSessionAsync(),
                session.StartAsync(Directory.GetCurrentDirectory()),
                session.StartAsync(Directory.GetCurrentDirectory()),
            };

            await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(10));

            var records = await File.ReadAllLinesAsync(logPath);
            Assert.Equal(2, records.Count(line => line.StartsWith("command prompt ", StringComparison.Ordinal)));
            Assert.Equal(2, records.Count(line => line.StartsWith("command abort ", StringComparison.Ordinal)));
            Assert.Equal(2, records.Count(line => line.StartsWith("command new_session ", StringComparison.Ordinal)));
            Assert.Equal(3, records.Count(line => line.StartsWith("start ", StringComparison.Ordinal)));
            Assert.Equal(PiSessionLifecycleState.Connected, session.LifecycleState);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    private static PiRuntimeInfo SupportedRuntime() =>
        new("node.exe", "fake.js", PiSessionService.MinimumSupportedPiVersion);

    private static PiRpcClient CreateRpc(string behavior, string lifecyclePath, TimeSpan? requestTimeout = null)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-rpc.js");
        return new PiRpcClient(workingDirectory =>
        {
            var info = new ProcessStartInfo
            {
                FileName = "node.exe",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add(fixture);
            info.ArgumentList.Add(behavior);
            info.ArgumentList.Add(lifecyclePath);
            return info;
        }, requestTimeout ?? TimeSpan.FromSeconds(5));
    }
}
