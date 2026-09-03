using System.Text.Json;
using PiDesk.Services;

namespace PiDesk.Tests;

public sealed class RpcResponseRouterTests
{
    [Fact]
    public async Task RoutesOutOfOrderResponsesToMatchingRequests()
    {
        var router = new RpcResponseRouter();
        var first = router.Register("first", generation: 1);
        var second = router.Register("second", generation: 1);

        Assert.True(router.TryRoute(Parse("""{"type":"response","id":"second","success":true}""")));
        Assert.True(router.TryRoute(Parse("""{"type":"response","id":"first","success":true}""")));

        Assert.Equal("first", (await first).GetProperty("id").GetString());
        Assert.Equal("second", (await second).GetProperty("id").GetString());
    }

    [Fact]
    public async Task FailingGenerationDoesNotAffectReplacementGeneration()
    {
        var router = new RpcResponseRouter();
        var oldRequest = router.Register("old", generation: 1);
        var currentRequest = router.Register("current", generation: 2);

        router.FailGeneration(1, new InvalidOperationException("stopped"));
        Assert.True(router.TryRoute(Parse("""{"type":"response","id":"current","success":true}""")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => oldRequest);
        Assert.Equal("current", (await currentRequest).GetProperty("id").GetString());
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

public sealed class StrictJsonlReaderTests
{
    [Fact]
    public async Task SplitsOnlyOnLineFeedAndAcceptsCrLf()
    {
        var records = new List<string>();
        using var input = new StringReader("first\u2028still-first\rsecond\r\nthird");

        await StrictJsonlReader.ReadAsync(input, line =>
        {
            records.Add(line);
            return Task.CompletedTask;
        });

        Assert.Equal(["first\u2028still-first\rsecond", "third"], records);
    }

    [Fact]
    public async Task AcceptsRecordExactlyAtConfiguredLimit()
    {
        var records = new List<string>();
        using var reader = new StringReader("12345678\n");

        await StrictJsonlReader.ReadAsync(reader, record =>
        {
            records.Add(record);
            return Task.CompletedTask;
        }, maximumRecordCharacters: 8);

        Assert.Equal(["12345678"], records);
    }

    [Theory]
    [InlineData("123456789")]
    [InlineData("123456789\n")]
    public async Task RejectsOversizedTerminatedAndUnterminatedRecords(string input)
    {
        using var reader = new StringReader(input);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            StrictJsonlReader.ReadAsync(reader, _ => Task.CompletedTask, maximumRecordCharacters: 8));

        Assert.Contains("8-character limit", exception.Message);
    }

    [Fact]
    public async Task MalformedRecordDoesNotPreventFollowingRecordFromParsing()
    {
        var messages = new List<JsonElement>();
        var errors = new List<string>();
        using var input = new StringReader("not-json\n{\"type\":\"agent_start\"}\n");

        await StrictJsonlReader.ReadAsync(input, line =>
        {
            if (RpcRecordParser.TryParse(line, out var message, out var error))
            {
                messages.Add(message);
            }
            else
            {
                errors.Add(error!);
            }
            return Task.CompletedTask;
        });

        Assert.Single(errors);
        Assert.Single(messages);
        Assert.Equal("agent_start", messages[0].GetProperty("type").GetString());
    }
}
