using System.Diagnostics;
using System.Text.Json;

namespace PiDesk.Services;

internal sealed record PiRuntimeInfo(
    string NodePath,
    string CliPath,
    string Version,
    PiBackend? Backend = null,
    IReadOnlyList<string>? AdditionalArguments = null)
{
    public ProcessStartInfo CreateStartInfo(string workingDirectory)
    {
        if (Backend is { Kind: PiBackendKind.Wsl, Distribution: { } distribution })
        {
            return PiBackendProvider.CreateWslRpcStartInfo(distribution, workingDirectory, NodePath, CliPath);
        }

        var startInfo = PiBackendProvider.RedirectedStartInfo(NodePath);
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.ArgumentList.Add(CliPath);
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add("rpc");
        foreach (var argument in AdditionalArguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }
}

internal static class PiRuntimeResolver
{
    public static PiRuntimeInfo Resolve() => ResolveWindows();

    public static PiRuntimeInfo ResolveWindows()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directoryValue in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var directory = directoryValue.Trim('"');
                if (!File.Exists(Path.Combine(directory, "pi.cmd")))
                {
                    continue;
                }

                var packageDirectory = Path.Combine(directory, "node_modules", "@earendil-works", "pi-coding-agent");
                var cliPath = Path.Combine(packageDirectory, "dist", "bundle", "cli.js");
                var packagePath = Path.Combine(packageDirectory, "package.json");
                if (!File.Exists(cliPath) || !File.Exists(packagePath))
                {
                    continue;
                }

                using var package = JsonDocument.Parse(File.ReadAllText(packagePath));
                var version = package.RootElement.TryGetProperty("version", out var value) ? value.GetString() : null;
                if (string.IsNullOrWhiteSpace(version))
                {
                    throw new InvalidDataException($"Pi package metadata has no version: {packagePath}");
                }

                var adjacentNode = Path.Combine(directory, "node.exe");
                return new PiRuntimeInfo(File.Exists(adjacentNode) ? adjacentNode : "node.exe", cliPath, version, PiBackend.Windows);
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        throw new FileNotFoundException("Pi was not found on PATH. Install @earendil-works/pi-coding-agent and restart PiDesk.");
    }
}
