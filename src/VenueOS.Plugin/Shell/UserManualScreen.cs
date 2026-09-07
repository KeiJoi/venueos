using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>The built-in, offline User Manual reader — a small, deliberately non-CommonMark Markdown renderer over
/// whatever <see cref="ManualMarkdown"/> parsed from the bundled <c>docs/USER_MANUAL.md</c> copy (see
/// <see cref="UserManualLoader"/>). Reached from Home's "User Manual" tile, rendered as an ordinary embedded VenueOS
/// page (same <see cref="AppFrame"/> chrome every module/Settings page uses) — no detached window, no external
/// browser, no network. A missing/empty manual renders a plain warning instead of crashing VenueOS.
///
/// Table cells and list items reuse the exact same inline-run word-wrap renderer paragraphs use — one rendering
/// path for every piece of inline content, rather than a separate implementation per block kind. External links
/// (anything not a same-document "#anchor") have no safe way to open a browser anywhere in this codebase today, so
/// activating one copies the URL to the clipboard instead of launching anything — the same "Copy" convention already
/// used elsewhere in VenueOS (ShoutRunner's Copy Terminal, Party Finder's Copy Link).</summary>
internal sealed class UserManualScreen
{
    private readonly bool loaded;
    private readonly IReadOnlyList<MarkdownBlock> blocks;
    private readonly List<(string Title, int BlockIndex)> toc = [];
    private readonly Dictionary<string, int> slugToBlockIndex = new(StringComparer.OrdinalIgnoreCase);

    private int? pendingScrollBlockIndex;
    private string search = "";
    private int lastFoundBlockIndex = -1;
    private string? searchStatus;
    private string? copiedLinkStatus;
    private double copiedLinkStatusUntil;

    public UserManualScreen(string? markdown)
    {
        loaded = markdown is not null;
        blocks = ManualMarkdown.Parse(markdown);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Kind != MarkdownBlockKind.Heading) continue;
            slugToBlockIndex.TryAdd(ManualMarkdown.Slugify(block.PlainText), i);
            if (block.HeadingLevel == 2) toc.Add((block.PlainText, i));
        }
    }

    public void Draw(VenueTheme theme)
    {
        if (!loaded || blocks.Count == 0) { UiKit.WarningState(theme, "User Manual could not be loaded."); return; }

        DrawToolbar(theme);
        ImGui.Spacing();

        var avail = ImGui.GetContentRegionAvail();
        const float sidebarWidth = 220f;
        ImGui.BeginChild("manual-toc", new Vector2(sidebarWidth, avail.Y), true);
        foreach (var (title, index) in toc)
            if (ImGui.Selectable($"{title}##manual-toc-{index}")) pendingScrollBlockIndex = index;
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("manual-content", new Vector2(0, avail.Y), false);
        for (var i = 0; i < blocks.Count; i++)
        {
            if (pendingScrollBlockIndex == i) { ImGui.SetScrollHereY(0f); pendingScrollBlockIndex = null; }
            DrawBlock(theme, blocks[i]);
        }
        ImGui.EndChild();
    }

    private void DrawToolbar(VenueTheme theme)
    {
        var width = ImGui.GetContentRegionAvail().X;
        Forms.SearchBox(theme, "manual-search", ref search, "Find in manual...", MathF.Max(120, width - 116));
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Find Next", new Vector2(100, 0))) FindNext();

        if (searchStatus is not null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
            ImGui.TextUnformatted(searchStatus);
            ImGui.PopStyleColor();
        }
        else if (copiedLinkStatus is not null && ImGui.GetTime() < copiedLinkStatusUntil)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.Success));
            ImGui.TextUnformatted(copiedLinkStatus);
            ImGui.PopStyleColor();
        }
    }

    private void FindNext()
    {
        if (string.IsNullOrWhiteSpace(search)) { searchStatus = null; return; }
        for (var offset = 1; offset <= blocks.Count; offset++)
        {
            var index = (lastFoundBlockIndex + offset) % blocks.Count;
            if (!BlockContainsText(blocks[index], search)) continue;
            lastFoundBlockIndex = index;
            pendingScrollBlockIndex = index;
            searchStatus = null;
            return;
        }
        searchStatus = "No matches.";
    }

    private static bool BlockContainsText(MarkdownBlock block, string needle)
    {
        bool RunsContain(IReadOnlyList<MarkdownInlineRun> runs) => runs.Any(r => r.Text.Contains(needle, StringComparison.OrdinalIgnoreCase));
        if (block.Lines is not null && block.Lines.Any(RunsContain)) return true;
        if (block.CodeLines is not null && block.CodeLines.Any(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase))) return true;
        if (block.TableHeader is not null && block.TableHeader.Any(RunsContain)) return true;
        if (block.TableRows is not null && block.TableRows.Any(row => row.Any(RunsContain))) return true;
        return false;
    }

    private void DrawBlock(VenueTheme theme, MarkdownBlock block)
    {
        switch (block.Kind)
        {
            case MarkdownBlockKind.Heading:
                ImGui.Spacing();
                UiKit.SectionHeader(theme, block.PlainText);
                ImGui.Spacing();
                break;
            case MarkdownBlockKind.Paragraph:
                DrawRuns(theme, block.Lines![0]);
                ImGui.Spacing();
                break;
            case MarkdownBlockKind.UnorderedList:
                foreach (var item in block.Lines!) { ImGui.Bullet(); ImGui.SameLine(); DrawRuns(theme, item); }
                ImGui.Spacing();
                break;
            case MarkdownBlockKind.OrderedList:
                for (var n = 0; n < block.Lines!.Count; n++) { ImGui.TextUnformatted($"{n + 1}."); ImGui.SameLine(); DrawRuns(theme, block.Lines[n]); }
                ImGui.Spacing();
                break;
            case MarkdownBlockKind.CodeBlock:
                DrawCodeBlock(theme, block.CodeLines!);
                break;
            case MarkdownBlockKind.HorizontalRule:
                UiKit.Divider(theme);
                break;
            case MarkdownBlockKind.Table:
                DrawTable(theme, block);
                break;
            case MarkdownBlockKind.Blockquote:
                DrawBlockquote(theme, block.Lines![0]);
                break;
        }
    }

    private static void DrawCodeBlock(VenueTheme theme, IReadOnlyList<string> lines)
    {
        var height = lines.Count * ImGui.GetTextLineHeightWithSpacing() + theme.Metrics.Padding * 2;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UiKit.Color(theme.Tokens.RaisedSurface));
        ImGui.BeginChild($"manual-code-{lines.GetHashCode()}", new Vector2(0, height), false);
        foreach (var line in lines) ImGui.TextUnformatted(line);
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    private void DrawBlockquote(VenueTheme theme, IReadOnlyList<MarkdownInlineRun> runs)
    {
        var barStart = ImGui.GetCursorScreenPos();
        ImGui.Indent(14);
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        DrawRuns(theme, runs);
        ImGui.PopStyleColor();
        ImGui.Unindent(14);
        var barEnd = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(barStart, new Vector2(barStart.X, barEnd.Y), UiKit.ColorU32(theme.Tokens.Accent), 2f);
        ImGui.Spacing();
    }

    private void DrawTable(VenueTheme theme, MarkdownBlock block)
    {
        var header = block.TableHeader!;
        var rows = block.TableRows!;
        var columnCount = header.Count;
        if (columnCount == 0) return;
        var totalWidth = ImGui.GetContentRegionAvail().X;
        var columnWidth = totalWidth / columnCount;
        var startX = ImGui.GetCursorPosX();

        DrawTableRow(theme, header, startX, columnWidth, columnCount, bold: true);
        UiKit.Divider(theme);
        foreach (var row in rows)
        {
            DrawTableRow(theme, row, startX, columnWidth, columnCount, bold: false);
            UiKit.Divider(theme);
        }
        ImGui.Spacing();
    }

    private void DrawTableRow(VenueTheme theme, IReadOnlyList<IReadOnlyList<MarkdownInlineRun>> cells, float startX, float columnWidth, int columnCount, bool bold)
    {
        var rowStartY = ImGui.GetCursorPosY();
        var maxHeight = 0f;
        for (var col = 0; col < columnCount && col < cells.Count; col++)
        {
            ImGui.SetCursorPos(new Vector2(startX + col * columnWidth, rowStartY));
            var runs = bold ? cells[col].Select(r => r with { Bold = true }).ToArray() : cells[col];
            DrawRuns(theme, runs, columnWidth - 8);
            maxHeight = MathF.Max(maxHeight, ImGui.GetCursorPosY() - rowStartY);
        }
        ImGui.SetCursorPos(new Vector2(startX, rowStartY + maxHeight));
    }

    /// <summary>The one inline-content word-wrap renderer every block kind (paragraphs, list items, table cells,
    /// blockquotes) funnels through. Manually flows word-by-word rather than relying on <c>ImGui.TextWrapped</c>
    /// because a single wrapped line here can mix plain/bold/code/link runs, which ImGui's own text wrapping has no
    /// concept of.</summary>
    private void DrawRuns(VenueTheme theme, IReadOnlyList<MarkdownInlineRun> runs, float? maxWidth = null)
    {
        var avail = maxWidth ?? ImGui.GetContentRegionAvail().X;
        var startX = ImGui.GetCursorPosX();
        var spaceWidth = ImGui.CalcTextSize(" ").X;
        var firstOnLine = true;
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            foreach (var word in run.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var size = ImGui.CalcTextSize(word);
                if (!firstOnLine)
                {
                    if (ImGui.GetCursorPosX() + spaceWidth + size.X > startX + avail) { ImGui.NewLine(); ImGui.SetCursorPosX(startX); firstOnLine = true; }
                    else ImGui.SameLine(0, spaceWidth);
                }
                DrawWord(theme, word, run);
                firstOnLine = false;
            }
        }
        ImGui.NewLine();
    }

    private void DrawWord(VenueTheme theme, string word, MarkdownInlineRun run)
    {
        var pos = ImGui.GetCursorScreenPos();
        var size = ImGui.CalcTextSize(word);
        var drawList = ImGui.GetWindowDrawList();
        var color = run.LinkUrl is not null ? theme.Tokens.Accent : theme.Tokens.TextPrimary;
        var colorU32 = UiKit.ColorU32(color);

        if (run.Code) drawList.AddRectFilled(pos - new Vector2(3, 1), pos + size + new Vector2(3, 1), UiKit.ColorU32(theme.Tokens.RaisedSurface), 3);
        // ImGui has no bold font variant loaded here, so bold is faked by drawing the glyphs twice with a sub-pixel
        // horizontal offset — a common, cheap ImGui technique for a heavier-looking weight without a second font.
        if (run.Bold) drawList.AddText(pos + new Vector2(0.6f, 0), colorU32, word);
        drawList.AddText(pos, colorU32, word);
        if (run.LinkUrl is not null) drawList.AddLine(new Vector2(pos.X, pos.Y + size.Y), new Vector2(pos.X + size.X, pos.Y + size.Y), colorU32);

        ImGui.Dummy(size);
        if (run.LinkUrl is not null)
        {
            if (ImGui.IsItemClicked()) ActivateLink(run.LinkUrl);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(run.LinkUrl.StartsWith('#') ? "Jump to section" : $"Copy link: {run.LinkUrl}");
        }
    }

    /// <summary>A same-document "#anchor" link scrolls to that heading. Anything else (an external https:// URL, or
    /// a relative link to a sibling doc that isn't part of this single bundled file) has no safe way to open a
    /// browser anywhere in this codebase, so it's copied to the clipboard instead — the operator can paste it
    /// wherever they like. This is a deliberate scope limit, not an oversight.</summary>
    private void ActivateLink(string url)
    {
        if (url.StartsWith('#') && slugToBlockIndex.TryGetValue(url[1..], out var index)) { pendingScrollBlockIndex = index; return; }
        ImGui.SetClipboardText(url);
        copiedLinkStatus = $"Copied link: {url}";
        copiedLinkStatusUntil = ImGui.GetTime() + 2.0;
    }
}
