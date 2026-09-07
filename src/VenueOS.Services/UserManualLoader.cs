namespace VenueOS.Services;

/// <summary>Loads the bundled offline copy of <c>docs/USER_MANUAL.md</c> from beside the running plugin assembly —
/// never the source repository, never the network. <see cref="Load"/> never throws: any failure (file missing,
/// unreadable, empty) returns null, and the caller renders a plain "could not be loaded" state rather than crashing
/// VenueOS. The single authoritative manual text lives only in the repository's <c>docs/USER_MANUAL.md</c>;
/// <c>VenueOS.Plugin.csproj</c> copies that exact file into the build/package output as <see cref="FileName"/> via an
/// Include+Link content item, so there is never a second, independently maintained copy of the manual anywhere.</summary>
public static class UserManualLoader
{
    public const string FileName = "USER_MANUAL.md";

    public static string? Load(string pluginDirectory)
    {
        try
        {
            var path = Path.Combine(pluginDirectory, FileName);
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
