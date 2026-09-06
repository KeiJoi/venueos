using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VenueOS.Modules.Operations.QuestionLibrary;

/// <summary>
/// Local-disk implementation: one UTF-8 JSON file per set at "&lt;root&gt;/&lt;uuid&gt;.json" (the exact .fftrivia
/// shape, so Export is a byte-identical copy of the stored content and Import is a plain read), plus one
/// "index.json" holding only navigation metadata (id, title, manual order, content hash, updated-at, status) —
/// never a second copy of question content. Writes are atomic (temp file + File.Move with overwrite). If the index
/// is missing or unreadable it is rebuilt by rescanning "*.json" content files; a content file that fails to parse
/// is reported as a warning and left on disk untouched, never deleted.
/// </summary>
public sealed class FileQuestionSetRepository : IQuestionSetRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string root;
    private readonly string indexPath;
    private readonly object gate = new();

    public FileQuestionSetRepository(string rootDirectory)
    {
        root = rootDirectory;
        indexPath = Path.Combine(root, "index.json");
        Directory.CreateDirectory(root);
        if (!TryLoadIndex(out _)) RebuildIndex();
    }

    private string ContentPath(Guid id) => Path.Combine(root, $"{id:D}.json");

    public IReadOnlyList<QuestionLibraryIndexEntry> List()
    {
        lock (gate) { return TryLoadIndex(out var entries) ? [.. entries.OrderBy(e => e.Order)] : []; }
    }

    public TriviaQuestionSet? Read(Guid id)
    {
        lock (gate)
        {
            var path = ContentPath(id);
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<TriviaQuestionSet>(File.ReadAllText(path), Json); }
            catch (JsonException) { return null; }
        }
    }

    public void Save(TriviaQuestionSet set)
    {
        lock (gate)
        {
            WriteContentAtomic(set);
            var entries = TryLoadIndex(out var existing) ? existing.ToList() : [];
            var order = entries.FirstOrDefault(e => e.Id == set.Id)?.Order ?? (entries.Count == 0 ? 0 : entries.Max(e => e.Order) + 1);
            entries.RemoveAll(e => e.Id == set.Id);
            entries.Add(BuildEntry(set, order));
            WriteIndexAtomic(entries);
        }
    }

    public bool Delete(Guid id, Func<Guid, bool> isInUse)
    {
        lock (gate)
        {
            if (isInUse(id)) return false;
            var path = ContentPath(id);
            if (File.Exists(path)) File.Delete(path);
            if (TryLoadIndex(out var entries)) WriteIndexAtomic(entries.Where(e => e.Id != id).ToList());
            return true;
        }
    }

    public TriviaQuestionSet Duplicate(Guid id, string? title = null)
    {
        var source = Read(id) ?? throw new InvalidOperationException($"Question set {id} does not exist.");
        var copy = source.Duplicate(title);
        Save(copy);
        return copy;
    }

    public ImportCollisionInfo? CheckImportCollision(string fftriviaJson)
    {
        var incoming = Parse(fftriviaJson);
        var existing = Read(incoming.Id);
        return existing is null ? null : new ImportCollisionInfo(incoming.Id, existing.Title, incoming.Title);
    }

    public TriviaQuestionSet ImportReplacing(string fftriviaJson)
    {
        var set = Parse(fftriviaJson);
        Save(set);
        return set;
    }

    public TriviaQuestionSet ImportAsNew(string fftriviaJson, string? title = null)
    {
        var set = Parse(fftriviaJson);
        // A UUID collision is an identity collision, not a text one — renaming alone never resolves it. Import As New always mints fresh identity for the set AND every question.
        var fresh = set with { Id = Guid.NewGuid(), Title = title ?? set.Title, Questions = [.. set.Questions.Select(q => q.Duplicate())] };
        Save(fresh);
        return fresh;
    }

    public string Export(Guid id)
    {
        var path = ContentPath(id);
        if (!File.Exists(path)) throw new InvalidOperationException($"Question set {id} does not exist.");
        return File.ReadAllText(path);
    }

    public void Reorder(IReadOnlyList<Guid> orderedIdsInDesiredOrder)
    {
        lock (gate)
        {
            if (!TryLoadIndex(out var entries)) return;
            var byId = entries.ToDictionary(e => e.Id);
            var reordered = new List<QuestionLibraryIndexEntry>();
            for (var i = 0; i < orderedIdsInDesiredOrder.Count; i++)
                if (byId.TryGetValue(orderedIdsInDesiredOrder[i], out var entry)) reordered.Add(entry with { Order = i });
            // Anything not named in the caller's list keeps its relative order, appended after the explicitly ordered set — no set is ever silently dropped from the index by a partial reorder call.
            var remaining = entries.Where(e => !orderedIdsInDesiredOrder.Contains(e.Id)).OrderBy(e => e.Order);
            var next = reordered.Count;
            foreach (var entry in remaining) reordered.Add(entry with { Order = next++ });
            WriteIndexAtomic(reordered);
        }
    }

    public IReadOnlyList<string> RebuildIndex()
    {
        lock (gate)
        {
            var warnings = new List<string>();
            var entries = new List<QuestionLibraryIndexEntry>();
            var order = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*.json").Where(f => !string.Equals(Path.GetFileName(f), "index.json", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var set = JsonSerializer.Deserialize<TriviaQuestionSet>(File.ReadAllText(file), Json);
                    if (set is null) { warnings.Add($"{Path.GetFileName(file)}: file is empty or not a question set."); continue; }
                    entries.Add(BuildEntry(set, order++));
                }
                catch (JsonException ex) { warnings.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
            }
            WriteIndexAtomic(entries);
            return warnings;
        }
    }

    private static TriviaQuestionSet Parse(string fftriviaJson)
    {
        try { return JsonSerializer.Deserialize<TriviaQuestionSet>(fftriviaJson, Json) ?? throw new InvalidOperationException("The file is not a question set."); }
        catch (JsonException) { throw new InvalidOperationException("The file is not valid JSON."); }
    }

    private static QuestionLibraryIndexEntry BuildEntry(TriviaQuestionSet set, int order) => new(set.Id, set.Title, order, ContentHash(set), DateTimeOffset.UtcNow, QuestionSetValidator.Validate(set).Status);

    private static string ContentHash(TriviaQuestionSet set)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(set, Json);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private bool TryLoadIndex(out List<QuestionLibraryIndexEntry> entries)
    {
        entries = [];
        if (!File.Exists(indexPath)) return false;
        try { entries = JsonSerializer.Deserialize<List<QuestionLibraryIndexEntry>>(File.ReadAllText(indexPath), Json) ?? []; return true; }
        catch (JsonException) { return false; }
    }

    private void WriteContentAtomic(TriviaQuestionSet set) => WriteAtomic(ContentPath(set.Id), JsonSerializer.Serialize(set, Json));
    private void WriteIndexAtomic(List<QuestionLibraryIndexEntry> entries) => WriteAtomic(indexPath, JsonSerializer.Serialize(entries, Json));

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }
}
