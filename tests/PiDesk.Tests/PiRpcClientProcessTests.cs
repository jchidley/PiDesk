using System.Diagnostics;
using System.Text.Json.Nodes;
using PiDesk.Services;

namespace PiDesk.Tests;

public sealed class PiRpcClientProcessTests
{
    [Fact]
    public async Task MalformedRecordIsRetainedDiagnosticAndFollowingResponseCompletes()
    {
        await using var client = CreateClient("malformed");
        var observed = new TaskCompletionSource<RpcDiagnostic>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DiagnosticReceived += diagnostic =>
        {
            if (diagnostic.Kind == RpcDiagnosticKind.MalformedRecord)
            {
                observed.TrySetResult(diagnostic);
            }
        };

        await client.StartAsync(Directory.GetCurrentDirectory());
        var diagnostic = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("malformed JSONL", diagnostic.Message);
        Assert.NotEqual(default, diagnostic.Timestamp);
        Assert.Contains(client.Diagnostics, item => item == diagnostic);
        Assert.True(client.IsRunning);
    }

    [Fact]
    public async Task UnknownEventIsRetainedWithoutStoppingReader()
    {
        await using var client = CreateClient("unknown");
        var observed = new TaskCompletionSource<RpcDiagnostic>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DiagnosticReceived += diagnostic =>
        {
            if (diagnostic.Kind == RpcDiagnosticKind.UnknownEvent)
            {
                observed.TrySetResult(diagnostic);
            }
        };

        await client.StartAsync(Directory.GetCurrentDirectory());

        Assert.Contains("future_event", (await observed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Message);
        Assert.True(client.IsRunning);
    }

    [Fact]
    public async Task IntentionalStopAndReplacementExitEveryChildWithoutError()
    {
        var lifecyclePath = Path.Combine(Path.GetTempPath(), $"pidesk-fake-{Guid.NewGuid():N}.log");
        try
        {
            await using var client = CreateClient("normal", lifecyclePath: lifecyclePath);
            var errors = new List<string>();
            client.ErrorReceived += errors.Add;

            await client.StartAsync(Directory.GetCurrentDirectory());
            await client.StartAsync(Directory.GetCurrentDirectory());
            await client.StopAsync();

            var records = await File.ReadAllLinesAsync(lifecyclePath);
            var starts = records.Where(line => line.StartsWith("start ", StringComparison.Ordinal)).Select(Pid).ToArray();
            var exits = records.Where(line => line.StartsWith("exit ", StringComparison.Ordinal)).Select(Pid).ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Equal(starts.Order(), exits.Order());
            Assert.Empty(errors);
            Assert.False(client.IsRunning);
        }
        finally
        {
            File.Delete(lifecyclePath);
        }
    }

    [Fact]
    public async Task DelayedOutputFromStoppedGenerationIsIgnored()
    {
        await using var client = CreateClient("stale");
        var staleEvents = 0;
        client.EventReceived += message =>
        {
            if (message.TryGetProperty("type", out var type) && type.GetString() == "stale_event")
            {
                Interlocked.Increment(ref staleEvents);
            }
            return Task.CompletedTask;
        };

        await client.StartAsync(Directory.GetCurrentDirectory());
        await client.StartAsync(Directory.GetCurrentDirectory());
        await client.StopAsync();

        Assert.Equal(0, staleEvents);
    }

    [Fact]
    public async Task CleanUnexpectedEofReportsOneRecoverableFault()
    {
        await using var client = CreateClient("eof");
        var errors = new List<string>();
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ErrorReceived += message =>
        {
            lock (errors)
            {
                errors.Add(message);
            }
            reported.TrySetResult(message);
        };

        await client.StartAsync(Directory.GetCurrentDirectory());
        var message = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("code 0", message);
        Assert.Contains("reconnect", message, StringComparison.OrdinalIgnoreCase);
        lock (errors)
        {
            Assert.Single(errors);
        }
    }

    [Fact]
    public async Task UnexpectedExitFailsRequestAndReportsOnceWithStderr()
    {
        await using var client = CreateClient("crash");
        var errors = new List<string>();
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ErrorReceived += message =>
        {
            lock (errors)
            {
                errors.Add(message);
            }
            reported.TrySetResult();
        };
        await client.StartAsync(Directory.GetCurrentDirectory());

        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(Prompt()));
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (errors)
        {
            var error = Assert.Single(errors);
            Assert.Contains("code 7", error);
            Assert.Contains("fake child failure", error);
            Assert.Contains("reconnect", error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task OversizedStderrRetainsBoundedNewestContext()
    {
        await using var client = CreateClient("oversized-stderr");
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ErrorReceived += message => reported.TrySetResult(message);
        await client.StartAsync(Directory.GetCurrentDirectory());

        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(Prompt()));
        var error = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("TAIL-MARKER", error);
        Assert.DoesNotContain("HEAD-MARKER", error);
        Assert.True(error.Length < PiRpcClient.MaximumStderrCharacters + 500);
    }

    [Fact]
    public async Task OversizedStdoutRecordFaultsAndTerminatesGeneration()
    {
        await using var client = CreateClient("oversized-stdout");
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ErrorReceived += message => reported.TrySetResult(message);

        await client.StartAsync(Directory.GetCurrentDirectory());
        var error = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("record exceeded", error);
        Assert.False(client.IsRunning);
    }

    [Fact]
    public async Task RequestTimeoutRemovesPendingRequestAndDoesNotHangShutdown()
    {
        await using var client = CreateClient("timeout", timeout: TimeSpan.FromMilliseconds(100));
        await client.StartAsync(Directory.GetCurrentDirectory());

        await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync(Prompt()));
        Assert.Equal(0, client.PendingRequestCount);
        await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CallerCancellationRemovesPendingRequestAndClientRemainsStoppable()
    {
        await using var client = CreateClient("timeout");
        await client.StartAsync(Directory.GetCurrentDirectory());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(Prompt(), cancellation.Token));
        Assert.Equal(0, client.PendingRequestCount);
        await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ThrowingObserversCannotLeakRequestOrBlockOtherObservers()
    {
        await using var client = CreateClient("crash");
        var errorObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DiagnosticReceived += _ => throw new InvalidOperationException("diagnostic observer");
        client.ErrorReceived += _ => throw new InvalidOperationException("error observer");
        client.ErrorReceived += _ => errorObserved.TrySetResult();

        await client.StartAsync(Directory.GetCurrentDirectory());
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(Prompt()));
        await errorObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, client.PendingRequestCount);
        Assert.Contains(client.Diagnostics, item => item.Kind == RpcDiagnosticKind.ObserverFailure);
    }

    private static JsonObject Prompt() => new() { ["type"] = "prompt", ["message"] = "test" };

    private static int Pid(string record) => int.Parse(record[(record.IndexOf(' ') + 1)..]);

    private static PiRpcClient CreateClient(
        string behavior,
        TimeSpan? timeout = null,
        string? lifecyclePath = null)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-rpc.js");
        return new PiRpcClient(workingDirectory =>
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "node.exe",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(fixture);
            startInfo.ArgumentList.Add(behavior);
            if (lifecyclePath is not null)
            {
                startInfo.ArgumentList.Add(lifecyclePath);
            }
            return startInfo;
        }, timeout ?? TimeSpan.FromSeconds(5));
    }
}
