using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace PiDesk.Services;

internal sealed class RpcResponseRouter
{
    private readonly ConcurrentDictionary<string, PendingResponse> _pending = new();

    internal int PendingCount => _pending.Count;

    public Task<JsonElement> Register(string id, long generation)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, new PendingResponse(generation, completion)))
        {
            throw new InvalidOperationException($"RPC request '{id}' is already registered.");
        }

        return completion.Task;
    }

    public bool TryRoute(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("type", out var type) || type.GetString() != "response" ||
            !message.TryGetProperty("id", out var idValue) || idValue.GetString() is not { } id ||
            !_pending.TryGetValue(id, out var pending))
        {
            return false;
        }

        pending.Completion.TrySetResult(message);
        return true;
    }

    public void Remove(string id) => _pending.TryRemove(id, out _);

    public void FailGeneration(long generation, Exception exception)
    {
        foreach (var item in _pending)
        {
            if (item.Value.Generation == generation && _pending.TryRemove(item.Key, out var pending))
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private sealed record PendingResponse(long Generation, TaskCompletionSource<JsonElement> Completion);
}

internal static class RpcRecordParser
{
    public static bool TryParse(string record, out JsonElement message, out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(record);
            message = document.RootElement.Clone();
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            message = default;
            error = ex.Message;
            return false;
        }
    }
}

internal static class StrictJsonlReader
{
    internal const int MaximumRecordCharacters = 16 * 1024 * 1024;

    public static async Task ReadAsync(
        TextReader reader,
        Func<string, Task> recordReceived,
        CancellationToken cancellationToken = default,
        int maximumRecordCharacters = MaximumRecordCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRecordCharacters);
        var buffer = new char[4096];
        var pending = new StringBuilder(Math.Min(buffer.Length, maximumRecordCharacters));

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                break;
            }

            var start = 0;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != '\n')
                {
                    continue;
                }

                AppendChecked(pending, buffer, start, index - start, maximumRecordCharacters);
                await recordReceived(StripTrailingCarriageReturn(pending.ToString()));
                pending.Clear();
                start = index + 1;
            }

            AppendChecked(pending, buffer, start, count - start, maximumRecordCharacters);
        }

        if (pending.Length > 0)
        {
            await recordReceived(StripTrailingCarriageReturn(pending.ToString()));
        }
    }

    private static void AppendChecked(StringBuilder pending, char[] buffer, int start, int count, int maximum)
    {
        if (pending.Length > maximum - count)
        {
            throw new InvalidDataException($"RPC JSONL record exceeded the {maximum}-character limit.");
        }
        pending.Append(buffer, start, count);
    }

    private static string StripTrailingCarriageReturn(string value) =>
        value.EndsWith('\r') ? value[..^1] : value;
}
