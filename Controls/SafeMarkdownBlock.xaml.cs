using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace PiDesk.Controls;

public sealed partial class SafeMarkdownBlock : UserControl
{
    private static readonly Regex InlinePattern = new(
        @"(!?\[[^\]]*\]\([^\)]*\)|\*\*[^*]+\*\*|`[^`]+`)", RegexOptions.Compiled);

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SafeMarkdownBlock),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty ContentAutomationIdProperty = DependencyProperty.Register(
        nameof(ContentAutomationId), typeof(string), typeof(SafeMarkdownBlock),
        new PropertyMetadata(string.Empty, OnContentAutomationIdChanged));

    public SafeMarkdownBlock()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string ContentAutomationId
    {
        get => (string)GetValue(ContentAutomationIdProperty);
        set => SetValue(ContentAutomationIdProperty, value);
    }

    private static void OnContentAutomationIdChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (SafeMarkdownBlock)sender;
        AutomationProperties.SetAutomationId(control.ContentBlock, args.NewValue as string ?? string.Empty);
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((SafeMarkdownBlock)sender).Render(args.NewValue as string ?? string.Empty);
    }

    private void Render(string markdown)
    {
        ContentBlock.Blocks.Clear();
        var inCodeBlock = false;
        foreach (var sourceLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (sourceLine.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            var paragraph = new Paragraph();
            var line = sourceLine;
            if (inCodeBlock)
            {
                paragraph.FontFamily = new FontFamily("Cascadia Mono");
                paragraph.Inlines.Add(new Run { Text = line.Length == 0 ? " " : line });
            }
            else
            {
                var headingLength = line.TakeWhile(character => character == '#').Count();
                if (headingLength is > 0 and <= 6 && line.Length > headingLength && line[headingLength] == ' ')
                {
                    paragraph.FontWeight = FontWeights.SemiBold;
                    line = line[(headingLength + 1)..];
                }
                else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                {
                    line = $"• {line[2..]}";
                }
                AddInlineMarkdown(paragraph, line);
            }
            ContentBlock.Blocks.Add(paragraph);
        }
    }

    private static void AddInlineMarkdown(Paragraph paragraph, string line)
    {
        var position = 0;
        foreach (Match match in InlinePattern.Matches(line))
        {
            if (match.Index > position)
            {
                paragraph.Inlines.Add(new Run { Text = line[position..match.Index] });
            }

            var token = match.Value;
            if (token.StartsWith("**", StringComparison.Ordinal))
            {
                var bold = new Bold();
                bold.Inlines.Add(new Run { Text = token[2..^2] });
                paragraph.Inlines.Add(bold);
            }
            else if (token.StartsWith('`'))
            {
                var code = new Span { FontFamily = new FontFamily("Cascadia Mono") };
                code.Inlines.Add(new Run { Text = token[1..^1] });
                paragraph.Inlines.Add(code);
            }
            else
            {
                paragraph.Inlines.Add(new Run { Text = SafeLinkText(token) });
            }
            position = match.Index + match.Length;
        }

        if (position < line.Length)
        {
            paragraph.Inlines.Add(new Run { Text = line[position..] });
        }
    }

    private static string SafeLinkText(string token)
    {
        var image = token.StartsWith("![", StringComparison.Ordinal);
        var labelStart = image ? 2 : 1;
        var labelEnd = token.IndexOf(']');
        var targetStart = token.IndexOf('(', labelEnd) + 1;
        var label = token[labelStart..labelEnd];
        var target = token[targetStart..^1];
        return image ? $"[Image: {label}]" : $"{label} ({target})";
    }
}
