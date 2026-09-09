using System.Text.RegularExpressions;

namespace VenueOS.Modules.Operations.Macro;

/// <summary>What a single authored macro line resolves to before <c>MacroRunner</c> acts on it. MACRO spec §44:
/// exactly two VenueOS-only directives exist (nested-macro invocation, <c>/actionready</c>) — no loops, variables,
/// goto, labels, conditions, or expression language. Everything else, including every ordinary unrecognized slash
/// command, is a <see cref="PlainLine"/> sent to FFXIV byte-for-byte as authored (spec §9) — VenueOS never
/// reimplements FFXIV command parsing.</summary>
public enum MacroDirectiveKind { PlainLine, Blank, NestedMacro, ActionReady }

/// <summary><see cref="MacroName"/> is set only when <see cref="Kind"/> is <see cref="MacroDirectiveKind.NestedMacro"/>.
/// <see cref="Blank"/> is a deliberate third case distinct from <see cref="MacroDirectiveKind.PlainLine"/>: a
/// whitespace-only line carries no chat command at all (<c>ChatCommandService.Enqueue</c> already no-ops on blank
/// text) and is not a meaningful FFXIV command either, so <c>MacroRunner</c> treats it as a free, zero-delay
/// no-op step rather than spending the macro's configured delay sending nothing — see MacroRunner's doc comment.</summary>
public readonly record struct MacroLineDirective(MacroDirectiveKind Kind, string RawLine, string? MacroName)
{
    public static MacroLineDirective Plain(string raw) => new(MacroDirectiveKind.PlainLine, raw, null);
    public static MacroLineDirective BlankLine(string raw) => new(MacroDirectiveKind.Blank, raw, null);
    public static MacroLineDirective Nested(string raw, string name) => new(MacroDirectiveKind.NestedMacro, raw, name);
    public static MacroLineDirective Ready(string raw) => new(MacroDirectiveKind.ActionReady, raw, null);
}

/// <summary>Pure, ImGui/game-free parsing of one authored macro line into a <see cref="MacroLineDirective"/>. Both
/// recognized directives require the ENTIRE trimmed line to match — a directive embedded mid-line (e.g. inside an
/// <c>/echo</c> argument) is deliberately NOT recognized, so an operator quoting these strings for other purposes
/// never has them misfire; this mirrors how the real <c>/venueos</c> command line itself is matched (MACRO spec §43:
/// canonical syntax only).</summary>
public static class MacroDirectiveParser
{
    // Matches: /venueos macro "Some Name" — case-insensitive command, exactly one required quoted name, optional
    // surrounding whitespace. The quoted name may contain anything except a literal double-quote.
    private static readonly Regex NestedMacroPattern = new(
        @"^/venueos\s+macro\s+""([^""]+)""\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ActionReadyPattern = new(
        @"^/actionready\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static MacroLineDirective Parse(string line)
    {
        var trimmed = line?.Trim() ?? "";
        if (trimmed.Length == 0) return MacroLineDirective.BlankLine(line ?? "");

        var nestedMatch = NestedMacroPattern.Match(trimmed);
        if (nestedMatch.Success) return MacroLineDirective.Nested(line!, nestedMatch.Groups[1].Value);

        if (ActionReadyPattern.IsMatch(trimmed)) return MacroLineDirective.Ready(line!);

        return MacroLineDirective.Plain(line!);
    }

    /// <summary>The canonical, single-source-of-truth text for a nested-macro-invocation line — used both when the
    /// operator authors one manually and when <c>MacroService</c> rewrites an existing nested reference after a
    /// rename (NEW_MODULE_GUIDE-style "exact parsed reference update", never a blind substring replace).</summary>
    public static string FormatNestedInvocation(string macroName) => $"/venueos macro \"{macroName}\"";
}

/// <summary>Finds which OTHER saved macros contain a line that is a parsed <see cref="MacroDirectiveKind.NestedMacro"/>
/// reference to a given macro name — used both for rename cascade (MacroService rewrites these lines in place) and
/// for delete-time "this macro is referenced elsewhere" detection (MACRO spec §16, confirmed via ConfirmDialog
/// before the parent macros are left with a dangling reference).</summary>
public static class MacroReferenceScanner
{
    public static IReadOnlyList<SavedMacro> FindReferencing(IReadOnlyList<SavedMacro> allMacros, string targetName) =>
        allMacros.Where(m => m.Lines.Any(line => IsReferenceTo(line, targetName))).ToArray();

    public static bool IsReferenceTo(string line, string targetName)
    {
        var directive = MacroDirectiveParser.Parse(line);
        return directive.Kind == MacroDirectiveKind.NestedMacro &&
               string.Equals(directive.MacroName, targetName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rewrites every line in <paramref name="lines"/> that is an exact parsed nested-reference to
    /// <paramref name="oldName"/> so it instead references <paramref name="newName"/> — every other line (including
    /// ordinary text that merely happens to contain the old name as a substring) is left byte-for-byte unchanged.</summary>
    public static IReadOnlyList<string> RewriteReferences(IReadOnlyList<string> lines, string oldName, string newName) =>
        lines.Select(line => IsReferenceTo(line, oldName) ? MacroDirectiveParser.FormatNestedInvocation(newName) : line).ToArray();
}
