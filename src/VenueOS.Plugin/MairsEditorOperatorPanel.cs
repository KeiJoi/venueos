using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.MairsEditor;
using VenueOS.Modules.Operations.QuestionLibrary;
using VenueOS.Services;
using VenueOS.Plugin.Shell;
using VenueOS.Venues;

namespace VenueOS.Plugin;

/// <summary>Mair's Editor's full authoring workspace: library browse/order/create/import/export/duplicate/delete,
/// question editing with Undo, explicit Save, and Ready/Incomplete/Invalid status with detailed reasons. This is a
/// new VenueOS-native workspace — nothing here is adapted from the donor's WPF layout or tab structure.</summary>
internal sealed class MairsEditorOperatorPanel(MairsEditorService editor, VenueProfileService venues)
{
    private string search = "";
    private Guid selectedQuestionId;
    private string categoriesBuffer = "", tagsBuffer = "";
    private string importPath = "";
    private readonly ConfirmDialog confirmDialog = new();
    private readonly TextInputModal newSetModal = new();
    private readonly TextInputModal importAsNewModal = new();
    private string? importPendingJson;
    private Guid pendingOpenId;
    private string? statusMessage;

    public void Draw()
    {
        var theme = venues.Current.Theme;
        DrawLibrary(theme);
        ImGui.Spacing();
        if (editor.HasOpenDocument) DrawDocument(theme);
        else UiKit.EmptyState(theme, "No question set open", "Create a new set or select one from the library above.");
        confirmDialog.Draw(theme);
        newSetModal.Draw(theme);
        importAsNewModal.Draw(theme);
        if (statusMessage is not null) { ImGui.Spacing(); ImGui.TextWrapped(statusMessage); }
    }

    private void DrawLibrary(VenueTheme theme)
    {
        UiKit.BeginSectionCard("editor-library", theme, "Question Library");
        Forms.SearchBox(theme, "editor-search", ref search);
        if (UiKit.PrimaryButton(theme, "+ New Set")) newSetModal.Request("New question set", "Title", "e.g. Friday Night Trivia", title => { editor.NewSet(title); statusMessage = null; });
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##import-path", "Path to .fftrivia file", ref importPath, 512);
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Import")) TryImport();

        var entries = editor.Library.Where(e => search.Length == 0 || e.Title.Contains(search, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Order).ToList();
        if (entries.Count == 0) UiKit.EmptyState(theme, "Library is empty", "Create or import a question set to get started.");
        foreach (var entry in entries)
        {
            var statusLabel = entry.Status switch { QuestionSetStatus.Ready => "READY", QuestionSetStatus.Incomplete => "INCOMPLETE", _ => "INVALID" };
            var statusLevel = entry.Status switch { QuestionSetStatus.Ready => ToastLevel.Success, QuestionSetStatus.Incomplete => ToastLevel.Warning, _ => ToastLevel.Error };
            if (UiKit.ListRow(theme, entry.Title, statusLabel, false)) RequestOpen(entry.Id);
            ImGui.SameLine(); UiKit.StatusBadge(theme, statusLevel == ToastLevel.Success ? "" : statusLabel, statusLevel);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "▲##up-" + entry.Id, new(28, 0))) Reorder(entries, entry, -1);
            ImGui.SameLine();
            if (UiKit.GhostButton(theme, "▼##down-" + entry.Id, new(28, 0))) Reorder(entries, entry, 1);
        }
        UiKit.EndSectionCard();
    }

    private void Reorder(List<QuestionLibraryIndexEntry> visible, QuestionLibraryIndexEntry entry, int direction)
    {
        var all = editor.Library.OrderBy(e => e.Order).Select(e => e.Id).ToList();
        var index = all.IndexOf(entry.Id); var target = index + direction;
        if (target < 0 || target >= all.Count) return;
        (all[index], all[target]) = (all[target], all[index]);
        editor.Reorder(all);
    }

    private void RequestOpen(Guid id)
    {
        if (editor.IsDirty) { pendingOpenId = id; confirmDialog.Request("Discard unsaved changes?", "The current question set has unsaved changes. Opening another set will discard them.", () => OpenSet(pendingOpenId)); }
        else OpenSet(id);
    }
    private void OpenSet(Guid id) { editor.OpenSet(id); SyncBuffersFromDraft(); statusMessage = null; }
    private void SyncBuffersFromDraft() { categoriesBuffer = string.Join(", ", editor.Draft?.Categories ?? []); tagsBuffer = string.Join(", ", editor.Draft?.Tags ?? []); selectedQuestionId = editor.Draft?.Questions.FirstOrDefault()?.Id ?? Guid.Empty; }

    private void TryImport()
    {
        if (string.IsNullOrWhiteSpace(importPath) || !File.Exists(importPath)) { statusMessage = "Import path does not exist."; return; }
        string json;
        try { json = File.ReadAllText(importPath); } catch (IOException ex) { statusMessage = $"Could not read file: {ex.Message}"; return; }
        var collision = editor.CheckImportCollision(json);
        if (collision is null) { var imported = editor.ImportReplacing(json); statusMessage = $"Imported \"{imported.Title}\"."; return; }
        importPendingJson = json;
        confirmDialog.Request("Import collision", $"A set titled \"{collision.ExistingTitle}\" with the same identity already exists. Choose Replace Existing to overwrite it, or Import As New for a separate copy.",
            () => { var replaced = editor.ImportReplacing(importPendingJson!); statusMessage = $"Replaced \"{replaced.Title}\"."; importPendingJson = null; });
        // ConfirmDialog only offers Confirm/Cancel; Import As New is offered as a distinct action below the same message via its own modal, per Decision 4 (Replace / Import As New / Cancel are three real, distinct choices, not two).
        importAsNewModal.Request("Import as new set", "Title for the new copy", "Leave blank to keep the original title", title => { if (importPendingJson is not null) { var imported = editor.ImportAsNew(importPendingJson, string.IsNullOrWhiteSpace(title) ? null : title); statusMessage = $"Imported as new set \"{imported.Title}\"."; } importPendingJson = null; });
    }

    private void DrawDocument(VenueTheme theme)
    {
        var draft = editor.Draft!;
        var validation = editor.Validation;
        UiKit.BeginSectionCard("editor-metadata", theme, $"Editing: {draft.Title}");
        var statusLabel = validation.Status switch { QuestionSetStatus.Ready => "READY", QuestionSetStatus.Incomplete => "INCOMPLETE", _ => "INVALID" };
        UiKit.StatusBadge(theme, statusLabel, validation.Status switch { QuestionSetStatus.Ready => ToastLevel.Success, QuestionSetStatus.Incomplete => ToastLevel.Warning, _ => ToastLevel.Error });
        if (editor.IsDirty) { ImGui.SameLine(); UiKit.StatusBadge(theme, "Unsaved changes", ToastLevel.Warning); }
        foreach (var reason in validation.Reasons.Take(6)) { ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary)); ImGui.TextWrapped("• " + reason); ImGui.PopStyleColor(); }

        var title = draft.Title; if (Forms.TextField(theme, "Title", ref title, 256)) editor.SetTitle(title);
        var description = draft.Description; if (Forms.MultilineField(theme, "Description", ref description, 2048)) editor.SetDescription(description);
        var author = draft.Author; if (Forms.TextField(theme, "Author", ref author, 256)) editor.SetAuthor(author);
        var version = draft.Version; if (Forms.TextField(theme, "Version", ref version, 64)) editor.SetVersion(version);
        if (Forms.TextField(theme, "Categories (comma-separated)", ref categoriesBuffer, 1024)) editor.SetCategories(ParseList(categoriesBuffer));
        if (Forms.TextField(theme, "Tags (comma-separated)", ref tagsBuffer, 1024)) editor.SetTags(ParseList(tagsBuffer));
        if (draft.SchemaVersion == 1) { ImGui.Spacing(); if (UiKit.GhostButton(theme, "Upgrade to schema v2 (allow 3–9 wrong answers)")) editor.UpgradeSchema(); }

        ImGui.Spacing();
        if (UiKit.PrimaryButton(theme, "Save")) { editor.Save(); statusMessage = "Saved."; }
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, editor.CanUndo ? "Undo" : "Undo (nothing to undo)") && editor.CanUndo) { editor.Undo(); SyncBuffersFromDraft(); }
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Duplicate Set")) { var copy = editor.DuplicateSet(draft.Id); statusMessage = $"Duplicated as \"{copy.Title}\"."; }
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Export")) { try { var target = Path.Combine(Path.GetTempPath(), $"{Sanitize(draft.Title)}.fftrivia"); File.WriteAllText(target, editor.Export(draft.Id)); statusMessage = $"Exported to {target}."; } catch (IOException ex) { statusMessage = $"Export failed: {ex.Message}"; } }
        ImGui.SameLine();
        if (UiKit.DangerButton(theme, "Delete Set")) RequestDelete(draft.Id);
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Close")) RequestClose();
        UiKit.EndSectionCard();

        ImGui.Spacing();
        DrawQuestions(theme, draft);
    }

    private void RequestDelete(Guid id)
    {
        if (editor.IsSetInUse(id)) { statusMessage = "This set is in use by an active or resumable Trivia game and cannot be deleted. Editing remains available — active games use an immutable snapshot."; return; }
        confirmDialog.Request("Delete question set?", "This permanently deletes the set from the library. This cannot be undone.", () => { if (editor.DeleteSet(id)) { editor.CloseDocument(); statusMessage = "Deleted."; } else statusMessage = "This set is in use and cannot be deleted."; });
    }
    private void RequestClose()
    {
        if (editor.IsDirty) confirmDialog.Request("Discard unsaved changes?", "Closing now will discard unsaved changes to this question set.", () => { editor.CloseDocument(); statusMessage = null; });
        else editor.CloseDocument();
    }

    private void DrawQuestions(VenueTheme theme, TriviaQuestionSet draft)
    {
        UiKit.BeginSectionCard("editor-questions", theme, $"Questions ({draft.Questions.Count})");
        if (UiKit.PrimaryButton(theme, "+ Add Question")) { editor.AddQuestion(); selectedQuestionId = editor.Draft!.Questions[^1].Id; }
        if (draft.Questions.Count == 0) UiKit.EmptyState(theme, "No questions yet", "Add one above.");
        foreach (var question in draft.Questions)
        {
            var label = string.IsNullOrWhiteSpace(question.Question) ? "(untitled question)" : question.Question;
            if (UiKit.ListRow(theme, label, $"{question.IncorrectAnswers.Count} wrong answers", question.Id == selectedQuestionId)) selectedQuestionId = question.Id;
        }
        UiKit.EndSectionCard();

        var selected = draft.Questions.FirstOrDefault(q => q.Id == selectedQuestionId);
        if (selected is null) return;
        ImGui.Spacing();
        UiKit.BeginSectionCard("editor-question-detail", theme, "Question");
        var text = selected.Question; if (Forms.MultilineField(theme, "Question text", ref text, 4096)) editor.UpdateQuestion(selected.Id, q => q with { Question = text });
        var correct = selected.CorrectAnswer; if (Forms.TextField(theme, "Correct answer", ref correct, 1024)) editor.UpdateQuestion(selected.Id, q => q with { CorrectAnswer = correct });
        var category = selected.Category ?? ""; if (Forms.TextField(theme, "Category (optional)", ref category, 256)) editor.UpdateQuestion(selected.Id, q => q with { Category = string.IsNullOrWhiteSpace(category) ? null : category });
        var tagsText = string.Join(", ", selected.Tags); if (Forms.TextField(theme, "Tags (comma-separated)", ref tagsText, 1024)) editor.UpdateQuestion(selected.Id, q => q with { Tags = ParseList(tagsText) });

        ImGui.Spacing(); ImGui.TextUnformatted($"Wrong answers ({selected.IncorrectAnswers.Count} / 3–9):");
        var minimum = draft.SchemaVersion == 1 ? 9 : 3;
        for (var i = 0; i < selected.IncorrectAnswers.Count; i++)
        {
            var answer = selected.IncorrectAnswers[i]; var index = i;
            if (Forms.TextField(theme, $"Wrong answer {index + 1}", ref answer, 1024)) editor.UpdateQuestion(selected.Id, q => { var list = q.IncorrectAnswers.ToList(); list[index] = answer; return q with { IncorrectAnswers = list }; });
        }
        if (selected.IncorrectAnswers.Count < 9 && UiKit.GhostButton(theme, "+ Add wrong answer")) editor.UpdateQuestion(selected.Id, q => q with { IncorrectAnswers = [.. q.IncorrectAnswers, ""] });
        ImGui.SameLine();
        if (selected.IncorrectAnswers.Count > minimum && UiKit.GhostButton(theme, "Remove last")) editor.UpdateQuestion(selected.Id, q => q with { IncorrectAnswers = q.IncorrectAnswers.Take(q.IncorrectAnswers.Count - 1).ToList() });

        ImGui.Spacing();
        if (UiKit.GhostButton(theme, "Duplicate Question")) editor.DuplicateQuestion(selected.Id);
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Move Up")) editor.MoveQuestion(selected.Id, -1);
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Move Down")) editor.MoveQuestion(selected.Id, 1);
        ImGui.SameLine();
        if (UiKit.DangerButton(theme, "Delete Question")) { editor.DeleteQuestion(selected.Id); selectedQuestionId = editor.Draft?.Questions.FirstOrDefault()?.Id ?? Guid.Empty; }
        UiKit.EndSectionCard();
    }

    private static IReadOnlyList<string> ParseList(string commaSeparated) => [.. commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
    private static string Sanitize(string title) => string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
