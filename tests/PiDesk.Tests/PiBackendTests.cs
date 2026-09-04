using System.ComponentModel;
using System.Diagnostics;
using PiDesk.Services;

namespace PiDesk.Tests;

public sealed class PiBackendTests
{
    [Fact]
    public async Task BackendCommandTimesOutAndTerminatesTheChild()
    {
        var pidPath = Path.Combine(Path.GetTempPath(), $"pidesk-command-{Guid.NewGuid():N}.pid");
        try
        {
            var runner = new ProcessCommandRunner(TimeSpan.FromMilliseconds(500), 4096);

            await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(
                "node.exe",
                ["-e", "require('fs').writeFileSync(process.argv[1],String(process.pid));setInterval(()=>{},1000)", pidPath],
                CancellationToken.None));

            var childPid = int.Parse(await File.ReadAllTextAsync(pidPath));
            AssertProcessExited(childPid);
        }
        finally
        {
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task CallerCancellationTerminatesTheBackendCommandChild()
    {
        var pidPath = Path.Combine(Path.GetTempPath(), $"pidesk-command-{Guid.NewGuid():N}.pid");
        try
        {
            var runner = new ProcessCommandRunner(TimeSpan.FromSeconds(30), 4096);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
                "node.exe",
                ["-e", "require('fs').writeFileSync(process.argv[1],String(process.pid));setInterval(()=>{},1000)", pidPath],
                cancellation.Token));

            var childPid = int.Parse(await File.ReadAllTextAsync(pidPath));
            AssertProcessExited(childPid);
        }
        finally
        {
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task BackendCommandRejectsExcessiveOutput()
    {
        var runner = new ProcessCommandRunner(TimeSpan.FromSeconds(5), 1024);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(
            "node.exe", ["-e", "process.stdout.write('x'.repeat(4096))"], CancellationToken.None));

        Assert.Contains("safety limit", error.Message);
    }

    [Fact]
    public void WindowsRuntimeBuildsDirectCleanRpcCommand()
    {
        var runtime = new PiRuntimeInfo(@"C:\node\node.exe", @"C:\npm\pi\cli.js", PiSessionService.MinimumSupportedPiVersion, PiBackend.Windows);

        var command = runtime.CreateStartInfo(@"C:\work folder");

        Assert.Equal(@"C:\node\node.exe", command.FileName);
        Assert.Equal(@"C:\work folder", command.WorkingDirectory);
        Assert.Equal([@"C:\npm\pi\cli.js", "--mode", "rpc"], command.ArgumentList);
        Assert.False(command.UseShellExecute);
        Assert.True(command.RedirectStandardInput);
        Assert.True(command.RedirectStandardOutput);
        Assert.True(command.RedirectStandardError);
    }

    [Fact]
    public async Task WslDiscoveryParsesSupportedListOutputWithoutHardcodedNames()
    {
        var runner = new RecordingRunner((_, _) => new(0, "\uFEFFDebian2\0\r\nUbuntu\0\r\nDebian2\r\n", string.Empty));
        var provider = new PiBackendProvider(runner);

        var backends = await provider.DiscoverAsync(CancellationToken.None);

        Assert.Equal(["Windows", "Debian2", "Ubuntu"], backends.Select(item => item.DisplayName));
        var call = Assert.Single(runner.Calls);
        Assert.Equal("wsl.exe", call.FileName);
        Assert.Equal(["--list", "--quiet"], call.Arguments);
    }

    [Fact]
    public async Task MissingWslKeepsWindowsAsTheDefaultAvailableBackend()
    {
        var provider = new PiBackendProvider(new RecordingRunner((_, _) => throw new Win32Exception("not installed")));

        var backend = Assert.Single(await provider.DiscoverAsync(CancellationToken.None));

        Assert.Equal(PiBackend.Windows, backend);
    }

    [Theory]
    [InlineData("relative\\repo")]
    [InlineData(@"\\server\share\repo")]
    [InlineData(@"C:\repo\..\other")]
    [InlineData(@"\\wsl.localhost\Debian2\home\me\repo")]
    public async Task WindowsRejectsNonCanonicalOrNonLocalProjectPaths(string path)
    {
        var provider = new PiBackendProvider(new RecordingRunner((_, _) =>
            throw new InvalidOperationException("No external command should run.")));

        var error = await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            provider.PrepareAsync(PiBackend.Windows, path, CancellationToken.None));

        Assert.Contains("path", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WslPreflightResolvesPathsAndBuildsShellFreeRpcLaunch()
    {
        var runner = SuccessfulWslRunner();
        var provider = new PiBackendProvider(runner);
        var backend = PiBackend.Wsl("Debian2");

        var prepared = await provider.PrepareAsync(backend, @"C:\Users\me\project", CancellationToken.None);
        var command = prepared.Runtime.CreateStartInfo(prepared.WorkingDirectory);

        Assert.Equal("/mnt/c/Users/me/project", prepared.WorkingDirectory);
        Assert.Equal("wsl.exe", command.FileName);
        Assert.Equal(
            ["--distribution", "Debian2", "--cd", "/mnt/c/Users/me/project", "--exec",
             "/usr/bin/node", "/home/me/.local/lib/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js", "--mode", "rpc"],
            command.ArgumentList);
        Assert.DoesNotContain(command.ArgumentList, argument => argument is "sh" or "/bin/sh" or "-c");
        Assert.Contains(runner.Calls, call => call.Arguments.Contains("wslpath"));
        Assert.Contains(runner.Calls, call => call.Arguments.Contains("-e"));
        var resolverScript = runner.Calls.SelectMany(call => call.Arguments)
            .Single(argument => argument.Contains("command -v node", StringComparison.Ordinal));
        Assert.Contains(".local/share/fnm/aliases/default/bin", resolverScript);
        Assert.DoesNotContain(".bashrc", resolverScript);
    }

    [Fact]
    public async Task WslTranslatesMatchingLocalhostPathWithoutGuessingAMountRoot()
    {
        var runner = SuccessfulWslRunner();
        var provider = new PiBackendProvider(runner);

        var prepared = await provider.PrepareAsync(
            PiBackend.Wsl("Debian3"), @"\\wsl.localhost\Debian3\home\me\repo", CancellationToken.None);

        Assert.Equal("/home/me/repo", prepared.WorkingDirectory);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("wslpath"));
    }

    [Theory]
    [InlineData("relative\\repo")]
    [InlineData(@"\\server\share\repo")]
    [InlineData(@"C:\repo\..\other")]
    [InlineData(@"\\wsl.localhost\Debian3\home\\repo")]
    public async Task WslRejectsMalformedOrUnmappablePathShapes(string path)
    {
        var provider = new PiBackendProvider(new RecordingRunner((_, _) =>
            throw new InvalidOperationException("No external command should run.")));

        var error = await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            provider.PrepareAsync(PiBackend.Wsl("Debian3"), path, CancellationToken.None));

        Assert.Contains("path", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WslRejectsPathOwnedByAnotherDistribution()
    {
        var provider = new PiBackendProvider(new RecordingRunner((_, _) =>
            throw new InvalidOperationException("No external command should run.")));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => provider.PrepareAsync(
            PiBackend.Wsl("Debian2"), @"\\wsl.localhost\Debian3\home\me\repo", CancellationToken.None));

        Assert.Contains("Debian3", error.Message);
        Assert.Contains("Debian2", error.Message);
    }

    [Fact]
    public async Task WslReportsUnmappableMountedDriveWithGuidance()
    {
        var runner = new RecordingRunner((_, arguments) => arguments.Contains("wslpath")
            ? new(1, string.Empty, "drive is not mounted")
            : throw new InvalidOperationException("Preflight must stop after translation fails."));
        var provider = new PiBackendProvider(runner);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => provider.PrepareAsync(
            PiBackend.Wsl("Debian2"), @"Z:\repo", CancellationToken.None));

        Assert.Contains("could not be mapped", error.Message);
        Assert.Contains("mounted", error.Message);
    }

    [Fact]
    public async Task WindowsInteropPiDoesNotSubstituteForMissingNativeWslNode()
    {
        var runner = new RecordingRunner((_, arguments) => arguments.Contains("wslpath")
            ? new(0, "/mnt/c/repo\n", string.Empty)
            : new(20, string.Empty, string.Empty));
        var provider = new PiBackendProvider(runner);

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => provider.PrepareAsync(
            PiBackend.Wsl("Debian-Recovered"), @"C:\repo", CancellationToken.None));

        Assert.Contains("Native Linux Node.js", error.Message);
        Assert.Contains("WSL interop", error.Message);
    }

    [Fact]
    public async Task MissingWslPiFailsDuringPreflightBeforeRpcLaunch()
    {
        var runner = new RecordingRunner((_, arguments) => arguments.Contains("wslpath")
            ? new(0, "/mnt/c/repo\n", string.Empty)
            : new(21, string.Empty, "pi not found"));
        var provider = new PiBackendProvider(runner);

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => provider.PrepareAsync(
            PiBackend.Wsl("Debian2"), @"C:\repo", CancellationToken.None));

        Assert.Contains("native Pi installation", error.Message);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("-e"));
    }

    [Theory]
    [InlineData("0.84.4")]
    [InlineData("0.86.0")]
    public async Task OutOfRangeWslVersionFailsBeforeRpcLaunch(string version)
    {
        var provider = new StaticBackendProvider((backend, path) =>
            new(backend, new PiRuntimeInfo("/usr/bin/node", "/pi/cli.js", version, backend), "/mnt/c/repo"));
        var launched = false;
        await using var session = new PiSessionService(_ =>
        {
            launched = true;
            throw new InvalidOperationException("must not launch");
        }, provider);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            session.StartAsync(PiBackend.Wsl("Debian2"), @"C:\repo"));

        Assert.False(launched);
        Assert.Contains("Debian2", error.Message);
        Assert.Contains(PiSessionService.MinimumSupportedPiVersion, error.Message);
    }

    [Fact]
    public async Task WindowsToWslAndBackCommitsEachCandidateAtomically()
    {
        var lifecycle = TempLog();
        try
        {
            var provider = new StaticBackendProvider((backend, path) => Prepared(backend, path));
            await using var session = new PiSessionService(
                runtime => CreateRpc(runtime.Backend?.Kind == PiBackendKind.Wsl ? "persistent" : "normal", lifecycle), provider);

            var windows = await session.StartAsync(PiBackend.Windows, Directory.GetCurrentDirectory());
            var wsl = await session.StartAsync(PiBackend.Wsl("Debian2"), Directory.GetCurrentDirectory());
            Assert.Equal("Debian2", session.CurrentBackend?.Distribution);
            Assert.Equal("restored question", wsl.Messages[0].Text);
            Assert.True(wsl.SessionGeneration > windows.SessionGeneration);

            var windowsAgain = await session.StartAsync(PiBackend.Windows, Directory.GetCurrentDirectory());
            Assert.Equal(PiBackendKind.Windows, session.CurrentBackend?.Kind);
            Assert.Empty(windowsAgain.Messages);
            Assert.True(windowsAgain.SessionGeneration > wsl.SessionGeneration);
            AssertCandidateBeforePreviousExit(await File.ReadAllLinesAsync(lifecycle));
        }
        finally
        {
            File.Delete(lifecycle);
        }
    }

    [Fact]
    public async Task FailedBackendCandidatePreservesBackendAndAuthoritativeSnapshot()
    {
        var lifecycle = TempLog();
        try
        {
            var provider = new StaticBackendProvider((backend, path) => Prepared(backend, path));
            await using var session = new PiSessionService(
                runtime => CreateRpc(runtime.Backend?.Kind == PiBackendKind.Wsl ? "candidate-load-failure" : "persistent", lifecycle), provider);
            var original = await session.StartAsync(PiBackend.Windows, Directory.GetCurrentDirectory());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.StartAsync(PiBackend.Wsl("Debian2"), Directory.GetCurrentDirectory()));

            Assert.Equal(PiBackend.Windows, session.CurrentBackend);
            Assert.Equal(original.SessionGeneration, session.SessionGeneration);
            Assert.Equal("restored question", (await session.GetSnapshotAsync()).Messages[0].Text);
            Assert.Equal(PiSessionLifecycleState.Connected, session.LifecycleState);
        }
        finally
        {
            File.Delete(lifecycle);
        }
    }

    [Fact]
    public async Task CancelledBackendReplacementPreservesCurrentBackendAndSnapshot()
    {
        var lifecycle = TempLog();
        try
        {
            var provider = new StaticBackendProvider((backend, path) => backend.Kind == PiBackendKind.Wsl
                ? throw new OperationCanceledException("replacement cancelled")
                : Prepared(backend, path));
            await using var session = new PiSessionService(_ => CreateRpc("persistent", lifecycle), provider);
            var original = await session.StartAsync(PiBackend.Windows, Directory.GetCurrentDirectory());

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                session.StartAsync(PiBackend.Wsl("Debian2"), Directory.GetCurrentDirectory()));

            Assert.Equal(PiBackend.Windows, session.CurrentBackend);
            Assert.Equal(original.SessionGeneration, session.SessionGeneration);
            Assert.Equal("restored question", (await session.GetSnapshotAsync()).Messages[0].Text);
        }
        finally
        {
            File.Delete(lifecycle);
        }
    }

    [Fact]
    public async Task ReplacedBackendLateEventsCannotChangeCurrentState()
    {
        var lifecycle = TempLog();
        try
        {
            var provider = new StaticBackendProvider((backend, path) => Prepared(backend, path));
            await using var session = new PiSessionService(
                runtime => CreateRpc(runtime.Backend?.Kind == PiBackendKind.Windows ? "stale" : "normal", lifecycle), provider);
            var stale = 0;
            session.EventReceived += value =>
            {
                if (value is UnknownSessionEvent { Type: "stale_event" }) stale++;
                return Task.CompletedTask;
            };

            await session.StartAsync(PiBackend.Windows, Directory.GetCurrentDirectory());
            await session.StartAsync(PiBackend.Wsl("Debian2"), Directory.GetCurrentDirectory());

            Assert.Equal(0, stale);
            Assert.Equal("Debian2", session.CurrentBackend?.Distribution);
            Assert.Equal(PiSessionLifecycleState.Connected, session.LifecycleState);
        }
        finally
        {
            File.Delete(lifecycle);
        }
    }

    private static RecordingRunner SuccessfulWslRunner() => new((_, arguments) =>
    {
        if (arguments.Contains("wslpath")) return new(0, "/mnt/c/Users/me/project\n", string.Empty);
        if (arguments.Contains("-e")) return new(0, "{\"cli\":\"/home/me/.local/lib/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js\",\"version\":\"0.85.0\"}", string.Empty);
        if (arguments.Contains("test -d \"$1\"")) return new(0, string.Empty, string.Empty);
        return new(0, "/usr/bin/node\n/home/me/.local/lib/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js\n", string.Empty);
    });

    private static PiPreparedBackend Prepared(PiBackend backend, string path) =>
        new(backend, new PiRuntimeInfo("node.exe", "fake.js", PiSessionService.MinimumSupportedPiVersion, backend), path);

    private static string TempLog() => Path.Combine(Path.GetTempPath(), $"pidesk-backend-{Guid.NewGuid():N}.log");

    private static PiRpcClient CreateRpc(string behavior, string lifecyclePath)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-rpc.js");
        return new PiRpcClient(workingDirectory =>
        {
            var info = PiBackendProvider.RedirectedStartInfo("node.exe");
            info.WorkingDirectory = workingDirectory;
            info.ArgumentList.Add(fixture);
            info.ArgumentList.Add(behavior);
            info.ArgumentList.Add(lifecyclePath);
            return info;
        }, TimeSpan.FromSeconds(5));
    }

    private static void AssertProcessExited(int processId)
    {
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
    }

    private static void AssertCandidateBeforePreviousExit(string[] records)
    {
        var starts = records.Select((line, index) => (line, index))
            .Where(record => record.line.StartsWith("start ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, starts.Length);
        for (var index = 0; index < starts.Length - 1; index++)
        {
            var pid = starts[index].line[6..];
            var exit = Array.FindIndex(records, line => line == $"exit {pid}");
            Assert.True(starts[index + 1].index < exit);
        }
    }

    private sealed class RecordingRunner(Func<string, IReadOnlyList<string>, CommandResult> handler) : ICommandRunner
    {
        public List<(string FileName, string[] Arguments)> Calls { get; } = [];

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return Task.FromResult(handler(fileName, arguments));
        }
    }

    private sealed class StaticBackendProvider(Func<PiBackend, string, PiPreparedBackend> prepare) : IPiBackendProvider
    {
        public Task<IReadOnlyList<PiBackend>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PiBackend>>([PiBackend.Windows, PiBackend.Wsl("Debian2")]);

        public Task<PiPreparedBackend> PrepareAsync(PiBackend backend, string projectPath, CancellationToken cancellationToken) =>
            Task.FromResult(prepare(backend, projectPath));
    }
}
