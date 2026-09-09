using VenueOS.Modules.Operations.Macro;

namespace VenueOS.Services.Tests;

/// <summary><see cref="MacroDirectiveParser"/> and <see cref="MacroReferenceScanner"/>: recognizing the two
/// VenueOS-only directives (MACRO spec §11/§17), leaving every ordinary line untouched (spec §9/§44), and the
/// exact-parsed-reference rename/scan behavior (spec §16).</summary>
public sealed class MacroDirectiveParserTests
{
    [Theory]
    [InlineData("/venueos macro \"Craft HQ Widget\"", "Craft HQ Widget")]
    [InlineData("  /venueos macro \"Spaced\"  ", "Spaced")]
    [InlineData("/VENUEOS MACRO \"Case Insensitive\"", "Case Insensitive")]
    public void Recognizes_nested_macro_invocation(string line, string expectedName)
    {
        var directive = MacroDirectiveParser.Parse(line);
        Assert.Equal(MacroDirectiveKind.NestedMacro, directive.Kind);
        Assert.Equal(expectedName, directive.MacroName);
    }

    [Theory]
    [InlineData("/actionready")]
    [InlineData("  /actionready  ")]
    [InlineData("/ActionReady")]
    public void Recognizes_actionready_directive(string line)
    {
        Assert.Equal(MacroDirectiveKind.ActionReady, MacroDirectiveParser.Parse(line).Kind);
    }

    [Theory]
    [InlineData("/ac \"Basic Synthesis\"")]
    [InlineData("/gearset change 8")]
    [InlineData("/say Hello")]
    [InlineData("/em waves.")]
    [InlineData("/target <t>")]
    [InlineData("some /venueos macro \"Not At Line Start\" trailing text")]
    [InlineData("/actionready now")]
    public void Unknown_or_embedded_text_is_a_plain_line(string line)
    {
        Assert.Equal(MacroDirectiveKind.PlainLine, MacroDirectiveParser.Parse(line).Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Whitespace_only_line_is_blank(string line)
    {
        Assert.Equal(MacroDirectiveKind.Blank, MacroDirectiveParser.Parse(line).Kind);
    }

    [Fact]
    public void FormatNestedInvocation_round_trips_through_Parse()
    {
        var line = MacroDirectiveParser.FormatNestedInvocation("Craft HQ Widget");
        var directive = MacroDirectiveParser.Parse(line);
        Assert.Equal(MacroDirectiveKind.NestedMacro, directive.Kind);
        Assert.Equal("Craft HQ Widget", directive.MacroName);
    }

    [Fact]
    public void FindReferencing_locates_macros_with_an_exact_nested_reference()
    {
        var a = new SavedMacro(Guid.NewGuid(), "A", 0, 1, ["/venueos macro \"Target\""]);
        var b = new SavedMacro(Guid.NewGuid(), "B", 0, 1, ["/say Target is mentioned here but not invoked"]);
        var c = new SavedMacro(Guid.NewGuid(), "C", 0, 1, ["/venueos macro \"Other\""]);

        var referencing = MacroReferenceScanner.FindReferencing([a, b, c], "Target");
        Assert.Equal([a], referencing);
    }

    [Fact]
    public void RewriteReferences_only_touches_exact_directive_lines_not_ordinary_text()
    {
        IReadOnlyList<string> lines = ["/venueos macro \"Old Name\"", "/say Old Name is just a mention here", "/venueos macro \"Unrelated\""];
        var rewritten = MacroReferenceScanner.RewriteReferences(lines, "Old Name", "New Name");

        Assert.Equal("/venueos macro \"New Name\"", rewritten[0]);
        Assert.Equal("/say Old Name is just a mention here", rewritten[1]); // untouched — not a parsed reference
        Assert.Equal("/venueos macro \"Unrelated\"", rewritten[2]); // untouched — references a different macro
    }

    [Fact]
    public void RewriteReferences_is_case_insensitive_on_the_old_name()
    {
        IReadOnlyList<string> lines = ["/venueos macro \"old name\""];
        var rewritten = MacroReferenceScanner.RewriteReferences(lines, "Old Name", "New Name");
        Assert.Equal("/venueos macro \"New Name\"", rewritten[0]);
    }
}
