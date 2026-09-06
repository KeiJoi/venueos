using VenueOS.Core;
using VenueOS.Modules.Operations.QuestionLibrary;
using VenueOS.Venues;

namespace VenueOS.Modules.Operations.MairsEditor;

/// <summary>
/// Authoring service over the shared <see cref="IQuestionSetRepository"/>. Holds an unsaved WORKING DRAFT plus the
/// last-saved baseline — it is never a second authoritative library. Save is the one explicit commit operation
/// (Decision 6: no separate Save Draft / Validate-and-Save). Undo is a bounded, in-memory command/snapshot history
/// scoped to the current draft session; Save does NOT clear it, so Save → notice a mistake → Undo → Save again
/// works exactly as required, and Undo can legitimately make the draft dirty again relative to what was last saved.
/// </summary>
public sealed class MairsEditorService(IQuestionSetRepository library, Func<Guid, bool>? isSetInUse = null)
{
    private const int MaxUndoDepth = 50;
    private readonly List<TriviaQuestionSet> undo = [];
    private readonly Func<Guid, bool> isInUse = isSetInUse ?? (_ => false);

    public TriviaQuestionSet? Draft { get; private set; }
    public TriviaQuestionSet? SavedBaseline { get; private set; }
    public bool HasOpenDocument => Draft is not null;
    public bool IsDirty => Draft is not null && !ContentEquals(Draft, SavedBaseline);
    public bool CanUndo => undo.Count > 0;
    public QuestionSetValidation Validation => QuestionSetValidator.Validate(Draft);
    public IReadOnlyList<QuestionLibraryIndexEntry> Library => library.List();

    public void OpenSet(Guid id)
    {
        var set = library.Read(id) ?? throw new InvalidOperationException($"Question set {id} does not exist.");
        Draft = set; SavedBaseline = set; undo.Clear();
    }
    public void NewSet(string title)
    {
        Draft = TriviaQuestionSet.CreateDraft(string.IsNullOrWhiteSpace(title) ? "Untitled Question Set" : title.Trim());
        SavedBaseline = null; undo.Clear();
    }
    /// <summary>Closes the current document without saving. Callers must obtain confirmation from the operator first if <see cref="IsDirty"/>.</summary>
    public void CloseDocument() { Draft = null; SavedBaseline = null; undo.Clear(); }

    /// <summary>The one explicit Save. Incomplete/blank-in-progress content may be saved — READY is what gates Trivia's use of it, not Save itself.</summary>
    public void Save()
    {
        if (Draft is null) return;
        library.Save(Draft);
        SavedBaseline = Draft;
    }

    public bool Undo()
    {
        if (undo.Count == 0 || Draft is null) return false;
        Draft = undo[^1]; undo.RemoveAt(undo.Count - 1);
        return true;
    }

    private void PushUndo() { if (Draft is null) return; undo.Add(Draft); if (undo.Count > MaxUndoDepth) undo.RemoveAt(0); }
    private void Mutate(Func<TriviaQuestionSet, TriviaQuestionSet> mutate) { if (Draft is null) return; PushUndo(); Draft = mutate(Draft); }

    public void SetTitle(string value) => Mutate(s => s with { Title = value });
    public void SetDescription(string value) => Mutate(s => s with { Description = value });
    public void SetAuthor(string value) => Mutate(s => s with { Author = value });
    public void SetVersion(string value) => Mutate(s => s with { Version = value });
    public void SetCategories(IReadOnlyList<string> values) => Mutate(s => s with { Categories = Distinct(values) });
    public void SetTags(IReadOnlyList<string> values) => Mutate(s => s with { Tags = Distinct(values) });
    public void UpgradeSchema() => Mutate(s => s with { SchemaVersion = 2 });

    public void AddQuestion() => Mutate(s => s with { Questions = [.. s.Questions, new TriviaQuestion(Guid.NewGuid(), "", "", ["", "", ""], null, [])] });
    public void DeleteQuestion(Guid id) => Mutate(s => s with { Questions = [.. s.Questions.Where(q => q.Id != id)] });
    public void DuplicateQuestion(Guid id) => Mutate(s =>
    {
        var index = s.Questions.ToList().FindIndex(q => q.Id == id); if (index < 0) return s;
        var copy = s.Questions[index].Duplicate();
        var list = s.Questions.ToList(); list.Insert(index + 1, copy);
        return s with { Questions = list };
    });
    public void MoveQuestion(Guid id, int direction) => Mutate(s =>
    {
        var list = s.Questions.ToList(); var index = list.FindIndex(q => q.Id == id); var target = index + direction;
        if (index < 0 || target < 0 || target >= list.Count) return s;
        (list[index], list[target]) = (list[target], list[index]);
        return s with { Questions = list };
    });
    public void UpdateQuestion(Guid id, Func<TriviaQuestion, TriviaQuestion> update) => Mutate(s => s with { Questions = [.. s.Questions.Select(q => q.Id == id ? update(q) : q)] });

    public TriviaQuestionSet DuplicateSet(Guid id, string? title = null) => library.Duplicate(id, title);
    public bool DeleteSet(Guid id) => library.Delete(id, isInUse);
    public bool IsSetInUse(Guid id) => isInUse(id);
    public ImportCollisionInfo? CheckImportCollision(string fftriviaJson) => library.CheckImportCollision(fftriviaJson);
    public TriviaQuestionSet ImportReplacing(string fftriviaJson) => library.ImportReplacing(fftriviaJson);
    public TriviaQuestionSet ImportAsNew(string fftriviaJson, string? title = null) => library.ImportAsNew(fftriviaJson, title);
    public string Export(Guid id) => library.Export(id);
    public void Reorder(IReadOnlyList<Guid> orderedIds) => library.Reorder(orderedIds);
    public IReadOnlyList<string> RebuildIndex() => library.RebuildIndex();

    private static IReadOnlyList<string> Distinct(IReadOnlyList<string> values) => [.. values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// NOT record `==`: <see cref="TriviaQuestionSet"/>'s list-typed properties (Questions/Categories/Tags) are
    /// declared as <c>IReadOnlyList&lt;T&gt;</c>, and synthesized record equality compares those by the concrete
    /// backing type's own Equals — which for arrays/List&lt;T&gt; is reference equality, not element-wise. Two
    /// content-identical drafts built from separate `with` expressions (e.g. after Undo lands back on a state that
    /// happens to match what was last saved) would therefore show as spuriously dirty. This does real structural
    /// comparison so IsDirty only ever reflects an actual content difference.
    /// </summary>
    private static bool ContentEquals(TriviaQuestionSet? a, TriviaQuestionSet? b)
    {
        if (a is null || b is null) return false;
        return a.Format == b.Format && a.SchemaVersion == b.SchemaVersion && a.Id == b.Id && a.Title == b.Title && a.Description == b.Description
            && a.Author == b.Author && a.Version == b.Version
            && a.Categories.SequenceEqual(b.Categories, StringComparer.Ordinal) && a.Tags.SequenceEqual(b.Tags, StringComparer.Ordinal)
            && a.Questions.Count == b.Questions.Count && a.Questions.Zip(b.Questions).All(pair => QuestionEquals(pair.First, pair.Second));
    }
    private static bool QuestionEquals(TriviaQuestion a, TriviaQuestion b) =>
        a.Id == b.Id && a.Question == b.Question && a.CorrectAnswer == b.CorrectAnswer && a.Category == b.Category
        && a.IncorrectAnswers.SequenceEqual(b.IncorrectAnswers, StringComparer.Ordinal) && a.Tags.SequenceEqual(b.Tags, StringComparer.Ordinal);
}

/// <summary>No persistent preferences exist for local authoring today — a concise "nothing to configure" surface is
/// valid (VIP's Settings contribution is the existing precedent). Never shows Trivia's backend credentials here:
/// local authoring works fully offline and must not gain an accidental dependency on a live session.</summary>
public sealed class MairsEditorModule(Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    public ModuleDescriptor Descriptor { get; } = new("games.mairseditor", "Mair's Editor", "Question-set and trivia-content authoring.", "file-pen");
    public bool IsEnabled { get; set; } = true;
    public Task InitializeAsync(ModuleContext c, CancellationToken t) => Task.CompletedTask;
    // The shared question library is VenueOS-wide, not per-venue — a venue switch never resets or touches the editor's working draft.
    public Task OnVenueChangedAsync(VenueContext c, CancellationToken t) => Task.CompletedTask;
    public void Tick(DateTimeOffset now) { }
    public void Draw() => draw?.Invoke();
    public void DrawSettings() => (drawSettings ?? draw)?.Invoke();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // in-memory draft is intentionally preserved across disable/re-enable — nothing to release
}
