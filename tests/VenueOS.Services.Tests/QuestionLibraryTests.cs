using VenueOS.Modules.Operations.MairsEditor;
using VenueOS.Modules.Operations.QuestionLibrary;

namespace VenueOS.Services.Tests;

public sealed class QuestionSetValidatorTests
{
    private static TriviaQuestion Question(Guid id, string text = "Q?", string correct = "Right", int wrongCount = 3, int schemaVersion = 2) =>
        new(id, text, correct, Enumerable.Range(0, wrongCount).Select(i => $"Wrong{i}").ToArray(), null, []);
    private static TriviaQuestionSet Set(params TriviaQuestion[] questions) => new("fftrivia-question-set", 2, Guid.NewGuid(), "Set", "", "Author", "1.0", [], [], questions);

    [Fact] public void Empty_question_list_is_incomplete_not_invalid()
    {
        var result = QuestionSetValidator.Validate(Set());
        Assert.Equal(QuestionSetStatus.Incomplete, result.Status);
    }
    [Fact] public void Fully_authored_set_is_ready()
    {
        var result = QuestionSetValidator.Validate(Set(Question(Guid.NewGuid())));
        Assert.Equal(QuestionSetStatus.Ready, result.Status);
        Assert.Empty(result.Reasons);
    }
    [Fact] public void Blank_correct_answer_is_incomplete()
    {
        var result = QuestionSetValidator.Validate(Set(Question(Guid.NewGuid(), correct: "")));
        Assert.Equal(QuestionSetStatus.Incomplete, result.Status);
    }
    [Fact] public void Too_few_wrong_answers_is_incomplete()
    {
        var result = QuestionSetValidator.Validate(Set(Question(Guid.NewGuid(), wrongCount: 1)));
        Assert.Equal(QuestionSetStatus.Incomplete, result.Status);
    }
    [Fact] public void Duplicate_question_uuids_are_invalid()
    {
        var id = Guid.NewGuid();
        var result = QuestionSetValidator.Validate(Set(Question(id), Question(id)));
        Assert.Equal(QuestionSetStatus.Invalid, result.Status);
    }
    [Fact] public void Duplicate_answer_text_case_insensitive_is_invalid()
    {
        var question = new TriviaQuestion(Guid.NewGuid(), "Q?", "Right", ["right", "A", "B"], null, []);
        var result = QuestionSetValidator.Validate(Set(question));
        Assert.Equal(QuestionSetStatus.Invalid, result.Status);
    }
    [Fact] public void Unsupported_schema_version_is_invalid()
    {
        var result = QuestionSetValidator.Validate(Set(Question(Guid.NewGuid())) with { SchemaVersion = 3 });
        Assert.Equal(QuestionSetStatus.Invalid, result.Status);
    }
    [Fact] public void Null_incorrect_answers_does_not_throw()
    {
        var question = new TriviaQuestion(Guid.NewGuid(), "Q?", "Right", null!, null, []);
        var result = QuestionSetValidator.Validate(Set(question));
        Assert.Equal(QuestionSetStatus.Invalid, result.Status);
    }
    [Fact] public void Ready_status_is_the_only_status_IsValidHostSet_accepts()
    {
        var ready = Set(Question(Guid.NewGuid()));
        Assert.True(ready.IsValidHostSet());
        Assert.False((ready with { Title = "" }).IsValidHostSet());
    }
}

public sealed class FileQuestionSetRepositoryTests
{
    private static TriviaQuestion Q(string correct = "Right") => new(Guid.NewGuid(), "Q?", correct, ["A", "B", "C"], null, []);
    private static TriviaQuestionSet NewSet(string title = "Set") => new("fftrivia-question-set", 2, Guid.NewGuid(), title, "", "Author", "1.0", [], [], [Q()]);
    private static string TempDir() { var dir = Path.Combine(Path.GetTempPath(), "venueos-lib-" + Guid.NewGuid()); return dir; }

    [Fact] public void Save_then_read_round_trips_exactly()
    {
        var repo = new FileQuestionSetRepository(TempDir());
        var set = NewSet();
        repo.Save(set);
        var read = repo.Read(set.Id);
        Assert.NotNull(read);
        Assert.Equal(set.Id, read!.Id); Assert.Equal(set.Title, read.Title); Assert.Equal(set.Author, read.Author);
        Assert.Equal(set.Questions.Count, read.Questions.Count);
        Assert.Equal(set.Questions[0].Id, read.Questions[0].Id); Assert.Equal(set.Questions[0].CorrectAnswer, read.Questions[0].CorrectAnswer);
        Assert.Equal(set.Questions[0].IncorrectAnswers, read.Questions[0].IncorrectAnswers);
    }
    [Fact] public void List_reflects_manual_order_after_reorder()
    {
        var repo = new FileQuestionSetRepository(TempDir());
        var a = NewSet("A"); var b = NewSet("B"); var c = NewSet("C");
        repo.Save(a); repo.Save(b); repo.Save(c);
        repo.Reorder([c.Id, a.Id, b.Id]);
        Assert.Equal(["C", "A", "B"], repo.List().OrderBy(e => e.Order).Select(e => e.Title));
    }
    [Fact] public void Duplicate_mints_fresh_set_and_question_uuids()
    {
        var repo = new FileQuestionSetRepository(TempDir());
        var original = NewSet();
        repo.Save(original);
        var copy = repo.Duplicate(original.Id);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.NotEqual(original.Questions[0].Id, copy.Questions[0].Id);
        Assert.Equal("Set - Copy", copy.Title);
        Assert.NotNull(repo.Read(original.Id)); // source untouched
    }
    [Fact] public void Delete_is_blocked_while_in_use_and_does_not_remove_the_file()
    {
        var repo = new FileQuestionSetRepository(TempDir());
        var set = NewSet(); repo.Save(set);
        Assert.False(repo.Delete(set.Id, _ => true));
        Assert.NotNull(repo.Read(set.Id));
        Assert.True(repo.Delete(set.Id, _ => false));
        Assert.Null(repo.Read(set.Id));
    }
    [Fact] public void Import_collision_is_detected_by_uuid_and_replace_keeps_the_same_identity()
    {
        var repo = new FileQuestionSetRepository(TempDir());
        var original = NewSet("Original"); repo.Save(original);
        var incomingJson = System.Text.Json.JsonSerializer.Serialize(original with { Title = "Edited Elsewhere" }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var collision = repo.CheckImportCollision(incomingJson);
        Assert.NotNull(collision);
        Assert.Equal(original.Id, collision!.Id);
        var replaced = repo.ImportReplacing(incomingJson);
        Assert.Equal(original.Id, replaced.Id);
        Assert.Equal("Edited Elsewhere", repo.Read(original.Id)!.Title);
    }
    [Fact] public void Import_as_new_mints_fresh_set_and_question_identity_even_on_a_uuid_collision()
    {
        var repo = new FileQuestionSetRepository(TempDir());
        var original = NewSet("Original"); repo.Save(original);
        var incomingJson = System.Text.Json.JsonSerializer.Serialize(original, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var imported = repo.ImportAsNew(incomingJson, "A Distinct Copy");
        Assert.NotEqual(original.Id, imported.Id);
        Assert.NotEqual(original.Questions[0].Id, imported.Questions[0].Id);
        Assert.Equal("A Distinct Copy", imported.Title);
        Assert.Equal(2, repo.List().Count); // original + the new import, never merged/overwritten
    }
    [Fact] public void Rebuild_index_recovers_from_a_missing_index_file_without_touching_content()
    {
        var dir = TempDir();
        var repo = new FileQuestionSetRepository(dir);
        var set = NewSet(); repo.Save(set);
        File.Delete(Path.Combine(dir, "index.json"));
        var warnings = repo.RebuildIndex();
        Assert.Empty(warnings);
        Assert.Single(repo.List());
        var recovered = repo.Read(set.Id);
        Assert.NotNull(recovered);
        Assert.Equal(set.Title, recovered!.Title); Assert.Equal(set.Questions.Count, recovered.Questions.Count);
    }
    [Fact] public void A_corrupt_content_file_is_reported_and_left_on_disk_not_deleted()
    {
        var dir = TempDir();
        var repo = new FileQuestionSetRepository(dir);
        var badId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(dir, $"{badId:D}.json"), "{ not valid json");
        var warnings = repo.RebuildIndex();
        Assert.Single(warnings);
        Assert.True(File.Exists(Path.Combine(dir, $"{badId:D}.json")));
    }
}

public sealed class MairsEditorServiceTests
{
    private static TriviaQuestionSet NewSet(string title = "Set") => new("fftrivia-question-set", 2, Guid.NewGuid(), title, "", "Author", "1.0", [], [], []);
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "venueos-editor-" + Guid.NewGuid());

    [Fact] public void New_unsaved_set_is_dirty_and_stays_dirty_through_further_edits_until_saved()
    {
        // A brand-new set does not exist in the repository yet, so it is correctly dirty from the moment it's created.
        var editor = new MairsEditorService(new FileQuestionSetRepository(TempDir()));
        editor.NewSet("Draft");
        Assert.True(editor.IsDirty);
        editor.SetAuthor("Kei Joi");
        Assert.True(editor.IsDirty);
        editor.Save();
        Assert.False(editor.IsDirty);
    }
    [Fact] public void Save_clears_dirty_and_persists_to_the_repository()
    {
        var repo = new FileQuestionSetRepository(TempDir());
        var editor = new MairsEditorService(repo);
        editor.NewSet("Draft"); editor.SetAuthor("Kei Joi");
        var id = editor.Draft!.Id;
        editor.Save();
        Assert.False(editor.IsDirty);
        Assert.Equal("Kei Joi", repo.Read(id)!.Author);
    }
    [Fact] public void Undo_reverts_the_most_recent_mutation()
    {
        var editor = new MairsEditorService(new FileQuestionSetRepository(TempDir()));
        editor.NewSet("Draft");
        editor.SetTitle("First edit");
        editor.SetTitle("Second edit");
        Assert.True(editor.Undo());
        Assert.Equal("First edit", editor.Draft!.Title);
        Assert.True(editor.Undo());
        Assert.Equal("Draft", editor.Draft!.Title);
        Assert.False(editor.CanUndo);
    }
    [Fact] public void Save_does_not_clear_undo_history_so_undo_after_save_still_works()
    {
        var repo = new FileQuestionSetRepository(TempDir());
        var editor = new MairsEditorService(repo);
        editor.NewSet("Draft");
        editor.SetTitle("Correct Title");
        editor.Save();
        editor.SetTitle("Oops Typo");
        editor.Save(); // save again with the mistake
        Assert.True(editor.Undo()); // undoing past the most recent Save is allowed and expected
        Assert.Equal("Correct Title", editor.Draft!.Title);
        Assert.True(editor.IsDirty); // the working draft is now older than the last-saved baseline ("Oops Typo") — correctly dirty again
        editor.Save();
        Assert.Equal("Correct Title", repo.Read(editor.Draft!.Id)!.Title);
    }
    [Fact] public void Duplicating_a_question_gives_it_a_fresh_uuid_and_inserts_it_immediately_after_the_original()
    {
        var editor = new MairsEditorService(new FileQuestionSetRepository(TempDir()));
        editor.NewSet("Draft");
        editor.AddQuestion();
        var originalId = editor.Draft!.Questions[0].Id;
        editor.DuplicateQuestion(originalId);
        Assert.Equal(2, editor.Draft!.Questions.Count);
        Assert.Equal(originalId, editor.Draft.Questions[0].Id);
        Assert.NotEqual(originalId, editor.Draft.Questions[1].Id);
    }
    [Fact] public void Delete_blocked_while_in_use_reports_false_without_throwing()
    {
        var repo = new FileQuestionSetRepository(TempDir());
        var editor = new MairsEditorService(repo, isSetInUse: _ => true);
        editor.NewSet("Draft"); editor.Save();
        Assert.False(editor.DeleteSet(editor.Draft!.Id));
    }
    [Fact] public void Opening_another_set_resets_undo_history()
    {
        var repo = new FileQuestionSetRepository(TempDir());
        var editor = new MairsEditorService(repo);
        var other = NewSet("Other"); repo.Save(other);
        editor.NewSet("Draft"); editor.SetTitle("Edited");
        Assert.True(editor.CanUndo);
        editor.OpenSet(other.Id);
        Assert.False(editor.CanUndo);
        Assert.False(editor.IsDirty);
    }
}
