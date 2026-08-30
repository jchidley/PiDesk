namespace PiDesk.Models;

public sealed record ModelOption(string Provider, string Id, string Name)
{
    public string DisplayName => $"{Name} · {Provider}";
}
