using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiDesk.Services;

public sealed class PiRpcClient : IAsyncDisposable
{
    internal const int MaximumStderrCharacters = 8192;
    private readonly RpcResponseRouter _responses = new();
    private readonly RpcDiagnosticBuffer _diagnostics = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Func<string, ProcessStartInfo> _startInfoFactory;
    private readonly TimeSpan _requestTimeout;
    private RunState? _run;
    private long _nextGeneration;

    public PiRpcClient() : this(workingDirectory => PiRuntimeResolver.Resolve().CreateStartInfo(workingDirectory), TimeSpan.FromSeconds(30))
    {
    }

    internal PiRpcClient(Func<string, ProcessStartInfo> startInfoFactory, TimeSpan requestTimeout)
    {
        _startInfoFactory = startInfoFactory;
        _requestTimeout = requestTimeout;
    }

    public event Func<JsonElement, Task>? EventReceived;
    public event Action<string>? ErrorReceived;
    public event Action<RpcDiagnostic>? DiagnosticReceived;
    public bool IsRunning => _run is { Process.HasExited: false };
    public IReadOnlyList<RpcDiagnostic> Diagnostics => _diagnostics.Snapshot();
    internal int PendingRequestCount => _responses.PendingCount;
    internal long CurrentGeneration => _run?.Generation ?? 0;

    public async Task StartAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var startInfo = _startInfoFactory(workingDirectory);
        if (!string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) && !Directory.Exists(startInfo.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Working folder does not exist: {startInfo.WorkingDirectory}");
        }

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Pi.");
            var run = new RunState(process, Interlocked.Increment(ref _nextGeneration));
            _run = run;
            run.OutputReader = ReadOutputAsync(run);
            run.ErrorReader = ReadErrorsAsync(run);

            await SendAsync(new JsonObject { ["type"] = "get_state" }, cancellationToken);
        }
        catch
        {
            await StopCoreAsync();
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task SendNotificationAsync(JsonObject command, CancellationToken cancellationToken = default)
    {
        var run = GetRunningRun();
        await WriteAsync(run, command, cancellationToken);
    }

    public async Task<JsonElement> SendAsync(JsonObject command, CancellationToken cancellationToken = default)
    {
        var run = GetRunningRun();
        var id = Guid.NewGuid().ToString("N");
        command["id"] = id;
        var responseTask = _responses.Register(id, run.Generation);

        try
        {
            var commandType = command["type"]?.GetValue<string>() ?? "unknown";
            NotifyDiagnostic(RpcDiagnosticKind.Command, run.Generation,
                $"Sending RPC command '{commandType}'.", commandType, id);
            await WriteAsync(run, command, cancellationToken);
            var response = await responseTask.WaitAsync(_requestTimeout, cancellationToken);
            if (!response.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
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
            _responses.Remove(id);
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        var run = _run;
        if (run is null)
        {
            return;
        }

        _run = null;
        run.IntentionalStop = true;
        _responses.FailGeneration(run.Generation, new InvalidOperationException("Pi stopped before the request completed."));

        try
        {
            run.Process.StandardInput.Close();
            var exitTask = run.Process.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(1500)) != exitTask && !run.Process.HasExited)
            {
                run.Process.Kill(entireProcessTree: true);
            }
            await exitTask;

            if (run.OutputReader is not null && run.ErrorReader is not null)
            {
                await Task.WhenAll(run.OutputReader, run.ErrorReader);
            }
        }
        finally
        {
            run.Process.Dispose();
        }
    }

    private RunState GetRunningRun()
    {
        var run = _run;
        if (run is null || run.Process.HasExited)
        {
            throw new InvalidOperationException("Pi is not running.");
        }

        return run;
    }

    private async Task WriteAsync(RunState run, JsonObject command, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(_run, run) || run.Process.HasExited)
            {
                throw new InvalidOperationException("The Pi session changed before the request could be sent.");
            }

            await run.Process.StandardInput.WriteLineAsync(command.ToJsonString());
            await run.Process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadOutputAsync(RunState run)
    {
        try
        {
            await StrictJsonlReader.ReadAsync(run.Process.StandardOutput, line => ProcessRecordAsync(run, line));
            _responses.FailGeneration(run.Generation, new InvalidOperationException("Pi exited before the request completed."));

            if (!run.IntentionalStop)
            {
                await run.Process.WaitForExitAsync();
                if (run.ErrorReader is not null)
                {
                    await run.ErrorReader;
                }
                ReportUnexpectedExit(run, $"Pi exited unexpectedly with code {run.Process.ExitCode}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _responses.FailGeneration(run.Generation, new InvalidOperationException("Pi output could not be read.", ex));
            if (!run.IntentionalStop)
            {
                await TerminateFaultedRunAsync(run);
                ReportUnexpectedExit(run, $"Could not read Pi output: {ex.Message}");
            }
        }
    }

    private async Task TerminateFaultedRunAsync(RunState run)
    {
        try
        {
            if (!run.Process.HasExited)
            {
                run.Process.Kill(entireProcessTree: true);
            }
            await run.Process.WaitForExitAsync();
            if (run.ErrorReader is not null)
            {
                await run.ErrorReader;
            }
        }
        catch (Exception ex)
        {
            NotifyDiagnostic(RpcDiagnosticKind.StderrFailure, run.Generation,
                $"Could not finish faulted Pi process shutdown: {ex.Message}");
        }
    }

    private async Task ProcessRecordAsync(RunState run, string line)
    {
        if (!ReferenceEquals(_run, run) || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (!RpcRecordParser.TryParse(line, out var message, out var parseError))
        {
            NotifyDiagnostic(RpcDiagnosticKind.MalformedRecord, run.Generation,
                $"Pi emitted malformed JSONL: {parseError}");
            return;
        }

        if (_responses.TryRoute(message))
        {
            return;
        }

        if (EventReceived is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Func<JsonElement, Task>>())
            {
                try
                {
                    await handler(message);
                }
                catch (Exception ex)
                {
                    NotifyError($"Could not handle a Pi event: {ex.Message}", run.Generation);
                }
            }
        }
        else
        {
            var eventType = message.TryGetProperty("type", out var type) ? type.GetString() : null;
            NotifyDiagnostic(RpcDiagnosticKind.UnknownEvent, run.Generation,
                $"Unhandled Pi event '{eventType ?? "unknown"}'.");
        }
    }

    private async Task ReadErrorsAsync(RunState run)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var count = await run.Process.StandardError.ReadAsync(buffer.AsMemory());
                if (count == 0)
                {
                    break;
                }
                run.AppendStderr(buffer.AsSpan(0, count));
            }
        }
        catch (Exception ex) when (!run.IntentionalStop)
        {
            NotifyDiagnostic(RpcDiagnosticKind.StderrFailure, run.Generation,
                $"Could not read Pi stderr: {ex.Message}");
        }
    }

    private void ReportUnexpectedExit(RunState run, string message)
    {
        if (run.IntentionalStop || Interlocked.Exchange(ref run.ExitReported, 1) != 0)
        {
            return;
        }

        var stderr = run.GetStderr();
        var context = string.IsNullOrWhiteSpace(stderr) ? string.Empty : $"{Environment.NewLine}{stderr}";
        NotifyError($"{message}{context}{Environment.NewLine}Choose the project folder again to reconnect.", run.Generation);
    }

    private void NotifyError(string message, long generation)
    {
        if (ErrorReceived is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(message);
            }
            catch (Exception ex)
            {
                _diagnostics.Add(RpcDiagnosticKind.ObserverFailure, generation,
                    $"An error observer failed: {ex.Message}");
            }
        }
    }

    private void NotifyDiagnostic(
        RpcDiagnosticKind kind,
        long generation,
        string message,
        string? commandType = null,
        string? correlationId = null)
    {
        var entry = _diagnostics.Add(kind, generation, message, commandType, correlationId);
        if (DiagnosticReceived is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<RpcDiagnostic>>())
        {
            try
            {
                handler(entry);
            }
            catch (Exception ex)
            {
                _diagnostics.Add(RpcDiagnosticKind.ObserverFailure, generation,
                    $"A diagnostic observer failed: {ex.Message}");
            }
        }
    }

    internal void RecordDiagnostic(RpcDiagnosticKind kind, long generation, string message) =>
        NotifyDiagnostic(kind, generation, message);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _writeLock.Dispose();
        _lifecycleLock.Dispose();
    }

    private sealed class RunState(Process process, long generation)
    {
        private readonly object _stderrLock = new();
        private readonly StringBuilder _stderr = new(MaximumStderrCharacters);

        public Process Process { get; } = process;
        public long Generation { get; } = generation;
        public bool IntentionalStop { get; set; }
        public int ExitReported;
        public Task? OutputReader { get; set; }
        public Task? ErrorReader { get; set; }

        public void AppendStderr(ReadOnlySpan<char> value)
        {
            lock (_stderrLock)
            {
                if (value.Length >= MaximumStderrCharacters)
                {
                    _stderr.Clear();
                    _stderr.Append(value[^MaximumStderrCharacters..]);
                    return;
                }

                var excess = _stderr.Length + value.Length - MaximumStderrCharacters;
                if (excess > 0)
                {
                    _stderr.Remove(0, excess);
                }
                _stderr.Append(value);
            }
        }

        public string GetStderr()
        {
            lock (_stderrLock)
            {
                return _stderr.ToString();
            }
        }
    }
}
