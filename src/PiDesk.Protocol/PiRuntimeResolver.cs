using System.Diagnostics;
using System.Text.Json;

namespace PiDesk.Services;

internal sealed record PiRuntimeInfo(string NodePath, string CliPath, string Version)
{
    public ProcessStartInfo CreateStartInfo(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = NodePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(CliPath);
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add("rpc");
        return startInfo;
    }
}

internal static class PiRuntimeResolver
{
    public static PiRuntimeInfo Resolve()
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
                return new PiRuntimeInfo(File.Exists(adjacentNode) ? adjacentNode : "node.exe", cliPath, version);
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        throw new FileNotFoundException("Pi was not found on PATH. Install @earendil-works/pi-coding-agent and restart PiDesk.");
    }
}
