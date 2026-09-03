using System.Text.Json;
using PiDesk.Services;

namespace PiDesk.Tests;

public sealed class PiActivityReducerTests
{
    [Fact]
    public async Task RpcFixtureReducesToCorrelatedOrderedActivity()
    {
        var reducer = new PiActivityReducer();
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "agent-activity.jsonl");

        foreach (var line in await File.ReadAllLinesAsync(fixturePath))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                reducer.Apply(PiProtocolParser.ParseEvent(Parse(line)));
            }
        }

        Assert.Equal(
            [
                PiActivityKind.Thinking,
                PiActivityKind.Tool,
                PiActivityKind.Tool,
                PiActivityKind.Error,
                PiActivityKind.Retry,
                PiActivityKind.Retry,
                PiActivityKind.Compaction,
                PiActivityKind.Compaction,
            ],
            reducer.Items.Select(item => item.Kind));

        Assert.Equal("Inspecting files", reducer.Items[0].Text);
        var edit = reducer.Items[1];
        Assert.Equal(("call-edit", "edit", PiActivityState.Completed), (edit.Key, edit.ToolName, edit.State));
        Assert.Contains("\"path\":\"a.txt\"", edit.ArgumentsJson);
        Assert.Equal("Updated a.txt", edit.Text);
        Assert.Equal(" 1 old\n+1 new", edit.Diff?.Diff);
        Assert.True(edit.IsExpandable);

        Assert.Equal(PiActivityState.Failed, reducer.Items[2].State);
        Assert.Equal("build failed", reducer.Items[2].Text);
        Assert.Equal("provider unavailable", reducer.Items[3].Text);
        Assert.Equal(PiActivityState.Failed, reducer.Items[4].State);
        Assert.Equal(PiActivityState.Completed, reducer.Items[5].State);
        Assert.Equal("Retained work summary", reducer.Items[6].Text);
        Assert.Equal("compaction quota exceeded", reducer.Items[7].Text);
    }

    [Fact]
    public void ToolArgumentDeltasUseRpcContentIndexRatherThanArrivalOrder()
    {
        var reducer = new PiActivityReducer();

        reducer.Apply(new AssistantToolCallStartedEvent(1, "call-a", "read"));
        reducer.Apply(new AssistantToolCallStartedEvent(2, "call-b", "bash"));
        reducer.Apply(new AssistantToolArgumentsDeltaEvent(1, "{\"path\":"));
        reducer.Apply(new AssistantToolArgumentsDeltaEvent(2, "{\"command\":"));
        reducer.Apply(new AssistantToolArgumentsDeltaEvent(1, "\"a.txt\"}"));
        reducer.Apply(new AssistantToolArgumentsDeltaEvent(2, "\"dotnet test\"}"));

        Assert.Equal("{\"path\":\"a.txt\"}", reducer.Items.Single(item => item.Key == "call-a").ArgumentsJson);
        Assert.Equal("{\"command\":\"dotnet test\"}", reducer.Items.Single(item => item.Key == "call-b").ArgumentsJson);
    }

    [Fact]
    public void LargeAccumulatedRpcToolUpdateRemainsOneActivityItem()
    {
        var reducer = new PiActivityReducer();
        var output = string.Join('\n', Enumerable.Range(1, 10_000).Select(line => $"line {line}"));
        var arguments = new PiToolArguments("{\"command\":\"build\"}");

        reducer.Apply(new ToolStartedEvent("call-large", "bash", arguments));
        reducer.Apply(new ToolUpdatedEvent(
            "call-large", "bash", arguments, new PiToolResult(output, null, null)));
        reducer.Apply(new AgentSettledEvent());

        var item = Assert.Single(reducer.Items);
        Assert.Equal(PiActivityState.Running, item.State);
        Assert.Equal(10_000, item.Text.Split('\n').Length);
    }

    [Fact]
    public void TypedGetMessagesRestorationSeedsActivityWithoutSessionFileAccess()
    {
        var reducer = new PiActivityReducer();
        var restored = PiProtocolParser.ParseMessages(Parse(
            """{"data":{"messages":[{"role":"user","content":"question"},{"role":"assistant","content":[{"type":"text","text":"answer"}]},{"role":"toolResult","toolCallId":"call-read","toolName":"read","content":[{"type":"text","text":"file text"}],"isError":false}]}}"""));

        reducer.Reset(restored);

        Assert.Equal([PiActivityKind.UserText, PiActivityKind.AssistantText, PiActivityKind.Tool],
            reducer.Items.Select(item => item.Kind));
        var tool = reducer.Items[2];
        Assert.Equal(("call-read", "read", "file text"), (tool.Key, tool.ToolName, tool.Text));
    }

    [Fact]
    public void ResetDropsPriorRpcCorrelationState()
    {
        var reducer = new PiActivityReducer();
        reducer.Apply(new AssistantToolCallStartedEvent(1, "old-call", "read"));

        reducer.Reset([new PiActivityItem("restored", PiActivityKind.UserText, "You", "hello", PiActivityState.Completed)]);
        reducer.Apply(new AssistantToolArgumentsDeltaEvent(1, "stale"));

        var restored = Assert.Single(reducer.Items);
        Assert.Equal("restored", restored.Key);
        Assert.Null(restored.ArgumentsJson);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
