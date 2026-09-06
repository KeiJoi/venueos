namespace VenueOS.Modules.Operations.QuestionLibrary;

/// <summary>
/// Rigorous Ready/Incomplete/Invalid classification. Never mutates the set as a side effect of validating it —
/// this is pure inspection. Fixes the donor/VenueOS-scaffold gaps the reconstruction audit identified: blank/duplicate
/// wrong-answer text, duplicate question UUIDs, and null-collection crashes are all now checked defensively.
/// </summary>
public static class QuestionSetValidator
{
    public static QuestionSetValidation Validate(TriviaQuestionSet? set)
    {
        var invalid = new List<string>();
        var incomplete = new List<string>();

        if (set is null) { invalid.Add("Question set is missing."); return new(QuestionSetStatus.Invalid, invalid); }
        if (set.Format != TriviaQuestionSet.CanonicalFormat) invalid.Add($"Unsupported format \"{set.Format}\".");
        if (set.SchemaVersion is not (1 or 2)) invalid.Add($"Unsupported schema version {set.SchemaVersion}.");
        if (set.Id == Guid.Empty) invalid.Add("Set has no identity (empty UUID).");
        if (set.Questions is null) { invalid.Add("Question list is missing."); return new(QuestionSetStatus.Invalid, invalid); }
        if (set.Categories is null || set.Tags is null) invalid.Add("Set metadata lists are missing.");

        var minimumWrongAnswers = set.SchemaVersion == 1 ? 9 : 3;
        var seenQuestionIds = new HashSet<Guid>();

        if (string.IsNullOrWhiteSpace(set.Title)) incomplete.Add("Set title is empty.");
        if (string.IsNullOrWhiteSpace(set.Author)) incomplete.Add("Set author is empty.");
        if (string.IsNullOrWhiteSpace(set.Version)) incomplete.Add("Set version is empty.");
        if (set.Questions.Count == 0) incomplete.Add("Set has no questions.");
        if (HasDuplicates(set.Categories ?? [])) invalid.Add("Set categories contain a duplicate (case-insensitive).");
        if (HasDuplicates(set.Tags ?? [])) invalid.Add("Set tags contain a duplicate (case-insensitive).");

        foreach (var (question, index) in set.Questions.Select((q, i) => (q, i)))
        {
            var label = string.IsNullOrWhiteSpace(question.Question) ? $"Question {index + 1}" : $"\"{Truncate(question.Question)}\"";
            if (question.Id == Guid.Empty) { invalid.Add($"{label} has no identity (empty UUID)."); continue; }
            if (!seenQuestionIds.Add(question.Id)) { invalid.Add($"{label} reuses a question UUID already used earlier in this set."); continue; }
            if (question.IncorrectAnswers is null) { invalid.Add($"{label} is missing its wrong-answer list."); continue; }
            if (question.IncorrectAnswers.Count > 9) invalid.Add($"{label} has more than the supported 9 wrong answers.");
            if (string.IsNullOrWhiteSpace(question.Question)) incomplete.Add($"Question {index + 1} has no question text.");
            if (string.IsNullOrWhiteSpace(question.CorrectAnswer)) incomplete.Add($"{label} has no correct answer.");
            if (question.IncorrectAnswers.Count < minimumWrongAnswers) incomplete.Add($"{label} needs at least {minimumWrongAnswers} wrong answers (has {question.IncorrectAnswers.Count}).");
            if (question.IncorrectAnswers.Any(string.IsNullOrWhiteSpace)) incomplete.Add($"{label} has a blank wrong-answer slot.");
            var nonBlankAnswers = new[] { question.CorrectAnswer }.Concat(question.IncorrectAnswers).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            if (HasDuplicates(nonBlankAnswers)) invalid.Add($"{label} has a duplicate answer text (correct and wrong answers must all be distinct).");
            if (question.Category is not null && string.IsNullOrWhiteSpace(question.Category)) incomplete.Add($"{label} has a blank (rather than empty) category.");
            if (HasDuplicates(question.Tags ?? [])) invalid.Add($"{label} has duplicate tags.");
        }

        if (invalid.Count > 0) return new(QuestionSetStatus.Invalid, invalid);
        if (incomplete.Count > 0) return new(QuestionSetStatus.Incomplete, incomplete);
        return QuestionSetValidation.Ready();
    }

    private static bool HasDuplicates(IEnumerable<string> values)
    {
        var trimmed = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim().ToLowerInvariant()).ToList();
        return trimmed.Count != trimmed.Distinct().Count();
    }

    private static string Truncate(string value) => value.Length <= 40 ? value : value[..37] + "...";
}
