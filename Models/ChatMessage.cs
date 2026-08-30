using CommunityToolkit.Mvvm.ComponentModel;

namespace PiDesk.Models;

public partial class ChatMessage : ObservableObject
{
    public ChatMessage(string role, string text, string glyph, bool isActivity = false, string? correlationId = null)
    {
        Role = role;
        Text = text;
        Glyph = glyph;
        IsActivity = isActivity;
        CorrelationId = correlationId;
    }

    public string Role { get; }
    public string Glyph { get; }
    public bool IsActivity { get; }
    public string? CorrelationId { get; }

    [ObservableProperty]
    public partial string Text { get; set; }
}
