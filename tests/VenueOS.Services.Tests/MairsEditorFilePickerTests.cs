using VenueOS.Modules.Operations.MairsEditor;
using VenueOS.Modules.Operations.QuestionLibrary;
using VenueOS.Services;

namespace VenueOS.Services.Tests;

/// <summary>Mair's Editor's "Browse…" picker for <c>.fftrivia</c> import files. Covers the ImGui-free parts — the
/// <see cref="FileBrowser"/> state (listing, filtering, navigation, selection, filesystem failure handling), the
/// <see cref="FilePickerResult"/> path rule (select fills, cancel preserves), the initial-folder rule, and that a
/// picked path feeds the SAME existing import pipeline. Rendering of <c>Shell/FilePickerModal</c> is live-QA only
/// (no ImGui test harness exists — NEW_MODULE_GUIDE.md §30). Every test works in its own temp folder.</summary>
public sealed class MairsEditorFilePickerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "venueos-picker-" + Guid.NewGuid().ToString("N"));

    public MairsEditorFilePickerTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------- .fftrivia is the canonical, primary type ----------

    [Fact]
    public void Fftrivia_is_the_canonical_extension_and_the_primary_default_filter()
    {
        Assert.Equal(".fftrivia", QuestionSetFileType.Extension);
        Assert.Equal("Mair's Trivia Question Sets (*.fftrivia)", QuestionSetFileType.PickerFilters[0].Description);
        Assert.Same(QuestionSetFileType.Filter, QuestionSetFileType.PickerFilters[0]);
        Assert.Same(FileBrowserFilter.AllFiles, QuestionSetFileType.PickerFilters[1]);
        Assert.Equal(2, QuestionSetFileType.PickerFilters.Count);

        var browser = NewBrowser(root);
        Assert.Equal(0, browser.FilterIndex);
        Assert.Same(QuestionSetFileType.Filter, browser.ActiveFilter);
    }

    [Fact]
    public void The_default_filter_lists_folders_and_fftrivia_files_only_case_insensitively()
    {
        Directory.CreateDirectory(Path.Combine(root, "Sub Folder"));
        Touch("a.fftrivia"); Touch("B.FFTRIVIA"); Touch("c.json"); Touch("d.txt"); Touch("e.fftrivia.bak");

        var browser = NewBrowser(root);

        Assert.Equal(["Sub Folder", "a.fftrivia", "B.FFTRIVIA"], browser.Entries.Select(e => e.Name));
        Assert.Equal(FileBrowserEntryKind.Directory, browser.Entries[0].Kind);
        Assert.True(QuestionSetFileType.IsQuestionSetFile(@"C:\x\y.FFTrivia"));
        Assert.False(QuestionSetFileType.IsQuestionSetFile(@"C:\x\y.json"));
        Assert.False(QuestionSetFileType.IsQuestionSetFile("  "));
    }

    [Fact]
    public void All_Files_is_an_optional_fallback_that_shows_everything()
    {
        Touch("a.fftrivia"); Touch("c.json");
        var browser = NewBrowser(root);
        browser.SetFilter(1);
        Assert.Equal(["a.fftrivia", "c.json"], browser.Entries.Select(e => e.Name));

        var json = browser.Entries.Single(e => e.Name == "c.json");
        browser.Activate(json);
        Assert.Equal(json.FullPath, browser.Confirm()); // picker allows it; Import's own validation still decides

        browser.SetFilter(0); // switching back drops a selection that no longer matches the primary filter
        Assert.Null(browser.SelectedPath);
        Assert.Equal(["a.fftrivia"], browser.Entries.Select(e => e.Name));
    }

    // ---------- selection → absolute path ----------

    [Fact]
    public void Selecting_a_valid_fftrivia_file_yields_its_absolute_path()
    {
        var file = Touch("mairs-trivia-us-history.fftrivia");
        var browser = NewBrowser(root);

        browser.Activate(browser.Entries.Single());
        var picked = browser.Confirm();

        Assert.Equal(Path.GetFullPath(file), picked);
        Assert.True(Path.IsPathFullyQualified(picked!));
        Assert.Null(browser.Error);
    }

    [Fact]
    public void Confirm_without_a_selection_reports_an_error_and_returns_nothing()
    {
        var browser = NewBrowser(root);
        Assert.Null(browser.Confirm());
        Assert.Equal("Select a file first.", browser.Error);
    }

    [Fact]
    public void A_confirmed_pick_replaces_the_path_field_and_a_cancel_preserves_it_exactly()
    {
        const string before = @"  C:\Some Old\path.fftrivia ";
        Assert.Equal(before, FilePickerResult.Cancelled.ApplyTo(before));
        Assert.Equal("", FilePickerResult.Cancelled.ApplyTo(""));
        Assert.False(FilePickerResult.Cancelled.Confirmed);

        var picked = Path.Combine(root, "new.fftrivia");
        Assert.Equal(picked, FilePickerResult.Selected(picked).ApplyTo(before));
    }

    [Fact]
    public void Paths_with_spaces_and_punctuation_work()
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "My Trivia (2026) - Vol. #1 & more")).FullName;
        var file = Touch(Path.Combine(dir, "mairs trivia - us history's [final], v2.fftrivia"));

        var browser = NewBrowser(root);
        browser.Activate(browser.Entries.Single(e => e.Kind == FileBrowserEntryKind.Directory));
        Assert.Equal(dir, browser.CurrentDirectory);
        browser.Activate(browser.Entries.Single());

        Assert.Equal(file, browser.Confirm());
    }

    [Fact]
    public void Unicode_folder_and_file_names_work()
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "Тривия ✓ 日本語")).FullName;
        var file = Touch(Path.Combine(dir, "クイズ – café.fftrivia"));

        var browser = NewBrowser(dir);
        Assert.Equal("クイズ – café.fftrivia", browser.Entries.Single().Name);
        browser.Activate(browser.Entries.Single());

        Assert.Equal(file, browser.Confirm());
    }

    [Fact]
    public void A_long_file_name_works()
    {
        var name = new string('q', 200) + QuestionSetFileType.Extension;
        var file = Touch(name);
        var browser = NewBrowser(root);
        browser.Activate(browser.Entries.Single());
        Assert.Equal(file, browser.Confirm());
    }

    // ---------- the picker never imports and never touches the file ----------

    [Fact]
    public void Browsing_and_selecting_does_not_import_or_modify_the_file()
    {
        var library = new FileQuestionSetRepository(Path.Combine(root, "library"));
        var editor = new MairsEditorService(library);
        var file = Path.Combine(root, "set.fftrivia");
        File.WriteAllText(file, SerializedSet("Picked Set"));
        var bytes = File.ReadAllBytes(file);
        var written = File.GetLastWriteTimeUtc(file);

        var browser = NewBrowser(root);
        browser.Activate(browser.Entries.Single(e => e.Kind == FileBrowserEntryKind.File));
        var picked = FilePickerResult.Selected(browser.Confirm()!).ApplyTo("");

        Assert.Equal(file, picked);
        Assert.Empty(editor.Library);                  // selection alone imported nothing
        Assert.Equal(bytes, File.ReadAllBytes(file));  // not rewritten
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        Assert.Single(Directory.GetFiles(root, "*.fftrivia")); // not copied/renamed
    }

    [Fact]
    public void A_picked_path_goes_through_the_same_existing_import_pipeline_as_a_typed_one()
    {
        var editor = new MairsEditorService(new FileQuestionSetRepository(Path.Combine(root, "library")));
        var file = Path.Combine(root, "US History.fftrivia");
        File.WriteAllText(file, SerializedSet("US History"));

        var browser = NewBrowser(root);
        browser.Activate(browser.Entries.Single(e => e.Kind == FileBrowserEntryKind.File));
        var picked = browser.Confirm()!;

        // Exactly what MairsEditorOperatorPanel.TryImport does with the path field, regardless of how it got there.
        Assert.Null(editor.CheckImportCollision(File.ReadAllText(picked)));
        var imported = editor.ImportReplacing(File.ReadAllText(picked));
        Assert.Equal("US History", imported.Title);
        Assert.Single(editor.Library);
    }

    [Fact]
    public void Existing_import_validation_stays_authoritative_for_a_picked_malformed_file()
    {
        var editor = new MairsEditorService(new FileQuestionSetRepository(Path.Combine(root, "library")));
        Touch("broken.fftrivia", "{ this is not a question set");

        var browser = NewBrowser(root);
        browser.Activate(browser.Entries.Single(e => e.Kind == FileBrowserEntryKind.File));
        var picked = browser.Confirm();

        Assert.NotNull(picked); // the picker does not parse content…
        var error = Assert.Throws<InvalidOperationException>(() => editor.ImportReplacing(File.ReadAllText(picked!)));
        Assert.Contains("not valid JSON", error.Message); // …the existing importer still rejects it
        Assert.Empty(editor.Library);
    }

    // ---------- initial folder ----------

    [Fact]
    public void The_initial_folder_is_the_current_paths_folder_when_it_is_a_valid_fftrivia_file()
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "sets")).FullName;
        var file = Touch(Path.Combine(dir, "x.fftrivia"));
        var last = Directory.CreateDirectory(Path.Combine(root, "last")).FullName;

        Assert.Equal(dir, FileBrowser.ResolveInitialDirectory("  " + file + " ", QuestionSetFileType.Filter, last, root));
    }

    [Fact]
    public void The_initial_folder_falls_back_to_the_last_picked_folder_then_the_default_then_the_drive_list()
    {
        var last = Directory.CreateDirectory(Path.Combine(root, "last")).FullName;
        var jsonFile = Touch("not-a-set.json");

        Assert.Equal(last, FileBrowser.ResolveInitialDirectory("", QuestionSetFileType.Filter, last, root));
        Assert.Equal(last, FileBrowser.ResolveInitialDirectory(Path.Combine(root, "missing.fftrivia"), QuestionSetFileType.Filter, last, root));
        Assert.Equal(last, FileBrowser.ResolveInitialDirectory(jsonFile, QuestionSetFileType.Filter, last, root));
        Assert.Equal(last, FileBrowser.ResolveInitialDirectory("C:\\bad\0path.fftrivia", QuestionSetFileType.Filter, last, root));
        Assert.Equal(root, FileBrowser.ResolveInitialDirectory("", QuestionSetFileType.Filter, Path.Combine(root, "gone"), root));
        Assert.Null(FileBrowser.ResolveInitialDirectory(null, QuestionSetFileType.Filter, null, Path.Combine(root, "also-gone")));
    }

    [Fact]
    public void A_missing_initial_folder_opens_the_drive_list_instead_of_failing()
    {
        var browser = new FileBrowser(QuestionSetFileType.PickerFilters);
        browser.Reset(Path.Combine(root, "does-not-exist"));
        Assert.Null(browser.CurrentDirectory);
        Assert.Equal("That folder no longer exists.", browser.Error);
        Assert.All(browser.Entries, e => Assert.Equal(FileBrowserEntryKind.Drive, e.Kind));
    }

    // ---------- navigation & filesystem robustness ----------

    [Fact]
    public void Navigating_into_up_and_back_works()
    {
        var child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
        var browser = NewBrowser(root);
        Assert.False(browser.CanGoBack);

        browser.Activate(browser.Entries.Single());
        Assert.Equal(child, browser.CurrentDirectory);
        browser.GoUp();
        Assert.Equal(Path.GetFullPath(root), browser.CurrentDirectory);
        browser.GoBack();
        Assert.Equal(child, browser.CurrentDirectory);
        browser.GoBack();
        Assert.Equal(Path.GetFullPath(root), browser.CurrentDirectory);
    }

    [Fact]
    public void Up_from_a_drive_root_shows_the_drive_list_and_Back_returns()
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(root))!;
        var browser = NewBrowser(driveRoot);
        Assert.True(browser.CanGoUp);

        browser.GoUp();
        Assert.Null(browser.CurrentDirectory);
        Assert.False(browser.CanGoUp);
        Assert.Contains(browser.Entries, e => e.Kind == FileBrowserEntryKind.Drive && string.Equals(e.FullPath, driveRoot, StringComparison.OrdinalIgnoreCase));

        browser.GoBack();
        Assert.Equal(driveRoot, browser.CurrentDirectory);
    }

    [Fact]
    public void An_empty_folder_lists_nothing_without_an_error()
    {
        var browser = NewBrowser(Directory.CreateDirectory(Path.Combine(root, "empty")).FullName);
        Assert.Empty(browser.Entries);
        Assert.Null(browser.Error);
    }

    [Fact]
    public void Navigating_to_a_missing_or_invalid_folder_keeps_the_current_folder_and_explains()
    {
        var browser = NewBrowser(root);
        Assert.False(browser.NavigateTo(Path.Combine(root, "nope")));
        Assert.Equal("That folder no longer exists.", browser.Error);
        Assert.Equal(Path.GetFullPath(root), browser.CurrentDirectory);

        Assert.False(browser.NavigateTo("C:\\bad\0folder"));
        Assert.Equal("That folder path is not valid.", browser.Error);
        Assert.Equal(Path.GetFullPath(root), browser.CurrentDirectory);
    }

    [Fact]
    public void A_folder_deleted_while_open_is_reported_on_refresh_not_thrown()
    {
        var doomed = Directory.CreateDirectory(Path.Combine(root, "doomed")).FullName;
        var browser = NewBrowser(doomed);
        Directory.Delete(doomed);

        browser.Refresh();

        Assert.Equal("That folder no longer exists.", browser.Error);
        Assert.Null(browser.CurrentDirectory); // falls back to the drive list, still usable
    }

    [Fact]
    public void A_file_deleted_after_selection_is_refused_at_confirm()
    {
        var file = Touch("vanishing.fftrivia");
        var browser = NewBrowser(root);
        browser.Activate(browser.Entries.Single());
        File.Delete(file);

        Assert.Null(browser.Confirm());
        Assert.Equal("That file no longer exists.", browser.Error);
        Assert.Null(browser.SelectedPath);
        Assert.Empty(browser.Entries); // listing refreshed
    }

    [Fact]
    public void Search_narrows_the_listing_case_insensitively()
    {
        Touch("Friday Night.fftrivia"); Touch("history.fftrivia");
        var browser = NewBrowser(root);
        Assert.Equal(["Friday Night.fftrivia"], browser.Visible("friday").Select(e => e.Name));
        Assert.Equal(2, browser.Visible("  ").Count());
    }

    [Fact]
    public void Hidden_entries_are_skipped()
    {
        var hidden = Touch("hidden.fftrivia");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Touch("shown.fftrivia");
        Assert.Equal(["shown.fftrivia"], NewBrowser(root).Entries.Select(e => e.Name));
    }

    // ---------- helpers ----------

    private static FileBrowser NewBrowser(string directory)
    {
        var browser = new FileBrowser(QuestionSetFileType.PickerFilters);
        browser.Reset(directory);
        Assert.Null(browser.Error);
        return browser;
    }

    private string Touch(string nameOrPath, string content = "{}")
    {
        var path = Path.IsPathRooted(nameOrPath) ? nameOrPath : Path.Combine(root, nameOrPath);
        File.WriteAllText(path, content);
        return Path.GetFullPath(path);
    }

    private static string SerializedSet(string title)
    {
        var set = TriviaQuestionSet.CreateDraft(title) with { Questions = [new TriviaQuestion(Guid.NewGuid(), "Q?", "A", ["b", "c", "d"], null, [])] };
        return System.Text.Json.JsonSerializer.Serialize(set, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }
}
