namespace VenueOS.Modules.Operations.QuestionLibrary;

/// <summary>
/// The single, VenueOS-wide (not per-venue) canonical question repository. Constructed once at the composition root
/// and shared by Mair's Trivia (read-only picker) and Mair's Editor (authoring) — its lifetime is independent of
/// either module's enabled state, per the reconstruction's Decision 1. Storage-agnostic by design: a future
/// cloud/multi-PC-synchronized implementation can replace <see cref="FileQuestionSetRepository"/> without either
/// module changing, since both only ever depend on this interface.
/// </summary>
public interface IQuestionSetRepository
{
    /// <summary>Manually-ordered navigation list. Order is a repository/library concern only — it never affects a set's UUID, its questions' order, or any backend game queue.</summary>
    IReadOnlyList<QuestionLibraryIndexEntry> List();
    TriviaQuestionSet? Read(Guid id);
    void Save(TriviaQuestionSet set);
    /// <summary>Blocks deletion when <paramref name="isInUse"/> (typically backed by the live Trivia service's active/resumable games) reports the set is referenced. Returns false without deleting anything when blocked.</summary>
    bool Delete(Guid id, Func<Guid, bool> isInUse);
    /// <summary>Fresh set UUID and a fresh UUID for every question — never a source-ID-preserving copy.</summary>
    TriviaQuestionSet Duplicate(Guid id, string? title = null);
    /// <summary>Null if the incoming UUID does not already exist (caller may just <see cref="Save"/> it); otherwise describes the collision so the caller can offer Replace / Import As New / Cancel.</summary>
    ImportCollisionInfo? CheckImportCollision(string fftriviaJson);
    TriviaQuestionSet ImportReplacing(string fftriviaJson);
    TriviaQuestionSet ImportAsNew(string fftriviaJson, string? title = null);
    string Export(Guid id);
    void Reorder(IReadOnlyList<Guid> orderedIdsInDesiredOrder);
    /// <summary>Recovers the navigation index by rescanning content files. Returns human-readable warnings for anything unreadable (route these to DiagnosticsService) — corrupt files are never deleted.</summary>
    IReadOnlyList<string> RebuildIndex();
}
