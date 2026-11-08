using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Rondel.MacOS.UI;

/// <summary>Colors and actions of an answer's document (see <see cref="MarkdownView"/>).</summary>
/// <param name="CopyLabel">The label of a code block's copy button.</param>
/// <param name="CopyButton">Makes that button (the card's pill), given its label and what it does.</param>
/// <param name="Copy">Copies a code block's text.</param>
/// <param name="OpenLink">Opens a link; only http and https links are offered.</param>
internal sealed record MarkdownLook(IBrush Text, IBrush SubText, IBrush Link, IBrush CodeBackground, IBrush Rule, string CopyLabel,
    Func<string, Action, Control> CopyButton, Action<string> Copy, Action<Uri> OpenLink);

/// <summary>
/// Shows the Markdown an agent answers in, as on Windows: paragraphs, headings, lists, quotes, tables, code blocks (each
/// with a copy button) and, inline, emphasis, strikethrough, code and links. Anything else stays text. An answer still
/// being written may end inside a code block: the rest is code until it is closed. Text is selectable, one block at a time.
/// </summary>
internal static partial class MarkdownView
{
    const double BaseSize = 13;
    static readonly FontFamily Mono = new("Menlo");

    public static Control Build(string markdown, MarkdownLook look)
    {
        var document = new StackPanel();
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var text = new List<string>();
        void Flush()
        {
            if (text.Count > 0)
                document.Children.Add(Paragraph(text, look));
            text.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (Fence().Match(line) is { Success: true } fence)
            {
                Flush();
                string marker = fence.Groups["marker"].Value;
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(marker, StringComparison.Ordinal); i++)
                    code.Add(lines[i]);
                document.Children.Add(CodeBlock(string.Join('\n', code), fence.Groups["language"].Value, look));
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
            }
            else if (Heading().Match(line) is { Success: true } heading)
            {
                Flush();
                int level = heading.Groups["level"].Length;
                var block = Block(look, bottom: 6);
                block.Margin = new Thickness(0, 4, 0, 6);
                block.FontWeight = FontWeight.SemiBold;
                block.FontSize = level switch { 1 => BaseSize + 3, 2 => BaseSize + 2, 3 => BaseSize + 1, _ => BaseSize };
                block.Inlines!.AddRange(Inlines(heading.Groups["text"].Value, look));
                document.Children.Add(block);
            }
            else if (Rule().IsMatch(line))
            {
                Flush();
                document.Children.Add(new Border { Height = 1, Background = look.Rule, Margin = new Thickness(0, 4, 0, 10) });
            }
            else if (line.TrimStart().StartsWith('>'))
            {
                Flush();
                var quoted = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++)
                    quoted.Add(QuoteMark().Replace(lines[i], ""));
                i--;
                var paragraph = Paragraph(quoted, look, bottom: 0);
                paragraph.Foreground = look.SubText;
                document.Children.Add(new Border
                {
                    BorderBrush = look.Rule,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 8),
                    Child = paragraph,
                });
            }
            else if (ListItem().IsMatch(line))
            {
                Flush();
                int start = i;
                for (; i < lines.Length && (ListItem().IsMatch(lines[i]) || (i > start && lines[i].StartsWith("  ", StringComparison.Ordinal)
                    && lines[i].Trim().Length > 0)); i++)
                {
                }
                var list = List(lines[start..i], look);
                list.Margin = new Thickness(0, 0, 0, 8);
                document.Children.Add(list);
                i--;
            }
            else if (line.TrimStart().StartsWith('|') && i + 1 < lines.Length && TableRule().IsMatch(lines[i + 1]))
            {
                Flush();
                var rows = new List<string> { line };
                for (i += 2; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                    rows.Add(lines[i]);
                i--;
                document.Children.Add(Table(rows, look));
            }
            else
            {
                text.Add(line);
            }
        }
        Flush();
        return document;
    }

    static SelectableTextBlock Block(MarkdownLook look, double bottom) => new()
    {
        FontSize = BaseSize,
        Foreground = look.Text,
        SelectionBrush = look.Link,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, bottom),
        Inlines = [],
    };

    /// <summary>Lines of one paragraph; a line break in the answer stays one.</summary>
    static SelectableTextBlock Paragraph(IReadOnlyList<string> lines, MarkdownLook look, double bottom = 8)
    {
        var block = Block(look, bottom);
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                block.Inlines!.Add(new LineBreak());
            block.Inlines!.AddRange(Inlines(lines[i].Trim(), look));
        }
        return block;
    }

    /// <summary>
    /// A code block on a quiet background: a header with its language and a copy button, then the code, selectable and
    /// wrapped (a scroll bar of its own would be one more thing to drag on a small card).
    /// </summary>
    static Border CodeBlock(string code, string language, MarkdownLook look)
    {
        var copy = look.CopyButton(look.CopyLabel, () => look.Copy(code));
        DockPanel.SetDock(copy, Dock.Right);
        var header = new DockPanel
        {
            Margin = new Thickness(10, 6, 6, 0),
            Children =
            {
                copy,
                new TextBlock { Text = language, FontSize = 11, Foreground = look.SubText, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var body = new SelectableTextBlock
        {
            Text = code,
            FontFamily = Mono,
            FontSize = 11.5,
            Foreground = look.Text,
            SelectionBrush = look.Link,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 4, 10, 10),
        };
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = look.CodeBackground,
            BorderBrush = look.Rule,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 2, 0, 10),
            Child = new StackPanel { Children = { header, body } },
        };
    }

    /// <summary>Consecutive list items, nested by their indent, numbered when the first one is.</summary>
    static StackPanel List(IReadOnlyList<string> lines, MarkdownLook look)
    {
        var first = ListItem().Match(lines[0]);
        int indent = first.Groups["indent"].Length;
        bool ordered = char.IsAsciiDigit(first.Groups["marker"].Value[0]);
        int number = ordered && int.TryParse(first.Groups["marker"].Value.TrimEnd('.', ')'), out int start) ? start : 1;
        var list = new StackPanel();
        StackPanel? last = null;

        for (int i = 0; i < lines.Count; i++)
        {
            var item = ListItem().Match(lines[i]);
            if (!item.Success || item.Groups["indent"].Length > indent)
            {
                // A deeper item or a continued line belongs to the item before it.
                int end = i;
                while (end < lines.Count && (ListItem().Match(lines[end]) is not { Success: true } next || next.Groups["indent"].Length > indent))
                    end++;
                if (last is not null)
                {
                    var nested = lines.Skip(i).Take(end - i).ToList();
                    last.Children.Add(ListItem().IsMatch(nested[0]) ? List(nested, look) : Paragraph(nested, look, bottom: 2));
                }
                i = end - 1;
                continue;
            }
            var paragraph = Block(look, bottom: 3);
            paragraph.Inlines!.AddRange(Inlines(item.Groups["text"].Value, look));
            last = new StackPanel { Children = { paragraph } };
            var marker = new TextBlock
            {
                Text = ordered ? $"{number++}." : "•",
                FontSize = BaseSize,
                Foreground = look.SubText,
                Width = 20,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var row = new DockPanel { Children = { marker, last } };
            DockPanel.SetDock(marker, Dock.Left);
            list.Children.Add(row);
        }
        return list;
    }

    static Grid Table(IReadOnlyList<string> rows, MarkdownLook look)
    {
        var cells = rows.Select(Cells).ToList();
        int columns = cells.Max(c => c.Count);
        var table = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        for (int c = 0; c < columns; c++)
            table.ColumnDefinitions.Add(new ColumnDefinition(c == columns - 1 ? GridLength.Star : GridLength.Auto));
        for (int r = 0; r < cells.Count; r++)
        {
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < columns; c++)
            {
                var block = Block(look, bottom: 0);
                if (r == 0)
                    block.FontWeight = FontWeight.SemiBold;
                if (c < cells[r].Count)
                    block.Inlines!.AddRange(Inlines(cells[r][c], look));
                var cell = new Border
                {
                    Padding = new Thickness(0, 3, 10, 4),
                    BorderBrush = look.Rule,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = block,
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                table.Children.Add(cell);
            }
        }
        return table;
    }

    static List<string> Cells(string row) =>
        [.. row.Trim().Trim('|').Split('|').Select(cell => cell.Trim())];

    /// <summary>Inline code, emphasis, strikethrough and links in <paramref name="text"/>.</summary>
    static IEnumerable<Inline> Inlines(string text, MarkdownLook look)
    {
        var inlines = new List<Inline>();
        string[] parts = text.Split('`');
        for (int i = 0; i < parts.Length; i++)
        {
            // Odd parts sat between backticks; an unclosed one (still being written) stays text.
            if (i % 2 == 1 && i < parts.Length - 1)
                inlines.Add(new Run(parts[i]) { FontFamily = Mono, FontSize = BaseSize - 1, Background = look.CodeBackground });
            else
                inlines.AddRange(Emphasis(i % 2 == 1 ? "`" + parts[i] : parts[i], look));
        }
        return inlines;
    }

    static IEnumerable<Inline> Emphasis(string text, MarkdownLook look)
    {
        int at = 0;
        foreach (Match match in Inline().Matches(text))
        {
            if (match.Index > at)
                yield return new Run(Unescape(text[at..match.Index]));
            at = match.Index + match.Length;
            if (match.Groups["bold"].Success)
            {
                var bold = new Bold();
                bold.Inlines.AddRange(Emphasis(match.Groups["bold"].Value, look));
                yield return bold;
            }
            else if (match.Groups["italic"].Success || match.Groups["italic2"].Success)
            {
                var italic = new Italic();
                italic.Inlines.AddRange(Emphasis(match.Groups["italic"].Success ? match.Groups["italic"].Value : match.Groups["italic2"].Value, look));
                yield return italic;
            }
            else if (match.Groups["strike"].Success)
            {
                var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                span.Inlines.AddRange(Emphasis(match.Groups["strike"].Value, look));
                yield return span;
            }
            else
            {
                string label = match.Groups["label"].Success ? match.Groups["label"].Value : match.Groups["url"].Value;
                string target = match.Groups["label"].Success ? match.Groups["target"].Value : match.Groups["url"].Value;
                if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                    yield return Link(label, uri, look);
                else
                    yield return new Run(label); // only web links are offered
            }
        }
        if (at < text.Length)
            yield return new Run(Unescape(text[at..]));
    }

    /// <summary>A web link: underlined in the accent color, opened with a click; its address shows on hover.</summary>
    static InlineUIContainer Link(string label, Uri uri, MarkdownLook look)
    {
        var link = new TextBlock
        {
            Text = label,
            FontSize = BaseSize,
            Foreground = look.Link,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(link, uri.ToString());
        link.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            look.OpenLink(uri);
        };
        return new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom };
    }

    static string Unescape(string text) => Escaped().Replace(text, "$1");

    [GeneratedRegex(@"^\s*(?<marker>```|~~~)\s*(?<language>[\w+#.-]*)")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s{0,3}(?<level>#{1,6})\s+(?<text>.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\s*>\s?")]
    private static partial Regex QuoteMark();

    [GeneratedRegex(@"^(?<indent>\s*)(?<marker>[-*+]|\d{1,9}[.)])\s+(?<text>.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"(\*\*|__)(?<bold>.+?)\1|(?<![\w*\\])\*(?<italic>[^*\s](?:[^*]*?[^*\s])?)\*(?![\w*])|(?<![\w_\\])_(?<italic2>[^_\s](?:[^_]*?[^_\s])?)_(?![\w_])|~~(?<strike>.+?)~~|\[(?<label>[^\]]+)\]\((?<target>[^)\s]+)\)|(?<url>https?://[^\s<>()`]+[^\s<>()`.,;:!?'""])")]
    private static partial Regex Inline();

    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!|~>])")]
    private static partial Regex Escaped();
}
