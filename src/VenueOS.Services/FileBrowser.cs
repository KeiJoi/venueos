namespace VenueOS.Services;

/// <summary>One selectable file type in <see cref="FileBrowser"/> — e.g. "Mair's Trivia Question Sets (*.fftrivia)".
/// An empty <paramref name="Extensions"/> list means "All Files".</summary>
public sealed record FileBrowserFilter(string Description, IReadOnlyList<string> Extensions)
{
    public static FileBrowserFilter AllFiles { get; } = new("All Files (*.*)", []);

    public bool Matches(string fileName) =>
        Extensions.Count == 0 || Extensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
}

public enum FileBrowserEntryKind { Drive, Directory, File }

public sealed record FileBrowserEntry(string Name, string FullPath, FileBrowserEntryKind Kind);

/// <summary>A shortcut shown beside the listing (Desktop, Documents, Downloads, …).</summary>
public sealed record FileBrowserPlace(string Name, string FullPath);

/// <summary>What the operator did with a picker: <see cref="Confirmed"/> with an absolute <see cref="Path"/>, or
/// cancelled. <see cref="ApplyTo"/> is the single rule a caller's path field follows — a cancel leaves it untouched.</summary>
public sealed record FilePickerResult(bool Confirmed, string? Path)
{
    public static FilePickerResult Cancelled { get; } = new(false, null);
    public static FilePickerResult Selected(string absolutePath) => new(true, absolutePath);
    public string ApplyTo(string currentPath) => Confirmed && Path is not null ? Path : currentPath;
}

/// <summary>
/// The ImGui-free state behind VenueOS's file picker (<c>Shell/FilePickerModal</c>): current folder, its listing,
/// Back/Up navigation, the active file-type filter, the selected file, and a user-facing error string. Every
/// filesystem call is guarded — an inaccessible, deleted or invalid folder/file produces <see cref="Error"/> text and
/// leaves the browser in a usable state; nothing here throws for a normal filesystem condition.
///
/// <see cref="CurrentDirectory"/> == null is the top-level "This PC" view listing drive roots, reached by going Up
/// from a drive root. Hidden and system entries are skipped. The browser only ever reads the filesystem — it never
/// creates, copies, renames or modifies anything.
/// </summary>
public sealed class FileBrowser
{
    private static readonly EnumerationOptions ListingOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        RecurseSubdirectories = false,
    };

    private readonly Stack<string?> backHistory = new();
    private List<FileBrowserEntry> entries = [];

    public FileBrowser(IReadOnlyList<FileBrowserFilter> filters)
    {
        if (filters.Count == 0) throw new ArgumentException("At least one filter is required.", nameof(filters));
        Filters = filters;
    }

    public IReadOnlyList<FileBrowserFilter> Filters { get; }
    public int FilterIndex { get; private set; }
    public FileBrowserFilter ActiveFilter => Filters[FilterIndex];

    /// <summary>Absolute folder being shown, or null for the drive list.</summary>
    public string? CurrentDirectory { get; private set; }
    public IReadOnlyList<FileBrowserEntry> Entries => entries;
    public string? SelectedPath { get; private set; }
    public string? Error { get; private set; }
    public bool CanGoBack => backHistory.Count > 0;
    public bool CanGoUp => CurrentDirectory is not null;

    /// <summary>Starts a fresh browsing session at <paramref name="directory"/> (drive list if null or unusable).
    /// Clears history, selection and error, and resets the filter to the first (primary) one.</summary>
    public void Reset(string? directory)
    {
        backHistory.Clear();
        SelectedPath = null;
        Error = null;
        FilterIndex = 0;
        if (directory is null || !TryLoad(directory)) { var error = Error; LoadDrives(); Error = error ?? Error; }
    }

    public void SetFilter(int index)
    {
        if (index < 0 || index >= Filters.Count || index == FilterIndex) return;
        FilterIndex = index;
        if (SelectedPath is not null && !ActiveFilter.Matches(Path.GetFileName(SelectedPath))) SelectedPath = null;
        Refresh();
    }

    /// <summary>Opens a folder (or a drive root). On failure stays where it was and sets <see cref="Error"/>.</summary>
    public bool NavigateTo(string directory)
    {
        var previous = CurrentDirectory;
        if (!TryLoad(directory)) return false;
        backHistory.Push(previous);
        SelectedPath = null;
        return true;
    }

    /// <summary>The top-level "This PC" drive list (keeps Back history).</summary>
    public void ShowDrives()
    {
        if (CurrentDirectory is null) return;
        backHistory.Push(CurrentDirectory);
        LoadDrives();
        SelectedPath = null;
    }

    public void GoUp()
    {
        if (CurrentDirectory is null) return;
        string? parent;
        try { parent = Directory.GetParent(CurrentDirectory)?.FullName; }
        catch (Exception ex) when (IsFilesystemException(ex)) { parent = null; }

        var previous = CurrentDirectory;
        if (parent is null) { LoadDrives(); backHistory.Push(previous); SelectedPath = null; return; }
        if (!NavigateTo(parent)) { var error = Error; LoadDrives(); Error = error ?? Error; backHistory.Push(previous); SelectedPath = null; }
    }

    public void GoBack()
    {
        while (backHistory.Count > 0)
        {
            var target = backHistory.Pop();
            if (target is null) { LoadDrives(); SelectedPath = null; return; }
            if (TryLoad(target)) { SelectedPath = null; return; }
            // A folder in history was deleted/became inaccessible — keep unwinding; TryLoad already set Error.
        }
    }

    /// <summary>Re-reads the current folder (e.g. after an error, or a filter change).</summary>
    public void Refresh()
    {
        if (CurrentDirectory is null) LoadDrives();
        else if (!TryLoad(CurrentDirectory)) { var error = Error; LoadDrives(); Error = error ?? Error; }
    }

    /// <summary>A single click: a folder/drive opens; a file becomes the selection.</summary>
    public void Activate(FileBrowserEntry entry)
    {
        if (entry.Kind == FileBrowserEntryKind.File) { SelectedPath = entry.FullPath; Error = null; }
        else NavigateTo(entry.FullPath);
    }

    /// <summary>Confirms the current selection. Returns the absolute path only when it is still an existing file that
    /// matches the active filter; otherwise returns null and explains why in <see cref="Error"/>. Never opens or reads
    /// the file — whatever the caller does with the path (e.g. Mair's Editor's Import) keeps its own validation.</summary>
    public string? Confirm()
    {
        if (SelectedPath is null) { Error = "Select a file first."; return null; }
        string full;
        try { full = Path.GetFullPath(SelectedPath); }
        catch (Exception ex) when (IsFilesystemException(ex)) { Error = "That file path is not valid."; SelectedPath = null; return null; }
        if (!File.Exists(full)) { SelectedPath = null; Refresh(); Error = "That file no longer exists."; return null; }
        if (!ActiveFilter.Matches(Path.GetFileName(full))) { Error = $"Choose a file matching {ActiveFilter.Description}."; return null; }
        Error = null;
        return full;
    }

    /// <summary>Visible entries, optionally narrowed by a case-insensitive name search.</summary>
    public IEnumerable<FileBrowserEntry> Visible(string? search) =>
        string.IsNullOrWhiteSpace(search) ? entries : entries.Where(e => e.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Desktop, Documents, Downloads and the user folder — only those that actually exist.</summary>
    public static IReadOnlyList<FileBrowserPlace> CommonPlaces()
    {
        var places = new List<FileBrowserPlace>();
        void Add(string name, string? path)
        {
            try { if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && places.All(p => !SamePath(p.FullPath, path))) places.Add(new(name, Path.GetFullPath(path))); }
            catch (Exception ex) when (IsFilesystemException(ex)) { }
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Downloads", string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads"));
        Add("User Folder", profile);
        return places;
    }

    /// <summary>The picker's starting folder: the folder of <paramref name="currentPathText"/> when that is an existing
    /// file matching <paramref name="preferredFilter"/>; else <paramref name="lastDirectory"/> if it still exists; else
    /// <paramref name="fallbackDirectory"/> if it exists; else null (the drive list). Never throws.</summary>
    public static string? ResolveInitialDirectory(string? currentPathText, FileBrowserFilter preferredFilter, string? lastDirectory, string? fallbackDirectory)
    {
        try
        {
            var text = currentPathText?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                var full = Path.GetFullPath(text);
                if (File.Exists(full) && preferredFilter.Matches(Path.GetFileName(full)))
                {
                    var dir = Path.GetDirectoryName(full);
                    if (dir is not null && Directory.Exists(dir)) return dir;
                }
            }
        }
        catch (Exception ex) when (IsFilesystemException(ex)) { }

        if (ExistingDirectory(lastDirectory) is { } last) return last;
        return ExistingDirectory(fallbackDirectory);
    }

    private static string? ExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { var full = Path.GetFullPath(path); return Directory.Exists(full) ? full : null; }
        catch (Exception ex) when (IsFilesystemException(ex)) { return null; }
    }

    private bool TryLoad(string directory)
    {
        string full;
        try { full = Path.GetFullPath(directory); }
        catch (Exception ex) when (IsFilesystemException(ex)) { Error = "That folder path is not valid."; return false; }

        try
        {
            if (!Directory.Exists(full)) { Error = "That folder no longer exists."; return false; }
            var info = new DirectoryInfo(full);
            var folders = info.EnumerateDirectories("*", ListingOptions)
                .Select(d => new FileBrowserEntry(d.Name, d.FullName, FileBrowserEntryKind.Directory))
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);
            var files = info.EnumerateFiles("*", ListingOptions)
                .Where(f => ActiveFilter.Matches(f.Name))
                .Select(f => new FileBrowserEntry(f.Name, f.FullName, FileBrowserEntryKind.File))
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);
            entries = [.. folders, .. files];
            CurrentDirectory = info.FullName;
            Error = null;
            return true;
        }
        catch (Exception ex) when (IsFilesystemException(ex))
        {
            Error = ex is UnauthorizedAccessException or System.Security.SecurityException
                ? "You don't have permission to open that folder."
                : "That folder could not be opened.";
            return false;
        }
    }

    private void LoadDrives()
    {
        Error = null; // callers that must keep a preceding error (Reset/Refresh fallback) restore it themselves
        var drives = new List<FileBrowserEntry>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\', '/')})";
                    drives.Add(new FileBrowserEntry(label, drive.RootDirectory.FullName, FileBrowserEntryKind.Drive));
                }
                catch (Exception ex) when (IsFilesystemException(ex)) { }
            }
        }
        catch (Exception ex) when (IsFilesystemException(ex)) { Error = "The list of drives could not be read."; }

        entries = drives;
        CurrentDirectory = null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsFilesystemException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;
}
