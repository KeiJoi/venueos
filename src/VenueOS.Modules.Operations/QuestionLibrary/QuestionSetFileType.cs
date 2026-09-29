using VenueOS.Services;

namespace VenueOS.Modules.Operations.QuestionLibrary;

/// <summary>The user-facing file type for a Mair's Trivia Question Set. <c>.fftrivia</c> is the canonical extension —
/// its contents happen to be JSON (<see cref="TriviaQuestionSet"/>), but operators never rename it. Used by Mair's
/// Editor's import file picker, whose primary/default filter is <see cref="Filter"/>, with All Files as a fallback;
/// choosing a file only fills the import path — the existing Import action and its parsing/validation are unchanged.</summary>
public static class QuestionSetFileType
{
    public const string Extension = ".fftrivia";

    public static FileBrowserFilter Filter { get; } = new("Mair's Trivia Question Sets (*.fftrivia)", [Extension]);

    /// <summary>Filter list for the picker, primary first.</summary>
    public static IReadOnlyList<FileBrowserFilter> PickerFilters { get; } = [Filter, FileBrowserFilter.AllFiles];

    public static bool IsQuestionSetFile(string? path) => !string.IsNullOrWhiteSpace(path) && Filter.Matches(path.Trim());
}
