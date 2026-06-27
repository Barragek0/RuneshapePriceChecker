using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace RuneshapePriceChecker.App.Dashboard;

internal static partial class MarkdownRenderer
{
    private static int _spoilerId;
    private static readonly SolidColorBrush TextBrush = new(Color.FromRgb(0xE8, 0xEC, 0xF1));
    private static readonly SolidColorBrush SecondaryBrush = new(Color.FromRgb(0x8A, 0x8F, 0x98));
    private static readonly SolidColorBrush CodeBgBrush = new(Color.FromRgb(0x2A, 0x2E, 0x38));
    private static readonly SolidColorBrush LinkBrush = new(Color.FromRgb(0x4A, 0x9E, 0xFF));

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldPattern();

    [GeneratedRegex(@"\*(.+?)\*")]
    private static partial Regex ItalicPattern();

    [GeneratedRegex(@"~~(.+?)~~")]
    private static partial Regex StrikethroughPattern();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodePattern();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"https?://[^\s)]+")]
    private static partial Regex BareUrlPattern();

    [GeneratedRegex(@"^\*\*Full Changelog\*\*:\s+(https?://\S+)")]
    private static partial Regex FullChangelogPattern();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex CollapseBlankLines();

    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = TextBrush,
            PagePadding = new Thickness(0, 0, 32, 0),
            TextAlignment = TextAlignment.Left,
            IsOptimalParagraphEnabled = true,
            LineHeight = 20
        };

        try
        {
            doc.Resources.Add(typeof(Hyperlink), new Style(typeof(Hyperlink))
            {
                Setters =
                {
                    new Setter(TextElement.ForegroundProperty, LinkBrush),
                    new Setter(Inline.TextDecorationsProperty, null)
                },
                Triggers =
                {
                    new Trigger { Property = ContentElement.IsMouseOverProperty, Value = true,
                        Setters = { new Setter(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x7A, 0xBE, 0xFF))) } }
                }
            });
        }
        catch (InvalidOperationException)
        {
            // WPF resources may not be available in test/headless contexts; continue without style.
        }

        var raw = markdown
            .Replace("\r\n", "\n").Replace('\r', '\n');
        raw = CollapseBlankLines().Replace(raw, "\n\n");
        var lines = raw.Split('\n');
        var inCodeBlock = false;
        var inDetails = false;
        var detailsSummary = "";
        List<string> detailsLines = [];
        List<string> codeLines = [];

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.TrimStart().StartsWith("<details>", StringComparison.OrdinalIgnoreCase))
            {
                inDetails = true;
                detailsSummary = "Details";
                detailsLines.Clear();
                continue;
            }

            if (inDetails && line.TrimStart().StartsWith("<summary>", StringComparison.OrdinalIgnoreCase))
            {
                var endIdx = line.IndexOf("</summary>", StringComparison.OrdinalIgnoreCase);
                detailsSummary = endIdx > 10 ? line[9..endIdx].Trim() : line[9..].Trim();
                continue;
            }

            if (inDetails && line.TrimStart().StartsWith("</details>", StringComparison.OrdinalIgnoreCase))
            {
                inDetails = false;
                FlushDetailsBlock(doc, detailsSummary, [.. detailsLines]);
                detailsLines.Clear();
                continue;
            }

            if (inDetails)
            {
                detailsLines.Add(line);
                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeBlock)
                {
                    FlushCodeBlock(doc, codeLines);
                    codeLines.Clear();
                }
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock)
            {
                codeLines.Add(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 2, 0, 2) });
                continue;
            }

            if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("***", StringComparison.Ordinal) || line.StartsWith("___", StringComparison.Ordinal))
            {
                doc.Blocks.Add(new Paragraph(new Run("─".PadRight(40, '─')) { Foreground = SecondaryBrush, FontSize = 10 })
                { Margin = new Thickness(0, 4, 0, 4) });
                continue;
            }

            var headingMatch = HeadingPattern().Match(line);
            if (headingMatch.Success)
            {
                var level = headingMatch.Value.TrimEnd().Length;
                var text = line[headingMatch.Length..].Trim();
                var fontSize = level switch { 1 => 20.0, 2 => 17.0, 3 => 15.0, _ => 14.0 };
                doc.Blocks.Add(new Paragraph(new Run(text) { FontSize = fontSize, FontWeight = FontWeights.Bold, Foreground = TextBrush })
                { Margin = new Thickness(0, level == 1 ? 8 : 4, 0, 4) });
                continue;
            }

            var trimmedLine = line.TrimStart();
            var starMatch = trimmedLine.StartsWith("- ", StringComparison.Ordinal) || trimmedLine.StartsWith("* ", StringComparison.Ordinal) || trimmedLine.StartsWith("+ ", StringComparison.Ordinal) ||
                            trimmedLine.StartsWith("• ", StringComparison.Ordinal);
            if (starMatch)
            {
                var text = trimmedLine[2..].Trim();
                doc.Blocks.Add(new Paragraph
                {
                    Margin = new Thickness(0, 1, 0, 1),
                    Inlines = { new Run("•  ") { Foreground = TextBrush }, RenderInline(text) }
                });
                continue;
            }

            var numMatch = Regex.Match(line, @"^(\d+)\.\s+", RegexOptions.CultureInvariant);
            if (numMatch.Success)
            {
                var num = numMatch.Groups[1].Value;
                var text = line[numMatch.Length..].Trim();
                doc.Blocks.Add(new Paragraph
                {
                    Margin = new Thickness(0, 1, 0, 1),
                    Inlines = { new Run($"{num}.  ") { Foreground = TextBrush }, RenderInline(text) }
                });
                continue;
            }

            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                var text = line[2..].Trim();
                var p = new Paragraph
                {
                    Margin = new Thickness(0, 1, 0, 1),
                    BorderBrush = SecondaryBrush,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(8, 0, 0, 0)
                };
                p.Inlines.Add(RenderInline(text));
                doc.Blocks.Add(p);
                continue;
            }

            if (line.StartsWith('|') && line.EndsWith('|') && line.Contains("---"))
            {
                continue; // skip table separator rows
            }

            if (line.StartsWith('|') && line.EndsWith('|'))
            {
                var tableLines = new List<string> { line };
                while (i + 1 < lines.Length && lines[i + 1].StartsWith('|') && lines[i + 1].EndsWith('|'))
                {
                    i++;
                    if (lines[i].Contains("---")) continue; // skip separators within table
                    tableLines.Add(lines[i]);
                }
                FlushTable(doc, tableLines);
                continue;
            }
            var changelogMatch = FullChangelogPattern().Match(line);
            if (changelogMatch.Success)
            {
                var url = changelogMatch.Groups[1].Value;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var linkUri))
                {
                    doc.Blocks.Add(new Paragraph(new Run(line) { Foreground = TextBrush })
                    { Margin = new Thickness(0, 1, 0, 1) });
                    continue;
                }
                var link = new Hyperlink(new Run("Full Changelog") { FontWeight = FontWeights.Bold, Foreground = LinkBrush })
                {
                    NavigateUri = linkUri,
                    ToolTip = url
                };
                link.RequestNavigate += (_, e) => OpenUrl(e.Uri.ToString());
                var p = new Paragraph(link) { Margin = new Thickness(0, 1, 0, 1) };
                doc.Blocks.Add(p);
                continue;
            }
            doc.Blocks.Add(new Paragraph(RenderInline(line)) { Margin = new Thickness(0, 1, 0, 1) });
        }

        if (inCodeBlock && codeLines.Count > 0)
            FlushCodeBlock(doc, codeLines);

        return doc;
    }

    private static void FlushDetailsBlock(FlowDocument doc, string summary, List<string> lines)
    {
        var id = Interlocked.Increment(ref _spoilerId);
        var tag = $"spoiler_{id}";

        var header = new Paragraph
        {
            Margin = new Thickness(0, 8, 0, 2),
            Tag = tag
        };
        var headerLink = new Hyperlink(new Run("▶ " + summary) { FontWeight = FontWeights.Bold, FontSize = 14, Foreground = LinkBrush })
        {
            Tag = tag
        };
        headerLink.RequestNavigate += (_, _) => { }; // prevent navigation
        headerLink.Click += (_, _) =>
        {
            ToggleSpoilerBlocks(doc, header, tag, lines);
        };
        header.Inlines.Add(headerLink);
        doc.Blocks.Add(header);
    }

    private static void ToggleSpoilerBlocks(FlowDocument doc, Paragraph header, string tag, List<string> lines)
    {
        var expanded = false;
        List<Block> afterBlocks = [];

        var pastHeader = false;
        foreach (Block block in doc.Blocks.ToList())
        {
            if (block == header) { pastHeader = true; continue; }
            if (pastHeader && block.Tag is string t && t == tag)
            {
                _ = doc.Blocks.Remove(block);
                expanded = true;
            }
            else if (pastHeader && !expanded)
            {
                afterBlocks.Add(block);
                _ = doc.Blocks.Remove(block);
            }
        }

        if (expanded)
        {
            if (header.Inlines.FirstInline is Hyperlink link && link.Inlines.FirstInline is Run run)
                run.Text = "▶ " + run.Text[2..];
            foreach (var b in afterBlocks) doc.Blocks.Add(b);
            return;
        }

        if (header.Inlines.FirstInline is Hyperlink link2 && link2.Inlines.FirstInline is Run run2)
            run2.Text = "▼ " + run2.Text[2..];

        var tableLines = new List<string>();
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith('|'))
            {
                tableLines.Add(line);
                continue;
            }
            if (tableLines.Count > 0)
            {
                FlushTable(doc, tableLines, tag);
                tableLines.Clear();
            }

            Paragraph? p;
            if (string.IsNullOrWhiteSpace(line)) p = new Paragraph { Margin = new Thickness(16, 2, 0, 2) };
            else
            {
                var trimmed = line.TrimStart();
                var headingMatch = HeadingPattern().Match(line);
                if (headingMatch.Success)
                {
                    var level = headingMatch.Value.TrimEnd().Length;
                    var text = line[headingMatch.Length..].Trim();
                    var fontSize = level switch { 1 => 18.0, 2 => 16.0, 3 => 14.0, _ => 13.0 };
                    p = new Paragraph(new Run(text) { FontSize = fontSize, FontWeight = FontWeights.Bold, Foreground = TextBrush })
                    { Margin = new Thickness(16, 6, 0, 2) };
                }
                else if (trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("+ ", StringComparison.Ordinal) || trimmed.StartsWith("• ", StringComparison.Ordinal))
                {
                    var text = trimmed[2..].Trim();
                    p = new Paragraph { Margin = new Thickness(24, 1, 0, 1), Inlines = { new Run("• ") { Foreground = TextBrush }, RenderInline(text) } };
                }
                else
                {
                    p = new Paragraph(RenderInline(line)) { Margin = new Thickness(16, 1, 0, 1) };
                }
            }
            p.Tag = tag;
            doc.Blocks.Add(p);
        }
        if (tableLines.Count > 0)
        {
            FlushTable(doc, tableLines, tag);
        }
        var spacer = new Paragraph { Margin = new Thickness(0, 6, 0, 0), Tag = tag };
        doc.Blocks.Add(spacer);

        foreach (var b in afterBlocks) doc.Blocks.Add(b);
    }

    private static void FlushTable(FlowDocument doc, List<string> tableLines, string? tag = null)
    {
        var rows = tableLines
            .Select(l => l.Split('|').Skip(1).Select(c => c.Trim()).ToArray())
            .Where(r => r.Length > 0 && r.Any(c => c.Length > 0))
            .ToList();
        if (rows.Count == 0) return;

        var colCount = rows.Max(r => r.Length);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(4, 2, 4, 4), Tag = tag! };
        for (var c = 0; c < colCount; c++)
            table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

        var rowGroup = new TableRowGroup();
        for (var ri = 0; ri < rows.Count; ri++)
        {
            var row = rows[ri];
            var isHeader = ri == 0;
            var tableRow = new TableRow();
            for (var c = 0; c < colCount; c++)
            {
                var cellText = c < row.Length ? row[c] : "";
                var cellPara = new Paragraph
                {
                    Margin = new Thickness(6, 2, 6, 2)
                };
                cellPara.Inlines.Add(isHeader
                    ? new Run(cellText) { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextBrush }
                    : RenderInline(cellText));
                var cell = new TableCell(cellPara)
                {
                    Background = isHeader ? CodeBgBrush : null,
                    Padding = new Thickness(0)
                };
                tableRow.Cells.Add(cell);
            }
            rowGroup.Rows.Add(tableRow);
        }
        table.RowGroups.Add(rowGroup);
        doc.Blocks.Add(table);
    }

    private static void FlushCodeBlock(FlowDocument doc, List<string> lines)
    {
        var p = new Paragraph
        {
            Background = CodeBgBrush,
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(8, 4, 8, 4),
            FontFamily = new FontFamily("Consolas, Courier New"),
            FontSize = 12
        };
        p.Inlines.Add(new Run(string.Join("\n", lines)) { Foreground = TextBrush });
        doc.Blocks.Add(p);
    }

    private static Span RenderInline(string text)
    {
        var span = new Span();
        var remaining = text;

        while (remaining.Length > 0)
        {
            var linkMatch = LinkPattern().Match(remaining);
            var bareUrlMatch = BareUrlPattern().Match(remaining);
            var boldMatch = BoldPattern().Match(remaining);
            var italicMatch = ItalicPattern().Match(remaining);
            var strikeMatch = StrikethroughPattern().Match(remaining);
            var codeMatch = InlineCodePattern().Match(remaining);

            var earliest = int.MaxValue;
            Match? earliestMatch = null;
            foreach (var m in new[] { linkMatch, bareUrlMatch, boldMatch, italicMatch, strikeMatch, codeMatch })
            {
                if (m.Success && m.Index < earliest) { earliest = m.Index; earliestMatch = m; }
            }

            if (earliestMatch is null)
            {
                span.Inlines.Add(new Run(remaining) { Foreground = TextBrush });
                break;
            }

            if (earliest > 0)
                span.Inlines.Add(new Run(remaining[..earliest]) { Foreground = TextBrush });

            if (earliestMatch == linkMatch)
            {
                var linkedText = linkMatch.Groups[1].Value;
                var url = linkMatch.Groups[2].Value;
                if (Uri.TryCreate(url, UriKind.Absolute, out var linkUri))
                {
                    var link = new Hyperlink(new Run(linkedText) { Foreground = LinkBrush })
                    {
                        NavigateUri = linkUri,
                        ToolTip = url
                    };
                    link.RequestNavigate += (_, e) => OpenUrl(e.Uri.ToString());
                    span.Inlines.Add(link);
                }
                else
                {
                    span.Inlines.Add(new Run($"[{linkedText}]({url})") { Foreground = TextBrush });
                }
            }
            else if (earliestMatch == bareUrlMatch)
            {
                var url = bareUrlMatch.Value;
                if (Uri.TryCreate(url, UriKind.Absolute, out var linkUri))
                {
                    var link = new Hyperlink(new Run(url) { Foreground = LinkBrush })
                    {
                        NavigateUri = linkUri,
                        ToolTip = url
                    };
                    link.RequestNavigate += (_, e) => OpenUrl(e.Uri.ToString());
                    span.Inlines.Add(link);
                }
                else
                {
                    span.Inlines.Add(new Run(url) { Foreground = TextBrush });
                }
            }
            else if (earliestMatch == boldMatch)
                span.Inlines.Add(new Run(boldMatch.Groups[1].Value) { FontWeight = FontWeights.Bold, Foreground = TextBrush });
            else if (earliestMatch == italicMatch)
                span.Inlines.Add(new Run(italicMatch.Groups[1].Value) { FontStyle = FontStyles.Italic, Foreground = TextBrush });
            else if (earliestMatch == strikeMatch)
                span.Inlines.Add(new Run(strikeMatch.Groups[1].Value) { TextDecorations = TextDecorations.Strikethrough, Foreground = SecondaryBrush });
            else if (earliestMatch == codeMatch)
                span.Inlines.Add(new Run(codeMatch.Groups[1].Value) { FontFamily = new FontFamily("Consolas, Courier New"), Background = CodeBgBrush, Foreground = TextBrush });

            remaining = remaining[(earliest + earliestMatch.Length)..];
        }

        return span;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
