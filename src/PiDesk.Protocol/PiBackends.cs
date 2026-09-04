using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiDesk.Services;

public enum PiBackendKind
{
    Windows,
    Wsl,
}

public sealed record PiBackend(string Id, string DisplayName, PiBackendKind Kind, string? Distribution = null)
{
    public static PiBackend Windows { get; } = new("windows", "Windows", PiBackendKind.Windows);

    public static PiBackend Wsl(string distribution) =>
        new($"wsl:{distribution}", distribution, PiBackendKind.Wsl, distribution);
}

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

internal interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal sealed class ProcessCommandRunner : ICommandRunner
{
    private const int DefaultMaximumOutputCharacters = 65_536;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout;
    private readonly int _maximumOutputCharacters;

    public ProcessCommandRunner() : this(DefaultTimeout, DefaultMaximumOutputCharacters)
    {
    }

    internal ProcessCommandRunner(TimeSpan timeout, int maximumOutputCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputCharacters);
        _timeout = timeout;
        _maximumOutputCharacters = maximumOutputCharacters;
    }

    public async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Could not start {fileName}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var stdout = ReadBoundedAsync(process.StandardOutput, process, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, process, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new CommandResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timeout.Cancel();
            await TerminateAsync(process);
            await ObserveAsync(stdout, stderr);
            throw new TimeoutException($"{fileName} did not complete within {_timeout.TotalSeconds:g} seconds.");
        }
        catch
        {
            timeout.Cancel();
            await TerminateAsync(process);
            await ObserveAsync(stdout, stderr);
            throw;
        }
    }

    private async Task<string> ReadBoundedAsync(StreamReader reader, Process process, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return output.ToString();
            }
            if (output.Length + count > _maximumOutputCharacters)
            {
                TryKill(process);
                throw new InvalidDataException(
                    $"Backend command output exceeded the {_maximumOutputCharacters:N0}-character safety limit.");
            }
            output.Append(buffer, 0, count);
        }
    }

    private static async Task TerminateAsync(Process process)
    {
        TryKill(process);
        try
        {
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // The process exited before termination was requested.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and termination.
        }
    }

    private static async Task ObserveAsync(params Task<string>[] readers)
    {
        foreach (var reader in readers)
        {
            try
            {
                await reader;
            }
            catch
            {
                // Preserve the command's primary timeout, cancellation, or output-limit failure.
            }
        }
    }
}

internal sealed record PiPreparedBackend(PiBackend Backend, PiRuntimeInfo Runtime, string WorkingDirectory);

internal interface IPiBackendProvider
{
    Task<IReadOnlyList<PiBackend>> DiscoverAsync(CancellationToken cancellationToken);
    Task<PiPreparedBackend> PrepareAsync(PiBackend backend, string projectPath, CancellationToken cancellationToken);
}

internal sealed class PiBackendProvider(ICommandRunner commandRunner) : IPiBackendProvider
{
    private const string ResolveWslCommands = "node=$(command -v node 2>/dev/null || true); pi=$(command -v pi 2>/dev/null || true); case \"$node\" in /mnt/*) node=;; esac; case \"$pi\" in /mnt/*) pi=;; esac; fnm_bin=\"$HOME/.local/share/fnm/aliases/default/bin\"; if [ -z \"$node\" ] && [ -x \"$fnm_bin/node\" ]; then node=$(readlink -f \"$fnm_bin/node\"); fi; if [ -z \"$pi\" ] && [ -e \"$fnm_bin/pi\" ]; then pi=$(readlink -f \"$fnm_bin/pi\"); fi; [ -n \"$node\" ] || exit 20; [ -n \"$pi\" ] || exit 21; printf '%s\\n%s\\n' \"$node\" \"$(readlink -f \"$pi\")\"";
    private const string ReadPackageMetadata = "const fs=require('fs'),p=require('path'),cli=process.argv[1],pkg=p.resolve(p.dirname(cli),'..','..','package.json');if(!fs.existsSync(cli)||!fs.existsSync(pkg))process.exit(22);const m=JSON.parse(fs.readFileSync(pkg,'utf8'));if(!m.version)process.exit(23);process.stdout.write(JSON.stringify({cli:p.resolve(cli),version:m.version}));";
    private static readonly Regex DrivePath = new("^[A-Za-z]:[\\\\/]", RegexOptions.CultureInvariant);

    public PiBackendProvider() : this(new ProcessCommandRunner())
    {
    }

    public async Task<IReadOnlyList<PiBackend>> DiscoverAsync(CancellationToken cancellationToken)
    {
#if DEBUG
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PIDESK_UI_TEST_RPC_SCRIPT")))
        {
            return [PiBackend.Windows, PiBackend.Wsl("Fixture WSL"), PiBackend.Wsl("Unavailable WSL")];
        }
#endif
        CommandResult result;
        try
        {
            result = await commandRunner.RunAsync("wsl.exe", ["--list", "--quiet"], cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return [PiBackend.Windows];
        }
        if (result.ExitCode != 0)
        {
            // WSL itself is optional. Windows remains usable when it is absent or disabled.
            return [PiBackend.Windows];
        }

        var distributions = ParseWslDistributions(result.StandardOutput);
        return [PiBackend.Windows, .. distributions.Select(PiBackend.Wsl)];
    }

    public async Task<PiPreparedBackend> PrepareAsync(
        PiBackend backend,
        string projectPath,
        CancellationToken cancellationToken)
    {
#if DEBUG
        var fixture = Environment.GetEnvironmentVariable("PIDESK_UI_TEST_RPC_SCRIPT");
        if (!string.IsNullOrWhiteSpace(fixture))
        {
            if (backend.Distribution == "Unavailable WSL")
            {
                throw new FileNotFoundException("Pi is not available in Unavailable WSL. Install Pi in that distribution and try again.");
            }
            var fullPath = Path.GetFullPath(fixture);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("The PiDesk UI-test RPC fixture was not found.", fullPath);
            }
            var fixtureRuntime = new PiRuntimeInfo("node.exe", fullPath, PiSessionService.MinimumSupportedPiVersion,
                PiBackend.Windows, ["--fixture-backend", backend.DisplayName]);
            return new PiPreparedBackend(backend, fixtureRuntime, projectPath);
        }
#endif
        return backend.Kind switch
        {
            PiBackendKind.Windows => PrepareWindows(backend, projectPath),
            PiBackendKind.Wsl => await PrepareWslAsync(backend, projectPath, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported Pi backend: {backend.Kind}"),
        };
    }

    internal static IReadOnlyList<string> ParseWslDistributions(string output) =>
        output.Replace("\0", string.Empty, StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.TrimStart('\uFEFF'))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static string TranslateWslUncPath(string projectPath, string selectedDistribution)
    {
        const string prefix = @"\\wsl.localhost\";
        if (!projectPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The project path is not a \\wsl.localhost path.", nameof(projectPath));
        }

        var remainder = projectPath[prefix.Length..];
        var separator = remainder.IndexOfAny(['\\', '/']);
        var owner = separator < 0 ? remainder : remainder[..separator];
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException("The WSL project path does not name a distribution.", nameof(projectPath));
        }
        if (!string.Equals(owner, selectedDistribution, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"This project belongs to WSL distribution '{owner}', not selected distribution '{selectedDistribution}'. Select '{owner}' or choose a path owned by '{selectedDistribution}'.",
                nameof(projectPath));
        }

        var pathPart = separator < 0 ? string.Empty : remainder[(separator + 1)..];
        var segments = pathPart.Split(['\\', '/'], StringSplitOptions.None);
        if (segments.Any(segment => segment is "." or ".." || (segment.Length == 0 && pathPart.Length > 0)))
        {
            throw new ArgumentException("The WSL project path is malformed. Choose the folder again using its canonical \\wsl.localhost path.", nameof(projectPath));
        }
        return pathPart.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }

    internal static ProcessStartInfo CreateWslRpcStartInfo(
        string distribution,
        string workingDirectory,
        string nodePath,
        string cliPath)
    {
        var info = RedirectedStartInfo("wsl.exe");
        foreach (var argument in new[]
        {
            "--distribution", distribution, "--cd", workingDirectory, "--exec",
            nodePath, cliPath, "--mode", "rpc",
        })
        {
            info.ArgumentList.Add(argument);
        }
        return info;
    }

    private static PiPreparedBackend PrepareWindows(PiBackend backend, string projectPath)
    {
        if (projectPath.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase) ||
            projectPath.StartsWith(@"\\wsl$\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A WSL-owned project requires selecting its matching WSL distribution.", nameof(projectPath));
        }
        if (projectPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("Network project paths are not supported. Choose an absolute local Windows folder.", nameof(projectPath));
        }
        if (!Path.IsPathFullyQualified(projectPath) ||
            projectPath.Split(['\\', '/']).Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Choose an absolute canonical Windows folder without traversal segments.", nameof(projectPath));
        }
        if (!Directory.Exists(projectPath))
        {
            throw new DirectoryNotFoundException($"Windows project folder does not exist: {projectPath}");
        }

        return new PiPreparedBackend(backend, PiRuntimeResolver.ResolveWindows(), Path.GetFullPath(projectPath));
    }

    private async Task<PiPreparedBackend> PrepareWslAsync(
        PiBackend backend,
        string projectPath,
        CancellationToken cancellationToken)
    {
        var distribution = backend.Distribution;
        if (string.IsNullOrWhiteSpace(distribution))
        {
            throw new ArgumentException("The WSL backend does not name a distribution.", nameof(backend));
        }

        var linuxProjectPath = await TranslateProjectPathAsync(distribution, projectPath, cancellationToken);
        var prefix = new[] { "--distribution", distribution, "--exec" };
        var resolved = await commandRunner.RunAsync("wsl.exe", [.. prefix, "/bin/sh", "-c", ResolveWslCommands], cancellationToken);
        if (resolved.ExitCode != 0)
        {
            var guidance = resolved.ExitCode switch
            {
                20 => "Native Linux Node.js was not found. Install Node.js and @earendil-works/pi-coding-agent for this distribution's default user; a Windows executable inherited through WSL interop is not a WSL installation.",
                21 => "A native Pi installation was not found. Install @earendil-works/pi-coding-agent for this distribution's default user.",
                _ => "Install native Linux Node.js and @earendil-works/pi-coding-agent for this distribution's default user.",
            };
            throw new FileNotFoundException(
                $"Pi cannot start in WSL distribution '{distribution}'. {guidance} {CleanError(resolved.StandardError)}");
        }

        var paths = CleanLines(resolved.StandardOutput);
        if (paths.Length != 2 || !paths.All(path => path.StartsWith("/", StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"WSL distribution '{distribution}' returned malformed Pi executable paths during preflight.");
        }

        var metadata = await commandRunner.RunAsync(
            "wsl.exe", [.. prefix, paths[0], "-e", ReadPackageMetadata, paths[1]], cancellationToken);
        if (metadata.ExitCode != 0)
        {
            throw new FileNotFoundException(
                $"Pi's executable or package metadata could not be validated in WSL distribution '{distribution}'. Reinstall Pi there and try again. {CleanError(metadata.StandardError)}");
        }

        string cliPath;
        string version;
        try
        {
            using var document = JsonDocument.Parse(metadata.StandardOutput);
            cliPath = document.RootElement.GetProperty("cli").GetString() ?? string.Empty;
            version = document.RootElement.GetProperty("version").GetString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException($"WSL distribution '{distribution}' returned malformed Pi package metadata during preflight.", ex);
        }
        if (!cliPath.StartsWith("/", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidDataException($"WSL distribution '{distribution}' returned incomplete Pi package metadata during preflight.");
        }

        var folder = await commandRunner.RunAsync(
            "wsl.exe", [.. prefix, "/bin/sh", "-c", "test -d \"$1\"", "pidesk", linuxProjectPath], cancellationToken);
        if (folder.ExitCode != 0)
        {
            throw new DirectoryNotFoundException(
                $"Project folder '{linuxProjectPath}' is not available in WSL distribution '{distribution}'. Check that the drive is mounted or select a folder owned by that distribution.");
        }

        var runtime = new PiRuntimeInfo(paths[0], cliPath, version, backend);
        return new PiPreparedBackend(backend, runtime, linuxProjectPath);
    }

    private async Task<string> TranslateProjectPathAsync(
        string distribution,
        string projectPath,
        CancellationToken cancellationToken)
    {
        if (projectPath.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase))
        {
            return TranslateWslUncPath(projectPath, distribution);
        }
        if (projectPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Network and WSL paths outside \\wsl.localhost cannot be mapped safely. Choose a local Windows folder or a matching \\wsl.localhost folder.",
                nameof(projectPath));
        }
        if (!DrivePath.IsMatch(projectPath) || projectPath.Split(['\\', '/']).Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Choose an absolute Windows drive path or a canonical matching \\wsl.localhost path.", nameof(projectPath));
        }

        var result = await commandRunner.RunAsync(
            "wsl.exe", ["--distribution", distribution, "--exec", "wslpath", "-a", "-u", projectPath], cancellationToken);
        var lines = CleanLines(result.StandardOutput);
        if (result.ExitCode != 0 || lines.Length != 1 || !lines[0].StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Windows project path '{projectPath}' could not be mapped in WSL distribution '{distribution}'. Check that the drive is mounted, then choose the folder again. {CleanError(result.StandardError)}",
                nameof(projectPath));
        }
        return lines[0];
    }

    private static string[] CleanLines(string value) =>
        value.Replace("\0", string.Empty, StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string CleanError(string value) =>
        value.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();

    internal static ProcessStartInfo RedirectedStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
}
