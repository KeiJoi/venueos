using VenueOS.Modules.Operations.BlockLetters;

namespace VenueOS.Modules.Operations.Macro;

/// <summary>The result of a macro-authoring validation attempt (create or edit) — MACRO LIVE QA FIX §17: nothing is
/// ever saved while <see cref="Success"/> is false. Distinct from <see cref="MacroLaunchResult"/> (an EXECUTION
/// launch outcome) — this is purely about whether a draft is safe to persist. Carries every problem found, not just
/// the first, so an operator sees the complete picture (a bad name AND an oversized line) in one pass, matching
/// <c>GiveawayPresetValidator</c>'s existing convention.</summary>
public readonly record struct MacroValidationResult(bool Success, IReadOnlyList<string> Errors)
{
    public static MacroValidationResult Ok() => new(true, Array.Empty<string>());
    public static MacroValidationResult Failed(IReadOnlyList<string> errors) => new(false, errors);
}

/// <summary>Converts between a macro's authored MULTILINE BODY TEXT (what the single large editor field holds —
/// MACRO LIVE QA FIX §7/§14) and its executable <c>IReadOnlyList&lt;string&gt;</c> line list (what
/// <see cref="MacroRunner"/> actually executes, and what is persisted — unchanged from before this fix pass). The
/// runner/persistence model itself is untouched; this is a pure, both-directions conversion layer sitting in front
/// of it, per the fix spec's explicit "keep the runner simple" instruction (§11).
///
/// EOF RULE (spec §9/§10 — a deliberate product rule, not a parsing accident): the body is read top to bottom; the
/// FIRST logical line that is empty, or whitespace-only once trimmed, ends the macro. Every line at or after that
/// point is excluded from the executable/saved result — pure authoring scratch space, never sent to the game and
/// never persisted. A line containing only spaces/tabs is treated exactly like a truly empty line for this
/// purpose, specifically so an accidental whitespace-only line can never create an invisible, unintended
/// mid-macro stop that behaves differently from a visibly-blank one.</summary>
public static class MacroBodyText
{
    public static IReadOnlyList<string> ToExecutableLines(string? bodyText)
    {
        if (string.IsNullOrEmpty(bodyText)) return Array.Empty<string>();
        // CRLF first (so a lone '\r' pass afterward can't double-convert it), then any remaining lone '\r' (old
        // Mac-style line endings) — this is the only normalization applied; line CONTENT itself is never trimmed
        // or otherwise altered, only used (via a temporary Trim()) to test for EOF.
        var normalized = bodyText.Replace("\r\n", "\n").Replace('\r', '\n');
        var executable = new List<string>();
        foreach (var line in normalized.Split('\n'))
        {
            if (line.Trim().Length == 0) break; // first empty/whitespace-only line = EOF
            executable.Add(line);
        }
        return executable;
    }

    /// <summary>The inverse — seeds the editor's draft when opening an existing macro for editing. Lines are joined
    /// with a plain <c>'\n'</c> (ImGui's multiline input handles this consistently regardless of platform), never
    /// <see cref="Environment.NewLine"/>, so round-tripping through the editor without any edit is deterministic.</summary>
    public static string ToBodyText(IReadOnlyList<string> lines) => string.Join('\n', lines);
}

/// <summary>Per-logical-line FFXIV byte-limit validation for a macro body (MACRO LIVE QA FIX §12) — reuses Block
/// Letters' already-verified UTF-8 byte counter (<see cref="BlockTextLength.CountBytes"/>), never a second
/// implementation. Never truncates: a line that's too long is reported by 1-based line number and both its actual
/// and maximum byte counts, exactly as the fix spec's example error text requires ("Line 17 is 528 / 500 bytes."),
/// so the operator can see and fix the actual offending content rather than have it silently corrupted.</summary>
public static class MacroLineValidator
{
    public static IReadOnlyList<string> FindOversizedLines(IReadOnlyList<string> executableLines)
    {
        List<string>? errors = null;
        for (var i = 0; i < executableLines.Count; i++)
        {
            var bytes = BlockTextLength.CountBytes(executableLines[i]);
            if (bytes <= MacroLineLimits.MaxLineBytes) continue;
            errors ??= [];
            errors.Add($"Line {i + 1} is {bytes} / {MacroLineLimits.MaxLineBytes} bytes.");
        }
        return errors ?? (IReadOnlyList<string>)Array.Empty<string>();
    }
}
