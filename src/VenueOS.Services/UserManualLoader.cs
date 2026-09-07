namespace VenueOS.Services;

/// <summary>One directory this attempt tried, and exactly what happened there — enough for a Diagnostics entry to
/// explain a load failure without the caller re-deriving anything. <see cref="FailureReason"/> is null only on the
/// candidate that actually succeeded.</summary>
public sealed record UserManualCandidateResult(string Directory, bool DirectoryExists, bool FileExists, string? FailureReason);

public sealed record UserManualLoadResult(string? Markdown, IReadOnlyList<UserManualCandidateResult> Candidates);

/// <summary>Loads the bundled offline copy of <c>docs/USER_MANUAL.md</c> from beside the installed plugin — never
/// the source repository, never the network. <see cref="Load"/> never throws: any failure (no candidate directory
/// has the file, it's unreadable, or it's empty) returns a null <see cref="UserManualLoadResult.Markdown"/> plus the
/// full list of what was tried, and the caller renders a plain "could not be loaded" state rather than crashing
/// VenueOS. The single authoritative manual text lives only in the repository's <c>docs/USER_MANUAL.md</c>;
/// <c>VenueOS.Plugin.csproj</c> copies that exact file into the build/package output as <see cref="FileName"/> via an
/// Include+Link content item, so there is never a second, independently maintained copy of the manual anywhere.
///
/// <b>Why this takes a caller-supplied, ordered list of candidate directories instead of guessing one:</b> a live
/// installed Dalamud plugin's <c>Assembly.Location</c> is not reliable — Dalamud loads plugin assemblies in a way
/// that can leave it empty or pointing somewhere other than the actual installed plugin folder the manual was
/// copied into. The correct, officially-documented directory is <c>IDalamudPluginInterface.AssemblyLocation</c>
/// (backed by Dalamud's own tracked <c>DllFile</c> path for the plugin) — but that type lives in the Dalamud SDK,
/// which this project deliberately never references, so the composition root (<c>Plugin.cs</c>) resolves the real
/// candidate directories and hands them to this pure, testable method in priority order. No candidate here is ever
/// derived by searching/scanning — each one is a single, specific, justified directory the caller already knows
/// about.</summary>
public static class UserManualLoader
{
    public const string FileName = "USER_MANUAL.md";

    public static UserManualLoadResult Load(IReadOnlyList<string?> candidateDirectories)
    {
        var results = new List<UserManualCandidateResult>(candidateDirectories.Count);
        foreach (var directory in candidateDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory)) { results.Add(new(directory ?? "(none)", false, false, "No path was supplied for this candidate.")); continue; }

            bool directoryExists;
            try { directoryExists = Directory.Exists(directory); } catch { directoryExists = false; }
            if (!directoryExists) { results.Add(new(directory, false, false, "Directory does not exist.")); continue; }

            var path = Path.Combine(directory, FileName);
            bool fileExists;
            try { fileExists = File.Exists(path); } catch { fileExists = false; }
            if (!fileExists) { results.Add(new(directory, true, false, $"{FileName} was not found in this directory.")); continue; }

            try
            {
                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) { results.Add(new(directory, true, true, "The file exists but is empty.")); continue; }
                results.Add(new(directory, true, true, null));
                return new UserManualLoadResult(text, results);
            }
            catch (IOException ex) { results.Add(new(directory, true, true, $"Could not read the file: {ex.Message}")); }
            catch (UnauthorizedAccessException ex) { results.Add(new(directory, true, true, $"Could not read the file: {ex.Message}")); }
        }
        return new UserManualLoadResult(null, results);
    }
}
