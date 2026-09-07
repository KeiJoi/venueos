namespace VenueOS.Services;

/// <summary>One inline styled run of text inside a paragraph/list-item/table-cell/blockquote — the smallest unit the
/// renderer draws. <see cref="LinkUrl"/> non-null means this run is a Markdown link; <see cref="Bold"/>/<see cref="Code"/>
/// are independent flags (a run is never both bold and a link target's visible text needing separate bold handling —
/// bold-inside-a-link is simply a run with both flags set).</summary>
public sealed record MarkdownInlineRun(string Text, bool Bold = false, bool Code = false, string? LinkUrl = null);

public enum MarkdownBlockKind { Heading, Paragraph, UnorderedList, OrderedList, CodeBlock, HorizontalRule, Table, Blockquote }

/// <summary>One parsed block-level element. Only the fields relevant to <see cref="Kind"/> are populated:
/// <see cref="HeadingLevel"/> for Heading only; <see cref="Lines"/> holds one entry per paragraph/blockquote (always
/// exactly one) or per list item (one per item) — each entry is that line's sequence of inline runs;
/// <see cref="CodeLines"/> holds a fenced code block's raw, unparsed lines; <see cref="TableHeader"/>/<see cref="TableRows"/>
/// hold a table's cells, each cell pre-parsed into inline runs the same way a paragraph is.</summary>
public sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    int HeadingLevel = 0,
    IReadOnlyList<IReadOnlyList<MarkdownInlineRun>>? Lines = null,
    IReadOnlyList<string>? CodeLines = null,
    IReadOnlyList<IReadOnlyList<MarkdownInlineRun>>? TableHeader = null,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownInlineRun>>>? TableRows = null)
{
    /// <summary>Plain concatenated text of every run in this block's first line — used for the table-of-contents
    /// label (Heading blocks) and for a simple case-insensitive substring search across the document.</summary>
    public string PlainText => Lines is { Count: > 0 } ? string.Concat(Lines[0].Select(r => r.Text)) : "";
}

/// <summary>A small, deliberately scoped Markdown parser — it supports exactly the constructs
/// <c>docs/USER_MANUAL.md</c> actually uses (headings, paragraphs, bold, inline code, fenced code blocks, unordered
/// and ordered lists, horizontal rules, tables, links, and a single-line blockquote) and nothing beyond that. It is
/// not a CommonMark implementation: no nested lists, no italics (the manual never uses them), no images, no reference
/// -style links, no HTML passthrough. If the manual grows to need one of those, extend this parser deliberately
/// rather than reaching for a general-purpose Markdown library — the manual is the only consumer.</summary>
public static class ManualMarkdown
{
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<MarkdownBlock>();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) { code.Add(lines[i]); i++; }
                if (i < lines.Length) i++; // consume closing fence
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.CodeBlock, CodeLines: code));
                continue;
            }

            var headingMatch = System.Text.RegularExpressions.Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (headingMatch.Success)
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading, HeadingLevel: headingMatch.Groups[1].Value.Length, Lines: [ParseInline(headingMatch.Groups[2].Value.TrimEnd())]));
                i++;
                continue;
            }

            if (line.Trim() is "---" or "***" or "___")
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.HorizontalRule));
                i++;
                continue;
            }

            if (line.TrimStart().StartsWith("> ", StringComparison.Ordinal) || line.Trim() == ">")
            {
                var quoted = new List<string>();
                while (i < lines.Length && (lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal)))
                {
                    var stripped = lines[i].TrimStart();
                    stripped = stripped.Length > 1 ? stripped[1..].TrimStart() : "";
                    quoted.Add(stripped);
                    i++;
                }
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Blockquote, Lines: [ParseInline(string.Join(' ', quoted))]));
                continue;
            }

            if (line.TrimStart().StartsWith("|", StringComparison.Ordinal))
            {
                var tableLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal)) { tableLines.Add(lines[i]); i++; }
                if (tableLines.Count >= 2)
                {
                    var header = SplitTableRow(tableLines[0]).Select(ParseInline).ToArray();
                    var rows = tableLines.Skip(2).Select(row => (IReadOnlyList<IReadOnlyList<MarkdownInlineRun>>)SplitTableRow(row).Select(ParseInline).ToArray()).ToArray();
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Table, TableHeader: header, TableRows: rows));
                    continue;
                }
                // Only one "|" line with no separator row - treat as an ordinary paragraph instead of a broken table.
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, Lines: [ParseInline(tableLines[0].Trim())]));
                continue;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^[-*]\s+"))
            {
                var items = new List<IReadOnlyList<MarkdownInlineRun>>();
                while (i < lines.Length && System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"^[-*]\s+"))
                {
                    items.Add(ParseInline(System.Text.RegularExpressions.Regex.Replace(lines[i], @"^[-*]\s+", "")));
                    i++;
                }
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.UnorderedList, Lines: items));
                continue;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\.\s+"))
            {
                var items = new List<IReadOnlyList<MarkdownInlineRun>>();
                while (i < lines.Length && System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"^\d+\.\s+"))
                {
                    items.Add(ParseInline(System.Text.RegularExpressions.Regex.Replace(lines[i], @"^\d+\.\s+", "")));
                    i++;
                }
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.OrderedList, Lines: items));
                continue;
            }

            // Plain paragraph: consume consecutive non-blank lines that don't start a different block, joining
            // wrapped source lines with a single space (the renderer re-wraps to the actual window width anyway).
            var paragraph = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !IsBlockStart(lines[i])) { paragraph.Add(lines[i].Trim()); i++; }
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, Lines: [ParseInline(string.Join(' ', paragraph))]));
        }
        return blocks;
    }

    private static bool IsBlockStart(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal) ||
        System.Text.RegularExpressions.Regex.IsMatch(line, @"^#{1,6}\s+") ||
        line.Trim() is "---" or "***" or "___" ||
        line.TrimStart().StartsWith(">", StringComparison.Ordinal) ||
        line.TrimStart().StartsWith("|", StringComparison.Ordinal) ||
        System.Text.RegularExpressions.Regex.IsMatch(line, @"^[-*]\s+") ||
        System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\.\s+");

    private static string[] SplitTableRow(string row)
    {
        var trimmed = row.Trim();
        if (trimmed.StartsWith("|", StringComparison.Ordinal)) trimmed = trimmed[1..];
        if (trimmed.EndsWith("|", StringComparison.Ordinal)) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(c => c.Trim()).ToArray();
    }

    /// <summary>Parses one line of text into inline runs: <c>**bold**</c>, `` `code` ``, and <c>[text](url)</c> links.
    /// Plain runs of text between those markers pass through unstyled. No italics, no nesting (a link's own text is
    /// never itself re-scanned for bold/code) - the manual never needs either.</summary>
    public static IReadOnlyList<MarkdownInlineRun> ParseInline(string text)
    {
        var runs = new List<MarkdownInlineRun>();
        var pattern = new System.Text.RegularExpressions.Regex(@"\*\*(?<bold>[^*]+)\*\*|`(?<code>[^`]+)`|\[(?<linktext>[^\]]+)\]\((?<linkurl>[^)]+)\)");
        var pos = 0;
        foreach (System.Text.RegularExpressions.Match m in pattern.Matches(text))
        {
            if (m.Index > pos) runs.Add(new MarkdownInlineRun(text[pos..m.Index]));
            if (m.Groups["bold"].Success) runs.Add(new MarkdownInlineRun(m.Groups["bold"].Value, Bold: true));
            else if (m.Groups["code"].Success) runs.Add(new MarkdownInlineRun(m.Groups["code"].Value, Code: true));
            else if (m.Groups["linktext"].Success) runs.Add(new MarkdownInlineRun(m.Groups["linktext"].Value, LinkUrl: m.Groups["linkurl"].Value));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) runs.Add(new MarkdownInlineRun(text[pos..]));
        return runs.Count == 0 ? [new MarkdownInlineRun("")] : runs;
    }

    /// <summary>Reproduces GitHub's heading-anchor algorithm (lowercase; strip anything that isn't a letter, digit,
    /// hyphen, or space; turn every remaining space into a hyphen) closely enough to resolve the <c>#anchor</c> links
    /// docs/USER_MANUAL.md's own table of contents already uses.</summary>
    public static string Slugify(string headingText)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in headingText.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == ' ') sb.Append(ch);
        return sb.ToString().Trim().Replace(' ', '-');
    }
}
