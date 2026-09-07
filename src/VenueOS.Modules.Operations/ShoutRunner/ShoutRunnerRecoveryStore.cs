using System.Text;
using System.Text.Json;

namespace VenueOS.Modules.Operations.ShoutRunner;

/// <summary>Local-disk implementation: one JSON file, "&lt;directory&gt;/shoutrunner-active-run.json" — a single
/// current-run recovery journal, never a history log (see <see cref="ShoutRunnerRecoveryJournal"/>'s doc comment).
/// Writes are atomic (temp file + <see cref="File.Move(string,string,bool)"/> with overwrite), the same pattern
/// already proven in this codebase by <c>QuestionLibrary.FileQuestionSetRepository</c> — a crash or forced process
/// kill mid-write leaves either the old complete file or the new complete file on disk, never a half-written one,
/// since a rename/replace of an already-fully-flushed temp file is a single filesystem operation.
///
/// Deliberately takes a plain directory path rather than any Dalamud service, exactly like
/// <c>FileQuestionSetRepository</c> — the caller (<c>Plugin.cs</c>) resolves the real path via
/// <c>IDalamudPluginInterface.ConfigDirectory</c>; this class has no Dalamud dependency at all, so it is directly
/// unit-testable against a real temporary directory.</summary>
public sealed class FileShoutRunnerRecoveryStore : IShoutRunnerRecoveryStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;

    public FileShoutRunnerRecoveryStore(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "shoutrunner-active-run.json");
    }

    public ShoutRunnerRecoveryJournal? TryLoad(out bool corrupt)
    {
        corrupt = false;
        if (!File.Exists(path)) return null;

        ShoutRunnerRecoveryJournal? journal;
        try { journal = JsonSerializer.Deserialize<ShoutRunnerRecoveryJournal>(File.ReadAllText(path), Json); }
        catch (JsonException) { corrupt = true; return null; }
        catch (IOException) { corrupt = true; return null; }

        if (journal is null || journal.SchemaVersion != ShoutRunnerRecoveryJournal.CurrentSchemaVersion) { corrupt = true; return null; }
        // A journal that names a current Data Center/World not actually present in its own frozen route snapshot is
        // internally inconsistent — reject it rather than let Resume derive a nonsensical starting position.
        if (journal.CurrentDataCenter is not null && !journal.Route.DataCentersInOrder.Contains(journal.CurrentDataCenter, StringComparer.OrdinalIgnoreCase)) { corrupt = true; return null; }
        if (journal.Route.Destinations.Count == 0 || journal.Route.DataCentersInOrder.Count == 0) { corrupt = true; return null; }

        return journal;
    }

    public void Save(ShoutRunnerRecoveryJournal journal)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(journal, Json), Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }

    public void Delete()
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best-effort — a stale file left behind is not worth crashing the plugin over */ }
    }
}
