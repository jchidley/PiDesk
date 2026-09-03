using CommunityToolkit.Mvvm.ComponentModel;
using PiDesk.Services;

namespace PiDesk.Models;

public enum MessageDeliveryState
{
    Accepted,
    Pending,
    Failed,
}

public partial class ChatMessage : ObservableObject
{
    public ChatMessage(
        string role,
        string text,
        string glyph,
        bool isActivity = false,
        string? correlationId = null,
        MessageDeliveryState deliveryState = MessageDeliveryState.Accepted,
        string? activityKey = null,
        string? arguments = null,
        string? state = null,
        bool isExpandable = false)
    {
        Role = role;
        Text = text;
        Glyph = glyph;
        IsActivity = isActivity;
        CorrelationId = correlationId;
        DeliveryState = deliveryState;
        ActivityKey = activityKey;
        Arguments = arguments ?? string.Empty;
        State = state ?? string.Empty;
        IsExpandable = isExpandable;
    }

    public string Role { get; protected set; }
    public string Glyph { get; protected set; }
    public bool IsActivity { get; protected set; }
    public string? CorrelationId { get; protected set; }
    public string? ActivityKey { get; }
    public bool IsExpandable { get; protected set; }
    public bool HasArguments => !string.IsNullOrWhiteSpace(Arguments);
    public string Summary => Summarize(
        !string.IsNullOrWhiteSpace(Text) ? Text : !string.IsNullOrWhiteSpace(Arguments) ? Arguments : State,
        240);
    public string AutomationName
    {
        get
        {
            var state = string.IsNullOrWhiteSpace(State) ? DeliveryStatus : State;
            var parts = new[] { Role, state, Summarize(Text, 160) }
                .Where(part => !string.IsNullOrWhiteSpace(part));
            return string.Join(", ", parts);
        }
    }

    public string DeliveryStatus => DeliveryState switch
    {
        MessageDeliveryState.Pending => "Sending…",
        MessageDeliveryState.Failed => "Not sent · restored to composer",
        _ => string.Empty,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string Text { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArguments))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string Arguments { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string State { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeliveryStatus))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial MessageDeliveryState DeliveryState { get; set; }

    public virtual void Update(PiActivityItem activity)
    {
        Role = activity.Title;
        Text = activity.Text;
        Arguments = activity.ArgumentsJson ?? string.Empty;
        State = ActivityMessageFactory.StateText(activity.State);
        IsExpandable = activity.IsExpandable;
        OnPropertyChanged(nameof(Role));
        OnPropertyChanged(nameof(IsExpandable));
        OnPropertyChanged(nameof(AutomationName));
    }

    private static string Summarize(string value, int maximumLength)
    {
        var summary = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return summary.Length <= maximumLength ? summary : string.Concat(summary.AsSpan(0, maximumLength - 1), "…");
    }
}

public sealed class UserTextMessage : ChatMessage
{
    public UserTextMessage(string text, string? key = null, MessageDeliveryState delivery = MessageDeliveryState.Accepted)
        : base("You", text, "\uE77B", deliveryState: delivery, activityKey: key) { }
}

public sealed class AssistantTextMessage : ChatMessage
{
    public AssistantTextMessage(string text, string? key = null)
        : base("Pi", text, "\uE8BD", activityKey: key) { }
}

public sealed class ThinkingMessage : ChatMessage
{
    public ThinkingMessage(string text, string key)
        : base("Thinking", text, "\uE90F", true, activityKey: key, isExpandable: true) { }
}

public sealed class ToolActivityMessage : ChatMessage
{
    public ToolActivityMessage(PiActivityItem activity)
        : base(activity.ToolName ?? activity.Title, activity.Text, "\uE756", true, activity.Key,
            activityKey: activity.Key, arguments: activity.ArgumentsJson,
            state: ActivityMessageFactory.StateText(activity.State), isExpandable: true) { }
}

public sealed class DiffMessage : ChatMessage
{
    public DiffMessage(PiActivityItem activity)
        : base($"{activity.ToolName ?? activity.Title} diff", activity.Diff?.Diff ?? string.Empty, "\uE8A5", true,
            activity.Key, activityKey: $"{activity.Key}:diff", arguments: activity.Diff?.Patch,
            state: ActivityMessageFactory.StateText(activity.State), isExpandable: true) { }

    public override void Update(PiActivityItem activity)
    {
        Text = activity.Diff?.Diff ?? string.Empty;
        Arguments = activity.Diff?.Patch ?? string.Empty;
        State = ActivityMessageFactory.StateText(activity.State);
    }
}

public sealed class RetryMessage : ChatMessage
{
    public RetryMessage(PiActivityItem activity)
        : base(activity.Title, activity.Text, "\uE72C", true, activityKey: activity.Key,
            state: ActivityMessageFactory.StateText(activity.State)) { }
}

public sealed class CompactionMessage : ChatMessage
{
    public CompactionMessage(PiActivityItem activity)
        : base(activity.Title, activity.Text, "\uE7C3", true, activityKey: activity.Key,
            state: ActivityMessageFactory.StateText(activity.State)) { }
}

public sealed class ErrorActivityMessage : ChatMessage
{
    public ErrorActivityMessage(PiActivityItem activity)
        : base(activity.Title, activity.Text, "\uEA39", true, activityKey: activity.Key,
            state: ActivityMessageFactory.StateText(activity.State)) { }
}

public static class ActivityMessageFactory
{
    public static IReadOnlyList<ChatMessage> Create(PiActivityItem activity)
    {
        ChatMessage primary = activity.Kind switch
        {
            PiActivityKind.UserText => new UserTextMessage(activity.Text, activity.Key),
            PiActivityKind.AssistantText => new AssistantTextMessage(activity.Text, activity.Key),
            PiActivityKind.Thinking => new ThinkingMessage(activity.Text, activity.Key),
            PiActivityKind.Tool => new ToolActivityMessage(activity),
            PiActivityKind.Retry => new RetryMessage(activity),
            PiActivityKind.Compaction => new CompactionMessage(activity),
            PiActivityKind.Error => new ErrorActivityMessage(activity),
            _ => new AssistantTextMessage(activity.Text, activity.Key),
        };
        primary.Update(activity);
        return activity.Diff is null ? [primary] : [primary, new DiffMessage(activity)];
    }

    public static string StateText(PiActivityState state) => state switch
    {
        PiActivityState.Pending => "Pending",
        PiActivityState.Streaming => "Streaming",
        PiActivityState.Running => "Running",
        PiActivityState.Completed => "Completed",
        PiActivityState.Failed => "Failed",
        _ => string.Empty,
    };
}
