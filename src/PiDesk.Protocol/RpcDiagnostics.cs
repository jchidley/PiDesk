namespace PiDesk.Services;

public enum RpcDiagnosticKind
{
    Command,
    MalformedRecord,
    UnknownEvent,
    ObserverFailure,
    StderrFailure,
}

public sealed record RpcDiagnostic(
    DateTimeOffset Timestamp,
    RpcDiagnosticKind Kind,
    long ProcessGeneration,
    string Message,
    string? CommandType = null,
    string? CorrelationId = null);

internal sealed class RpcDiagnosticBuffer
{
    internal const int MaximumEntries = 200;
    internal const int MaximumMessageCharacters = 2048;
    private readonly object _lock = new();
    private readonly Queue<RpcDiagnostic> _entries = new();

    public RpcDiagnostic Add(
        RpcDiagnosticKind kind,
        long generation,
        string message,
        string? commandType = null,
        string? correlationId = null)
    {
        if (message.Length > MaximumMessageCharacters)
        {
            message = message[^MaximumMessageCharacters..];
        }

        var entry = new RpcDiagnostic(DateTimeOffset.UtcNow, kind, generation, message, commandType, correlationId);
        lock (_lock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaximumEntries)
            {
                _entries.Dequeue();
            }
        }
        return entry;
    }

    public IReadOnlyList<RpcDiagnostic> Snapshot()
    {
        lock (_lock)
        {
            return _entries.ToArray();
        }
    }
}
