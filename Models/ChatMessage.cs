using CommunityToolkit.Mvvm.ComponentModel;

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
        MessageDeliveryState deliveryState = MessageDeliveryState.Accepted)
    {
        Role = role;
        Text = text;
        Glyph = glyph;
        IsActivity = isActivity;
        CorrelationId = correlationId;
        DeliveryState = deliveryState;
    }

    public string Role { get; }
    public string Glyph { get; }
    public bool IsActivity { get; }
    public string? CorrelationId { get; }

    public string DeliveryStatus => DeliveryState switch
    {
        MessageDeliveryState.Pending => "Sending…",
        MessageDeliveryState.Failed => "Not sent · restored to composer",
        _ => string.Empty,
    };

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeliveryStatus))]
    public partial MessageDeliveryState DeliveryState { get; set; }
}
