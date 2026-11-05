using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Rondel.Windows.UI;

/// <summary>Colors and actions of an answer's document (see <see cref="MarkdownView"/>).</summary>
/// <param name="CopyLabel">The label of a code block's copy button.</param>
/// <param name="ButtonStyle">The style of that button (the card's).</param>
/// <param name="Copy">Copies a code block's text.</param>
/// <param name="OpenLink">Opens a link; only http and https links are offered.</param>
internal sealed record MarkdownLook(Brush Text, Brush SubText, Brush Link, Brush CodeBackground, Brush Rule, string CopyLabel, Style? ButtonStyle,
    Action<string> Copy, Action<Uri> OpenLink);

/// <summary>
/// Shows the Markdown an agent answers in, as a <see cref="FlowDocument"/>: paragraphs, headings, lists, quotes, tables,
/// code blocks (each with a copy button) and, inline, emphasis, strikethrough, code and links. Anything else stays text.
/// An answer still being written may end inside a code block: the rest is code until it is closed.
/// </summary>
internal static partial class MarkdownView
{
    const double BaseSize = 13;

    public static FlowDocument Build(string markdown, MarkdownLook look)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(4, 0, 4, 0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = BaseSize,
            Foreground = look.Text,
            TextAlignment = TextAlignment.Left,
        };
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var text = new List<string>();
        void Flush()
        {
            if (text.Count > 0)
                document.Blocks.Add(Paragraph(text, look));
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
                document.Blocks.Add(CodeBlock(string.Join('\n', code), fence.Groups["language"].Value, look));
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
            }
            else if (Heading().Match(line) is { Success: true } heading)
            {
                Flush();
                int level = heading.Groups["level"].Length;
                var paragraph = new Paragraph { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6) };
                paragraph.FontSize = level switch { 1 => BaseSize + 3, 2 => BaseSize + 2, 3 => BaseSize + 1, _ => BaseSize };
                paragraph.Inlines.AddRange(Inlines(heading.Groups["text"].Value, look));
                document.Blocks.Add(paragraph);
            }
            else if (Rule().IsMatch(line))
            {
                Flush();
                document.Blocks.Add(new BlockUIContainer(new Border { Height = 1, Background = look.Rule, Margin = new Thickness(0, 4, 0, 10) }));
            }
            else if (line.TrimStart().StartsWith('>'))
            {
                Flush();
                var quoted = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++)
                    quoted.Add(QuoteMark().Replace(lines[i], ""));
                i--;
                var section = new Section
                {
                    BorderBrush = look.Rule,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 8),
                    Foreground = look.SubText,
                };
                section.Blocks.Add(Paragraph(quoted, look, bottom: 0));
                document.Blocks.Add(section);
            }
            else if (ListItem().IsMatch(line))
            {
                Flush();
                int start = i;
                for (; i < lines.Length && (ListItem().IsMatch(lines[i]) || (i > start && lines[i].StartsWith("  ", StringComparison.Ordinal)
                    && lines[i].Trim().Length > 0)); i++)
                {
                }
                document.Blocks.Add(List(lines[start..i], look));
                i--;
            }
            else if (line.TrimStart().StartsWith('|') && i + 1 < lines.Length && TableRule().IsMatch(lines[i + 1]))
            {
                Flush();
                var rows = new List<string> { line };
                for (i += 2; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                    rows.Add(lines[i]);
                i--;
                document.Blocks.Add(Table(rows, look));
            }
            else
            {
                text.Add(line);
            }
        }
        Flush();
        return document;
    }

    /// <summary>Lines of one paragraph; a line break in the answer stays one.</summary>
    static Paragraph Paragraph(IReadOnlyList<string> lines, MarkdownLook look, double bottom = 8)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, bottom) };
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.AddRange(Inlines(lines[i].Trim(), look));
        }
        return paragraph;
    }

    /// <summary>
    /// A code block on a quiet background: a header with its language and a copy button, then the code, selectable and
    /// wrapped (a scroll bar of its own would be one more thing to drag on a small card).
    /// </summary>
    static BlockUIContainer CodeBlock(string code, string language, MarkdownLook look)
    {
        var copy = new Button
        {
            Content = look.CopyLabel,
            Style = look.ButtonStyle,
            FontSize = 11,
            Padding = new Thickness(9, 1, 9, 2),
            IsTabStop = false,
        };
        copy.Click += (_, _) => look.Copy(code);
        var header = new DockPanel { Margin = new Thickness(10, 6, 6, 0) };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        header.Children.Add(new TextBlock { Text = language, FontSize = 11, Foreground = look.SubText, VerticalAlignment = VerticalAlignment.Center });
        var box = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            IsTabStop = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = look.Text,
            CaretBrush = look.Text,
            SelectionBrush = look.Link,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(7, 2, 7, 9),
        };
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(box);
        return new BlockUIContainer(new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = look.CodeBackground,
            BorderBrush = look.Rule,
            BorderThickness = new Thickness(1),
            Child = panel,
        })
        { Margin = new Thickness(0, 2, 0, 10) };
    }

    /// <summary>Consecutive list items, nested by their indent, numbered when the first one is.</summary>
    static List List(IReadOnlyList<string> lines, MarkdownLook look)
    {
        var first = ListItem().Match(lines[0]);
        int indent = first.Groups["indent"].Length;
        bool ordered = char.IsAsciiDigit(first.Groups["marker"].Value[0]);
        var list = new List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(20, 0, 0, 0),
        };
        if (ordered && int.TryParse(first.Groups["marker"].Value.TrimEnd('.', ')'), out int number) && number > 1)
            list.StartIndex = number;

        for (int i = 0; i < lines.Count; i++)
        {
            var item = ListItem().Match(lines[i]);
            if (!item.Success || item.Groups["indent"].Length > indent)
            {
                // A deeper item or a continued line belongs to the item before it.
                int end = i;
                while (end < lines.Count && (ListItem().Match(lines[end]) is not { Success: true } next || next.Groups["indent"].Length > indent))
                    end++;
                if (list.ListItems.LastListItem is { } last)
                {
                    var nested = lines.Skip(i).Take(end - i).ToList();
                    if (ListItem().IsMatch(nested[0]))
                        last.Blocks.Add(List(nested, look));
                    else
                        last.Blocks.Add(Paragraph(nested, look, bottom: 2));
                }
                i = end - 1;
                continue;
            }
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
            paragraph.Inlines.AddRange(Inlines(item.Groups["text"].Value, look));
            list.ListItems.Add(new ListItem(paragraph));
        }
        return list;
    }

    static Table Table(IReadOnlyList<string> rows, MarkdownLook look)
    {
        var cells = rows.Select(Cells).ToList();
        int columns = cells.Max(c => c.Count);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 10) };
        for (int c = 0; c < columns; c++)
            table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        for (int r = 0; r < cells.Count; r++)
        {
            var row = new TableRow();
            if (r == 0)
                row.FontWeight = FontWeights.SemiBold;
            for (int c = 0; c < columns; c++)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                if (c < cells[r].Count)
                    paragraph.Inlines.AddRange(Inlines(cells[r][c], look));
                row.Cells.Add(new TableCell(paragraph)
                {
                    Padding = new Thickness(0, 3, 10, 4),
                    BorderBrush = look.Rule,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
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
            {
                inlines.Add(new Run(parts[i])
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = BaseSize - 1,
                    Background = look.CodeBackground,
                });
            }
            else
            {
                inlines.AddRange(Emphasis(i % 2 == 1 ? "`" + parts[i] : parts[i], look));
            }
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
                {
                    var link = new Hyperlink(new Run(label)) { Foreground = look.Link, NavigateUri = uri, ToolTip = uri.ToString() };
                    link.Click += (_, _) => look.OpenLink(uri);
                    yield return link;
                }
                else
                {
                    yield return new Run(label); // only web links are offered
                }
            }
        }
        if (at < text.Length)
            yield return new Run(Unescape(text[at..]));
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
