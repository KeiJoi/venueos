using VenueOS.Services;

namespace VenueOS.Services.Tests;

public sealed class ManualMarkdownTests
{
    [Fact]
    public void Null_or_whitespace_input_parses_to_no_blocks()
    {
        Assert.Empty(ManualMarkdown.Parse(null));
        Assert.Empty(ManualMarkdown.Parse(""));
        Assert.Empty(ManualMarkdown.Parse("   \n  \n"));
    }

    [Fact]
    public void Heading_levels_are_parsed_and_text_is_captured()
    {
        var blocks = ManualMarkdown.Parse("# Title\n## Section\n### Sub");
        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(MarkdownBlockKind.Heading, b.Kind));
        Assert.Equal([1, 2, 3], blocks.Select(b => b.HeadingLevel));
        Assert.Equal(["Title", "Section", "Sub"], blocks.Select(b => b.PlainText));
    }

    [Fact]
    public void Paragraph_lines_are_joined_with_a_single_space()
    {
        var blocks = ManualMarkdown.Parse("This is one\nparagraph wrapped\nacross lines.");
        var block = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
        Assert.Equal("This is one paragraph wrapped across lines.", block.PlainText);
    }

    [Fact]
    public void Blank_line_separates_two_paragraphs()
    {
        var blocks = ManualMarkdown.Parse("First paragraph.\n\nSecond paragraph.");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("First paragraph.", blocks[0].PlainText);
        Assert.Equal("Second paragraph.", blocks[1].PlainText);
    }

    [Fact]
    public void Bold_inline_code_and_links_are_parsed_as_distinct_runs()
    {
        var runs = ManualMarkdown.ParseInline("Plain **bold** and `code` and [a link](https://example.com/x) end.");
        Assert.Equal("Plain ", runs[0].Text);
        Assert.False(runs[0].Bold);
        Assert.Equal("bold", runs[1].Text); Assert.True(runs[1].Bold);
        Assert.Equal(" and ", runs[2].Text);
        Assert.Equal("code", runs[3].Text); Assert.True(runs[3].Code);
        Assert.Equal(" and ", runs[4].Text);
        Assert.Equal("a link", runs[5].Text); Assert.Equal("https://example.com/x", runs[5].LinkUrl);
        Assert.Equal(" end.", runs[6].Text);
    }

    [Fact]
    public void Unordered_and_ordered_lists_produce_one_item_per_line()
    {
        var unordered = ManualMarkdown.Parse("- one\n- two\n- three");
        var block = Assert.Single(unordered);
        Assert.Equal(MarkdownBlockKind.UnorderedList, block.Kind);
        Assert.Equal(3, block.Lines!.Count);
        Assert.Equal("two", string.Concat(block.Lines[1].Select(r => r.Text)));

        var ordered = ManualMarkdown.Parse("1. first\n2. second");
        var orderedBlock = Assert.Single(ordered);
        Assert.Equal(MarkdownBlockKind.OrderedList, orderedBlock.Kind);
        Assert.Equal(2, orderedBlock.Lines!.Count);
    }

    [Fact]
    public void Horizontal_rule_is_its_own_block()
    {
        var blocks = ManualMarkdown.Parse("Above.\n\n---\n\nBelow.");
        Assert.Equal(3, blocks.Count);
        Assert.Equal(MarkdownBlockKind.HorizontalRule, blocks[1].Kind);
    }

    [Fact]
    public void Fenced_code_block_captures_raw_unparsed_lines()
    {
        var blocks = ManualMarkdown.Parse("```\nline one\n**not bold**\n```");
        var block = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.CodeBlock, block.Kind);
        Assert.Equal(["line one", "**not bold**"], block.CodeLines);
    }

    [Fact]
    public void Table_splits_header_and_rows_and_skips_the_separator_line()
    {
        var blocks = ManualMarkdown.Parse("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |");
        var block = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Table, block.Kind);
        Assert.Equal(["A", "B"], block.TableHeader!.Select(c => string.Concat(c.Select(r => r.Text))));
        Assert.Equal(2, block.TableRows!.Count);
        Assert.Equal(["1", "2"], block.TableRows[0].Select(c => string.Concat(c.Select(r => r.Text))));
    }

    [Fact]
    public void Blockquote_strips_the_leading_marker()
    {
        var blocks = ManualMarkdown.Parse("> **Note:** something important.");
        var block = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Blockquote, block.Kind);
        Assert.Equal("Note: something important.", block.PlainText);
    }

    [Theory]
    [InlineData("Installation", "installation")]
    [InlineData("1. Installation", "1-installation")]
    [InlineData("Data / Privacy / Credential Notes", "data--privacy--credential-notes")]
    [InlineData("Under Development Modules", "under-development-modules")]
    public void Slugify_matches_githubs_heading_anchor_algorithm(string heading, string expectedSlug)
    {
        Assert.Equal(expectedSlug, ManualMarkdown.Slugify(heading));
    }

    [Fact]
    public void The_real_bundled_manual_parses_without_error_and_covers_every_construct_it_uses()
    {
        var blocks = ManualMarkdown.Parse(File.ReadAllText(ManualTestPaths.FindRepoManualPath()));
        Assert.NotEmpty(blocks);
        // The manual is known to use every one of these constructs (see the release-pass audit that scoped this
        // parser) - if a future edit removes the last instance of one, that's fine; this only guards against the
        // parser itself silently failing to recognize a construct that's actually present today.
        Assert.Contains(blocks, b => b.Kind == MarkdownBlockKind.Heading && b.HeadingLevel == 1);
        Assert.Contains(blocks, b => b.Kind == MarkdownBlockKind.Heading && b.HeadingLevel == 2);
        Assert.Contains(blocks, b => b.Kind == MarkdownBlockKind.Heading && b.HeadingLevel == 3);
        Assert.Contains(blocks, b => b.Kind == MarkdownBlockKind.Paragraph);
        Assert.Contains(blocks, b => b.Kind == MarkdownBlockKind.UnorderedList);
        Assert.Contains(blocks, b => b.Kind == MarkdownBlockKind.OrderedList);
        Assert.Contains(blocks, b => b.Kind == MarkdownBlockKind.HorizontalRule);
        Assert.Contains(blocks, b => b.Kind == MarkdownBlockKind.Table);
        Assert.Contains(blocks, b => b.Kind == MarkdownBlockKind.CodeBlock);
        Assert.Contains(blocks, b => b.Kind == MarkdownBlockKind.Blockquote);
        Assert.Contains(blocks, b => (b.Lines ?? []).Any(line => line.Any(r => r.Bold)));
        Assert.Contains(blocks, b => (b.Lines ?? []).Any(line => line.Any(r => r.Code)));
        Assert.Contains(blocks, b => (b.Lines ?? []).Any(line => line.Any(r => r.LinkUrl is not null)));
    }
}

/// <summary>Shared helper for locating repository files (like docs/USER_MANUAL.md) from a test binary's output
/// directory, which sits several levels below the repository root.</summary>
internal static class ManualTestPaths
{
    public static string FindRepoManualPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "USER_MANUAL.md");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate docs/USER_MANUAL.md by walking up from the test output directory.");
    }
}
