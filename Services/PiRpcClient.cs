using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiDesk.Services;

public sealed class PiRpcClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private CancellationTokenSource? _lifetime;
    private Task? _outputReader;
    private Task? _errorReader;

    public event Func<JsonElement, Task>? EventReceived;
    public event Action<string>? ErrorReceived;
    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working folder does not exist: {workingDirectory}");
        }

        await StopAsync();
        var (nodePath, cliPath) = ResolvePiRuntime();
        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(cliPath);
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add("rpc");

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pi.");
        _lifetime = new CancellationTokenSource();
        _outputReader = ReadOutputAsync(_process, _lifetime.Token);
        _errorReader = ReadErrorsAsync(_process, _lifetime.Token);

        await SendAsync(new JsonObject { ["type"] = "get_state" }, cancellationToken);
    }

    public async Task SendNotificationAsync(JsonObject command, CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            throw new InvalidOperationException("Pi is not running.");
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(command.ToJsonString());
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<JsonElement> SendAsync(JsonObject command, CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            throw new InvalidOperationException("Pi is not running.");
        }

        var id = Guid.NewGuid().ToString("N");
        command["id"] = id;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Could not register RPC request.");
        }

        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await process.StandardInput.WriteLineAsync(command.ToJsonString());
                await process.StandardInput.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }

            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (!response.TryGetProperty("success", out var success) || !success.GetBoolean())
            {
                var error = response.TryGetProperty("error", out var errorValue)
                    ? errorValue.GetString()
                    : "Pi rejected the request.";
                throw new InvalidOperationException(error);
            }

            return response;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task StopAsync()
    {
        var lifetime = _lifetime;
        var process = _process;
        var outputReader = _outputReader;
        var errorReader = _errorReader;
        _lifetime = null;
        _process = null;
        _outputReader = null;
        _errorReader = null;

        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(new InvalidOperationException("Pi stopped before the request completed."));
        }
        _pending.Clear();

        if (process is not null)
        {
            process.StandardInput.Close();
            var exitTask = process.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(1500)) != exitTask)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            await exitTask;

            if (outputReader is not null && errorReader is not null)
            {
                await Task.WhenAll(outputReader, errorReader);
            }
            process.Dispose();
        }
        else
        {
            lifetime?.Cancel();
        }

        lifetime?.Dispose();
    }

    private async Task ReadOutputAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var message = document.RootElement.Clone();
                if (message.GetProperty("type").GetString() == "response" &&
                    message.TryGetProperty("id", out var idValue) &&
                    idValue.GetString() is { } id &&
                    _pending.TryGetValue(id, out var completion))
                {
                    completion.TrySetResult(message);
                }
                else if (EventReceived is { } handler)
                {
                    await handler(message);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                ErrorReceived?.Invoke($"Pi exited with code {process.ExitCode}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorReceived?.Invoke($"Could not read Pi output: {ex.Message}");
        }
    }

    private async Task ReadErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }
                if (!string.IsNullOrWhiteSpace(line))
                {
                    ErrorReceived?.Invoke(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static (string NodePath, string CliPath) ResolvePiRuntime()
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

                var cliPath = Path.Combine(directory, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js");
                if (!File.Exists(cliPath))
                {
                    continue;
                }

                var adjacentNode = Path.Combine(directory, "node.exe");
                return (File.Exists(adjacentNode) ? adjacentNode : "node.exe", cliPath);
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        throw new FileNotFoundException("Pi was not found on PATH. Install @earendil-works/pi-coding-agent and restart PiDesk.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _writeLock.Dispose();
    }
}
