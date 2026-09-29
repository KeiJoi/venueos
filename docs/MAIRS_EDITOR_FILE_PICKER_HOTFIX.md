# Mair's Editor — `.fftrivia` File Picker (post-0.3.8 hotfix, shipped in 0.3.9)

Status: **IMPLEMENTED — awaiting live QA.** Automated tests cover the picker's state/path logic only (see §8); the rendered
modal has not been seen in FFXIV yet.

Scope: VenueOS only; released in **VenueOS 0.3.9** (published before live QA so the operator's secondary Dalamud install can
receive it). `mairstrivia` and every backend repository untouched.

---

## 1. Previous UX

Mair's Editor → Question Library had a fixed-width (260 px) raw ImGui text box, hint *"Path to .fftrivia file"*, and an
**Import** button. Importing worked, but only if the operator already knew and typed/pasted the file's full absolute
Windows path. There was no way to browse for it.

## 2. `.fftrivia` is the canonical extension

`.fftrivia` is the Mair's Trivia Question Set file type (e.g. `mairs-trivia-us-history.fftrivia`). Its contents are JSON
(`TriviaQuestionSet`), but that is irrelevant to the operator — nothing asks them to rename it, and the format is unchanged.
It now has one named home: `QuestionSetFileType` (`src/VenueOS.Modules.Operations/QuestionLibrary/QuestionSetFileType.cs`):
`Extension = ".fftrivia"`, `Filter = "Mair's Trivia Question Sets (*.fftrivia)"`, `PickerFilters = [Filter, All Files]`.

## 3. Existing import pipeline (unchanged, still authoritative)

`MairsEditorOperatorPanel.TryImport` → `File.Exists(importPath)` → `File.ReadAllText` → `MairsEditorService.CheckImportCollision`
→ `ImportReplacing` / (collision) Replace-or-Import-As-New dialogs → `FileQuestionSetRepository.Parse` + validation.
**None of this was touched.** The picker's only job is to put an absolute path into `importPath`; the Import button is still
the only thing that imports.

```
Browse… → FilePickerModal → absolute path → importPath (existing text box) → [Import] → existing TryImport → existing parser/validation
```

## 4. Implementation

VenueOS had no file-picker component. The only file dialog in the codebase is Raffle's use of Dalamud's
`FileDialogManager` (xlsx import/export) — a stock ImGui window with native chrome, which the explicit requirement (and
`NEW_MODULE_GUIDE.md` §15) rules out. A native Windows dialog was not used either: nothing in the repo uses one, it would
need a separate STA thread from the game's render thread, and it would not look like VenueOS. So the smallest reusable
VenueOS-styled picker was added, in two layers (the guide's "logic below the ImGui boundary" rule):

* **`FileBrowser`** (`src/VenueOS.Services/FileBrowser.cs`, ImGui-free, unit-tested) — current folder, listing (folders first,
  then files matching the active filter; hidden/system entries skipped; case-insensitive sort), Back history, Up, a
  top-level "This PC" drive list (Up from a drive root), filter switching, selection, in-folder search, `Confirm()` (returns
  the absolute path only if the file still exists and matches the filter), `CommonPlaces()` (Desktop, Documents, Downloads,
  User Folder — only those that exist), `ResolveInitialDirectory(...)`. Every filesystem call is guarded; failures become a
  short `Error` sentence ("You don't have permission to open that folder.", "That folder no longer exists.", "That file no
  longer exists.", "That folder path is not valid.") and the browser stays usable. It only reads — never creates, copies,
  renames or writes.
* **`FilePickerResult`** (same file) — `Confirmed`/`Path`, and `ApplyTo(currentPath)`: the one rule the path box follows
  (a confirm replaces it; a cancel returns it unchanged).
* **`FilePickerModal`** (`src/VenueOS.Plugin/Shell/FilePickerModal.cs`) — the rendered modal; one instance per owning panel,
  per-instance popup id (same pattern as `ConfirmDialog`/`TextInputModal`). Reusable by any module.

## 5. Shared VenueOS UI used — how it stays VenueOS-styled

`BeginPopupModal` with `NoTitleBar` (no native ImGui title bar), background/border/rounding pushed from the venue theme
tokens, `UiKit.DrawVenueFrame` (the tablet's accent frame), `DialogHeader.Draw` (title + Close — Close = Cancel),
`UiKit.IconButton` (Back / Up), `Forms.PushFieldStyle` (read-only, copyable location bar), `UiKit.SectionHeader` +
`UiKit.ListRow` (Places), `Forms.SearchBox`, theme-coloured selectable rows with new procedural `AppIcons` glyphs
(`folder`, `file`, `arrow-left` — added to `Shell/Icons.cs`), `UiKit.EmptyState`, `UiKit.ErrorState`, `Forms.ComboField`
(File type), `UiKit.GhostButton` (Cancel), `UiKit.PrimaryButton` (Select File). No literal colours. The import row's path
box also now uses `Forms.PushFieldStyle`.

Layout: header → toolbar (Back, Up, location) → Places column | folder listing (search on top) → footer (error line,
"Selected: <name>", File type, Cancel, Select File).

Interaction: click a folder/drive/place to open it; click a file to select it; double-click a file (or Select File) to
confirm.

## 6. File filter

Default and first: **Mair's Trivia Question Sets (*.fftrivia)** (case-insensitive). Second, optional: **All Files (*.*)**.
Every `Request` resets to the primary filter. Switching back to the primary filter drops a selection that no longer matches.
All Files does not weaken anything — Import validates whatever is chosen, exactly as for a typed path.

## 7. Initial folder, path box, Cancel

* **Initial folder** (`FileBrowser.ResolveInitialDirectory`): (1) the folder of the path currently in the box, if that is an
  existing `.fftrivia` file; else (2) the folder of the last file picked this session; else (3) the user's Documents folder;
  else (4) the "This PC" drive list. Session-only — kept in a panel field (`lastImportDirectory`), not persisted; no new
  configuration.
* **Button placement:** `[+ New Set] [Browse…] [ path text box ………… ] [Import]` — Browse is immediately left of the box; the
  box takes the remaining width and stays fully editable (type, paste, adjust a picked path). Its buffer grew 512 → 4096
  bytes so long/Unicode paths aren't cut off.
* **Select:** the absolute path replaces the box's text, the picker closes, and the status line says
  `Selected "<file>" — press Import to import it.` **Nothing is imported.**
* **Cancel / header Close:** the picker closes; the box keeps exactly what it had; no import; no error.

## 8. Automated tests

`tests/VenueOS.Services.Tests/MairsEditorFilePickerTests.cs` — **23 tests**, each in its own temp folder with real files:
`.fftrivia` canonical + primary/default filter; default listing shows folders + `.fftrivia` only (case-insensitive,
`.json`/`.txt`/`.fftrivia.bak` hidden); All Files fallback; selection yields an absolute path; Confirm with nothing selected;
select-fills / cancel-preserves (`ApplyTo`); spaces + punctuation; Unicode folder/file names; a 200-character file name;
selecting does not import, rewrite, copy or rename the file; a picked path imports through the unchanged
`MairsEditorService` pipeline exactly like a typed one; a picked malformed file is still rejected by the existing importer;
initial-folder rule (current path → last folder → default → drive list, including invalid/missing/non-`.fftrivia` input);
missing initial folder opens the drive list; into/Up/Back; Up from a drive root → drive list → Back; empty folder; missing and
invalid folder paths; a folder deleted while open; a file deleted after selection; search; hidden entries skipped.

**Not automated** (no ImGui harness exists — `NEW_MODULE_GUIDE.md` §30): the rendered modal, its look under the four themes,
clicks/double-clicks, and an access-denied folder (not reliably reproducible in a temp folder; handled by the same guarded
`TryLoad` path, which maps `UnauthorizedAccessException` to the permission message).

Observed, deliberately **not** changed (outside this hotfix): `TryImport` catches only `IOException`, so an access-denied
file or a malformed file still surfaces through the panel's `SafeDraw` error state + Diagnostics, as before. Glyphs the game
font lacks (e.g. some CJK) render as placeholders in the listing, but the stored path is exact.

## 9. Live QA (not yet performed)

* **A — UI.** Open Mair's Editor. PASS: **Browse…** sits immediately left of the path box; layout looks like VenueOS.
* **B — Open picker.** Click Browse…. PASS: a themed dialog (VenueOS header + frame, no native ImGui title bar) opens;
  File type shows *Mair's Trivia Question Sets (*.fftrivia)*; only folders and `.fftrivia` files are listed. Check
  Dark/Light/Neon/Midnight.
* **C — Navigate.** Use Places / folders / Back / Up to reach a folder with a known valid `.fftrivia`. PASS: navigation works;
  the file is visible and selectable.
* **D — Select.** Click the file, then **Select File** (or double-click it). PASS: picker closes; its absolute path is in the
  box; status says press Import; **no import yet** (library unchanged).
* **E — Import.** Press **Import**. PASS: imports exactly as with a hand-typed path (including the collision dialogs if the
  set already exists); the set loads correctly.
* **F — Cancel.** Open Browse… again, navigate somewhere, press **Cancel** (and separately the header ✕). PASS: picker closes;
  the path box is unchanged; nothing imports; no error.
* Extra: Browse… again after D — it opens in the folder of the file in the box.

## 10. Files

New: `src/VenueOS.Services/FileBrowser.cs`, `src/VenueOS.Plugin/Shell/FilePickerModal.cs`,
`src/VenueOS.Modules.Operations/QuestionLibrary/QuestionSetFileType.cs`,
`tests/VenueOS.Services.Tests/MairsEditorFilePickerTests.cs`, this document.

Modified: `src/VenueOS.Plugin/MairsEditorOperatorPanel.cs` (Browse… button, picker wiring, path-box width/style/buffer),
`src/VenueOS.Plugin/Shell/Icons.cs` (`folder`, `file`, `arrow-left`), `docs/USER_MANUAL.md` (Mair's Editor Import),
`NEW_MODULE_GUIDE.md` (§15 component list + icon keys), `docs/POST_0.3.7_TRAINING_AUDIT.md` (ledger).
