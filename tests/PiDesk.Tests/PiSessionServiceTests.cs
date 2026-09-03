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
            ("""{"type":"tool_execution_start","toolCallId":"1","toolName":"read"}""", typeof(ToolStartedEvent)),
            ("""{"type":"tool_execution_end","toolCallId":"1","toolName":"read","isError":false}""", typeof(ToolEndedEvent)),
            ("""{"type":"auto_retry_start","delayMs":2000}""", typeof(RetryStartedEvent)),
            ("""{"type":"compaction_start"}""", typeof(CompactionStartedEvent)),
            ("""{"type":"extension_error","error":"failed"}""", typeof(ExtensionFailedEvent)),
            ("""{"type":"extension_ui_request","id":"ui-1","method":"select","title":"Pick","options":["a","b"]}""", typeof(ExtensionUiRequestedEvent)),
        };

        foreach (var (json, expected) in cases)
        {
            Assert.IsType(expected, PiProtocolParser.ParseEvent(Parse(json)));
        }
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
            item => Assert.Equal((PiConversationItemKind.Assistant, "answer"), (item.Kind, item.Text)),
            item => Assert.Equal((PiConversationItemKind.Tool, "read completed"), (item.Kind, item.Text)));
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
            () => new PiRuntimeInfo("node.exe", "fake.js", "99.0.0"));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            session.StartAsync(Directory.GetCurrentDirectory()));

        Assert.False(launched);
        Assert.Equal(PiSessionLifecycleState.Faulted, session.LifecycleState);
        Assert.Contains(PiSessionService.SupportedPiVersion, exception.Message);
    }

    [Fact]
    public async Task AgentEventsDriveExplicitBusyAndConnectedLifecycle()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"pidesk-lifecycle-{Guid.NewGuid():N}.log");
        try
        {
            var rpc = CreateRpc("lifecycle-events", logPath);
            await using var session = new PiSessionService(rpc,
                () => new PiRuntimeInfo("node.exe", "fake.js", PiSessionService.SupportedPiVersion));
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
                () => new PiRuntimeInfo("node.exe", "fake.js", PiSessionService.SupportedPiVersion));

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

    private static PiRuntimeInfo SupportedRuntime() =>
        new("node.exe", "fake.js", PiSessionService.SupportedPiVersion);

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
