namespace VenueOS.Modules.Operations.QuestionLibrary;

/// <summary>
/// The canonical, VenueOS-owned question/set model. This is deliberately the exact .fftrivia wire shape (see
/// <c>MairsTriviaClient.CreateGameAsync</c>/<c>AddQuestionSetAsync</c>, which reference these same types) rather than a
/// parallel "editor model" plus a separate "wire model" — one authoritative shape, read by Mair's Trivia and
/// authored by Mair's Editor through <see cref="IQuestionSetRepository"/>, per NEW_MODULE_GUIDE.md's shared-service rule.
/// </summary>
public sealed record TriviaQuestion(Guid Id, string Question, string CorrectAnswer, IReadOnlyList<string> IncorrectAnswers, string? Category, IReadOnlyList<string> Tags)
{
    public TriviaQuestion Duplicate() => this with { Id = Guid.NewGuid() };
}

public sealed record TriviaQuestionSet(string Format, int SchemaVersion, Guid Id, string Title, string Description, string Author, string Version, IReadOnlyList<string> Categories, IReadOnlyList<string> Tags, IReadOnlyList<TriviaQuestion> Questions)
{
    public const string CanonicalFormat = "fftrivia-question-set";

    public static TriviaQuestionSet CreateDraft(string title) => new(CanonicalFormat, 2, Guid.NewGuid(), title, "", "", "1.0.0", [], [], []);

    /// <summary>True only when the set is READY (see <see cref="QuestionSetValidator"/>) — the sole gate Mair's Trivia
    /// enforces before a set can be attached to a game. Equivalent to <c>QuestionSetValidator.Validate(this).Status == Ready</c>.</summary>
    public bool IsValidHostSet() => QuestionSetValidator.Validate(this).Status == QuestionSetStatus.Ready;

    /// <summary>New set UUID, new UUID for every question, "&lt;title&gt; - Copy" default title. Never retains a source question UUID.</summary>
    public TriviaQuestionSet Duplicate(string? title = null) => this with { Id = Guid.NewGuid(), Title = title ?? $"{Title} - Copy", Questions = [.. Questions.Select(q => q.Duplicate())] };
}

public enum QuestionSetStatus
{
    /// <summary>Valid and playable — the only status Mair's Trivia may attach to a game.</summary>
    Ready,
    /// <summary>Structurally understandable, still being authored — legitimate, expected mid-work state.</summary>
    Incomplete,
    /// <summary>Corrupt, unsupported, or structurally wrong — cannot be used regardless of authoring progress.</summary>
    Invalid,
}

public sealed record QuestionSetValidation(QuestionSetStatus Status, IReadOnlyList<string> Reasons)
{
    public static QuestionSetValidation Ready() => new(QuestionSetStatus.Ready, []);
}

/// <summary>Navigation/repository metadata only — never a second authoritative copy of question content.</summary>
public sealed record QuestionLibraryIndexEntry(Guid Id, string Title, int Order, string ContentHash, DateTimeOffset UpdatedAt, QuestionSetStatus Status);

public enum ImportCollisionPolicy { Replace, ImportAsNew }

public sealed record ImportCollisionInfo(Guid Id, string ExistingTitle, string IncomingTitle);

public sealed record ImportOutcome(bool Collided, ImportCollisionInfo? Collision, TriviaQuestionSet? Imported);
