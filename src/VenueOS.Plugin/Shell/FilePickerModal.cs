using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>
/// VenueOS's own file-open picker — a themed modal built from the shared kit (<see cref="DialogHeader"/>,
/// <see cref="UiKit"/>, <see cref="Forms"/>, <see cref="AppIcons"/>) over the ImGui-free <see cref="FileBrowser"/>
/// state. Deliberately not Dalamud's <c>FileDialogManager</c> (Raffle's xlsx import/export uses that): its window is
/// stock ImGui chrome, and VenueOS UI must not look like a raw ImGui tool window (NEW_MODULE_GUIDE.md §15).
///
/// Browse only: places (Desktop/Documents/Downloads/drives), folder navigation with Back/Up, a file-type filter
/// (the caller's primary filter first), an in-folder search, Select/Cancel. It never reads, copies or modifies the
/// chosen file — it reports an absolute path through the <see cref="FilePickerResult"/> callback and the caller
/// decides what to do. Cancel (or the header's Close) reports <see cref="FilePickerResult.Cancelled"/>.
/// One instance per owning panel; the popup identity is per-instance, like <see cref="ConfirmDialog"/>.
/// </summary>
internal sealed class FilePickerModal
{
    private const float FooterHeight = 104f;
    private const float PlacesWidth = 170f;

    private readonly string popupId = $"Select File##venueos-file-picker-{Guid.NewGuid():N}";
    private FileBrowser browser = new([FileBrowserFilter.AllFiles]);
    private IReadOnlyList<FileBrowserPlace> places = [];
    private string title = "Select File";
    private string search = "";
    private string? searchDirectory;
    private Action<FilePickerResult>? onClose;
    private bool openRequested;
    private bool windowOpen = true;

    public void Request(string title, IReadOnlyList<FileBrowserFilter> filters, string? initialDirectory, Action<FilePickerResult> onClose)
    {
        this.title = title;
        this.onClose = onClose;
        browser = new FileBrowser(filters);
        browser.Reset(initialDirectory);
        places = FileBrowser.CommonPlaces();
        search = "";
        searchDirectory = browser.CurrentDirectory;
        openRequested = true;
        windowOpen = true;
    }

    public void Draw(VenueTheme theme)
    {
        if (openRequested) { ImGui.OpenPopup(popupId); openRequested = false; }

        ImGui.SetNextWindowSize(new Vector2(720, 520), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(560, 400), new Vector2(float.MaxValue, float.MaxValue));
        // The modal's own background/rounding come from the venue theme, not ImGui's default popup style.
        ImGui.PushStyleColor(ImGuiCol.PopupBg, UiKit.Color(theme.Tokens.Background));
        ImGui.PushStyleColor(ImGuiCol.Border, UiKit.Color(theme.Tokens.Border));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, theme.Metrics.Rounding + 4);
        var visible = ImGui.BeginPopupModal(popupId, ref windowOpen, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
        if (!visible) return;

        UiKit.DrawVenueFrame(theme);
        DialogHeader.Draw(theme, title, () => Finish(FilePickerResult.Cancelled));
        if (onClose is null) { ImGui.EndPopup(); return; } // closed by the header this frame

        if (searchDirectory != browser.CurrentDirectory) { search = ""; searchDirectory = browser.CurrentDirectory; }

        DrawToolbar(theme);
        ImGui.Spacing();

        var bodyHeight = MathF.Max(120f, ImGui.GetContentRegionAvail().Y - FooterHeight);
        ImGui.BeginChild("file-picker-places", new Vector2(PlacesWidth, bodyHeight), true);
        DrawPlaces(theme);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("file-picker-listing", new Vector2(0, bodyHeight), true);
        DrawListing(theme);
        ImGui.EndChild();

        DrawFooter(theme);
        ImGui.EndPopup();
    }

    private void DrawToolbar(VenueTheme theme)
    {
        var buttonSize = ImGui.GetFrameHeight() + 4;
        ImGui.BeginDisabled(!browser.CanGoBack);
        if (UiKit.IconButton(theme, "file-picker-back", "arrow-left", buttonSize, "Back")) browser.GoBack();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!browser.CanGoUp);
        if (UiKit.IconButton(theme, "file-picker-up", "chevron-up", buttonSize, "Up one folder")) browser.GoUp();
        ImGui.EndDisabled();
        ImGui.SameLine();

        // Read-only, but selectable/copyable — the operator can see (and copy) exactly where they are.
        var location = browser.CurrentDirectory ?? "This PC";
        ImGui.SetNextItemWidth(-1);
        Forms.PushFieldStyle(theme);
        ImGui.InputText("##file-picker-location", ref location, 4096, ImGuiInputTextFlags.ReadOnly);
        Forms.PopFieldStyle();
    }

    private void DrawPlaces(VenueTheme theme)
    {
        UiKit.SectionHeader(theme, "Places");
        foreach (var place in places)
        {
            var current = browser.CurrentDirectory is not null && string.Equals(Path.TrimEndingDirectorySeparator(place.FullPath), Path.TrimEndingDirectorySeparator(browser.CurrentDirectory), StringComparison.OrdinalIgnoreCase);
            if (UiKit.ListRow(theme, place.Name, null, current)) browser.NavigateTo(place.FullPath);
        }
        if (UiKit.ListRow(theme, "This PC", null, browser.CurrentDirectory is null)) browser.ShowDrives();
    }

    private void DrawListing(VenueTheme theme)
    {
        Forms.SearchBox(theme, "file-picker-search", ref search, browser.CurrentDirectory is null ? "Search drives" : "Search this folder");
        ImGui.Spacing();

        var visible = browser.Visible(search).ToList();
        if (visible.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(search)) UiKit.EmptyState(theme, "Nothing matches your search", "Clear the search box to see everything in this folder.");
            else if (browser.CurrentDirectory is null) UiKit.EmptyState(theme, "No drives available", "No readable drives were found.");
            else UiKit.EmptyState(theme, "Nothing to show here", $"This folder has no subfolders or {browser.ActiveFilter.Description} files.");
            return;
        }

        var rowHeight = ImGui.GetTextLineHeight() + 12;
        for (var i = 0; i < visible.Count; i++)
        {
            var entry = visible[i];
            var selected = entry.Kind == FileBrowserEntryKind.File && string.Equals(entry.FullPath, browser.SelectedPath, StringComparison.OrdinalIgnoreCase);
            ImGui.PushID(i);
            ImGui.PushStyleColor(ImGuiCol.Header, UiKit.Color(theme.Tokens.Selected));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, UiKit.Color(theme.Tokens.RaisedSurface));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, UiKit.Color(theme.Tokens.Selected));
            var clicked = ImGui.Selectable("##file-picker-entry", selected, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(0, rowHeight));
            ImGui.PopStyleColor(3);
            var doubleClicked = clicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);

            var min = ImGui.GetItemRectMin();
            var drawList = ImGui.GetWindowDrawList();
            var iconColor = entry.Kind == FileBrowserEntryKind.File ? theme.Tokens.TextPrimary : theme.Tokens.Accent;
            AppIcons.Draw(entry.Kind == FileBrowserEntryKind.File ? "file" : "folder", min + new Vector2(14, rowHeight / 2), 9, UiKit.ColorU32(iconColor), 1.5f);
            drawList.AddText(min + new Vector2(32, (rowHeight - ImGui.GetTextLineHeight()) / 2), UiKit.ColorU32(theme.Tokens.TextPrimary), entry.Name);
            if (ImGui.IsItemHovered() && ImGui.CalcTextSize(entry.Name).X + 40 > ImGui.GetItemRectSize().X) ImGui.SetTooltip(entry.Name);
            ImGui.PopID();

            if (!clicked) continue;
            browser.Activate(entry);
            if (entry.Kind != FileBrowserEntryKind.File) break; // the listing was replaced — stop iterating the old one
            if (doubleClicked) TryConfirm();
            break;
        }
    }

    private void DrawFooter(VenueTheme theme)
    {
        ImGui.Spacing();
        if (browser.Error is { } error) UiKit.ErrorState(theme, error);
        var selectedName = browser.SelectedPath is null ? null : Path.GetFileName(browser.SelectedPath);
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(selectedName is null ? theme.Tokens.TextSecondary : theme.Tokens.TextPrimary));
        ImGui.TextUnformatted(selectedName is null ? "No file selected" : $"Selected: {selectedName}");
        ImGui.PopStyleColor();
        if (browser.SelectedPath is { } fullPath) UiKit.Tooltip(fullPath);

        var descriptions = browser.Filters.Select(f => f.Description).ToList();
        var filterIndex = browser.FilterIndex;
        if (Forms.ComboField(theme, "File type", descriptions, ref filterIndex, 300)) browser.SetFilter(filterIndex);

        const float buttonWidth = 110f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine();
        var buttonsX = ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - (buttonWidth * 2 + spacing));
        ImGui.SetCursorPosX(buttonsX);
        var cancelled = UiKit.GhostButton(theme, "Cancel##file-picker-cancel", new Vector2(buttonWidth, 0));
        ImGui.SameLine();
        var select = UiKit.PrimaryButton(theme, "Select File##file-picker-select", new Vector2(buttonWidth, 0));
        if (cancelled) Finish(FilePickerResult.Cancelled);
        else if (select) TryConfirm();
    }

    private void TryConfirm()
    {
        if (browser.Confirm() is { } path) Finish(FilePickerResult.Selected(path));
    }

    private void Finish(FilePickerResult result)
    {
        var callback = onClose;
        onClose = null;
        ImGui.CloseCurrentPopup();
        callback?.Invoke(result);
    }
}
