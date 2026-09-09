# Block Letters — Implementation Report

Status: implemented, automated-tested, **not yet live-tested**. `UnderDevelopment: true`, disabled by default. This is the first greenfield module built under the hardened `NEW_MODULE_GUIDE.md`.

## 1. Executive Summary

Block Letters is a small local utility module: it lets an operator compose text mixing normal ASCII and FFXIV's special "block letter" glyphs, tracks the composed text's length against the real limit of a selected FFXIV destination (Chat / Party Finder Comment / Macro Line), and copies the finished text to the clipboard. It has no backend, no WebSocket, no player identity, no chat-sending, and no automation — exactly as scoped.

The glyph mapping was researched from the public site ffxivalpha.com (client-side character table, not the page's visuals) and the three destination limits were researched from a combination of Square Enix forum threads, Dalamud's own source, and — for the highest-confidence one — VenueOS's own already-reconstructed Party Finder module.

## 2. Module ID / Descriptor

| Field | Value |
|---|---|
| ID | `tools.blockletters` |
| Display Name | Block Letters |
| Description | "Create FFXIV block-letter text within in-game character limits." |
| Icon key | `block-letters` (new — a pixel-art "A", `Shell/Icons.cs`) |
| `UnderDevelopment` | `true` |
| `IsEnabled` default | `false` |
| `DisplayOrder` | 11 (after Brackets, the current highest at 10) |

No ID conflict existed with any registered module; `tools.*` was unused (the guide's own `GuestNotes` example uses `tools.guestnotes` as a hypothetical, never actually registered).

## 3. Architecture / State Authority

Per §34a: **this module's local per-venue config (the default destination) is the only state it owns, full stop.** No backend, no FFXIV game-state reads, no shared/other-module state. The live composition text is deliberately *not* modeled as state anywhere except a private field on the operator panel — it is never read or written by `BlockLettersService`, never serialized, and is intentionally lost on venue switch, module disable, or plugin reload (§13, §22 — a documented, deliberate exception to "if the operator can edit it, persist it," justified in §13/§14 below).

Files:

```
src/VenueOS.Modules.Operations/BlockLetters/
  BlockLetterCatalog.cs       — the researched glyph table (pure data)
  BlockLettersTextEngine.cs   — pure logic: destinations, byte-length limits, insert/replace-with-limit, UTF-8<->UTF-16 offset bridge
  BlockLettersService.cs      — per-venue settings + IVenueModule wrapper (BlockLettersModule)

src/VenueOS.Plugin/BlockLetters/
  BlockLettersOperatorPanel.cs — Draw()/DrawSettings(), the ImGui InputTextMultiline callback wiring, palette, actions
```

`Plugin.cs`: one service construction line, one panel construction line, one `modules.Register(...)` call — nothing else needed for Home, embedded/detached rendering, or Auto Pop-Out (all generic per §6–§9).

## 4. FFXIVAlpha Research Result

- **Date inspected:** 2026-09-08
- **Page inspected:** `https://ffxivalpha.com` (homepage; the tool has two tabs, "Block Characters" and "Other Characters" — no separate page)
- **How extracted:** the homepage's compiled Next.js client chunk (`_next/static/chunks/app/page-*.js`) was fetched directly and contains a literal JS object mapping each supported character to a hex codepoint string, used via `String.fromCodePoint(parseInt(hex,16))`. This was cross-checked against the raw server-rendered HTML, which embeds the actual `\uE0xx` characters as real UTF-8 bytes in the 36 "Block Characters" button elements — both sources agreed exactly.
- **Total glyph buttons on the site:** 36 in "Block Characters" (digits 1–9,0 then letters in QWERTY order) + 85 in "Other Characters" = 121 buttons, plus 2 codepoints (`?`, `+`) reachable only via the site's keyboard-intercept toggle (no dedicated button) = **123 distinct glyphs** in total.
- **Style count:** exactly one "block letters" style. It is case-insensitive at the source — the site lowercases input before lookup, so typed/clicked uppercase and lowercase both resolve to the same 26 glyphs. There is no separate uppercase/lowercase glyph pair, and no bold/italic/other variant anywhere on the site.
- **Characters that could not be verified:** the semantic meaning of the 85 "Other Characters" glyphs — the site's own source carries no name/label for any of them, only raw codepoints. VenueOS labels these by hex codepoint (honest — we don't know what they depict beyond the rendered glyph) rather than inventing names.
- **Site behavior deliberately NOT copied:** the live keyboard-intercept "Type Block Letters" toggle; append-only button insertion (the site's own button click always appends to the end, never inserts at cursor — Block Letters implements real cursor/selection-aware insertion instead, per the task's explicit requirement); the "Submit to Gallery" community feature; any branding/visual design.

## 5. Complete Glyph/Catalog Summary

`BlockLetterCatalog` (`VenueOS.Modules.Operations.BlockLetters`):

| Group | Count | Codepoints | Source |
|---|---|---|---|
| Letters (A–Z) | 26 | U+E071–U+E08A | ffxivalpha.com's named `$` table |
| Digits (0–9) | 10 | U+E08F–U+E098 | ffxivalpha.com's named `$` table |
| Punctuation (`?`, `+`) | 2 | U+E070, U+E0AF | ffxivalpha.com's named `$` table (keyboard-only on the donor site; given real buttons here) |
| Other | 85 | U+E020–U+E0BF (several disjoint sub-ranges) | ffxivalpha.com's unlabeled `J` array, labeled here by hex codepoint |
| **Total** | **123** | | |

All codepoints are FFXIV Private Use Area characters rendered by the game's own AXIS-family font — no bundled font asset is required or included.

## 6. FFXIV Destination Limits Researched

| Destination | Limit | Confidence | Source |
|---|---|---|---|
| Chat (Say/Yell/Shout/Party/Tell/FC/LS/CWLS) | **500** | Medium | Square Enix forum thread documenting a firsthand-tested hard 500-character input stop; independently, Dalamud's own reimplemented chat entry box allocates a 500-byte `InputText` buffer (`goatcorp/Dalamud#862`) |
| Party Finder Comment | **192** | **High** | VenueOS's own already-reconstructed Party Finder module (`PartyFinderModels.TruncateCommentUtf8`/`CommentByteCount`) already encodes this in production, sourced from the donor project's own live "{byteCount}/192 bytes" counter reverse-engineered against the real client. Party Finder's *only* free-text field is the Comment — every other PF field (category, duty, jobs, conditions, etc.) is an enum/toggle, not something a text composer targets, so no second PF destination is needed. |
| Macro Line | **181** | Medium | Two independent Square Enix forum threads (thread #275306 and #506442) both cite 181 characters/line × 15 lines = 2,715 total, attributed to the same original tester. Confirmed NOT 150 or 255 (rejected guesses). |

**A number I explicitly did not use:** "192 characters, max 2 lines" for Party Finder kept surfacing from search-engine AI summaries. Direct inspection of the page those summaries cite (gamerescape.com) found no such text — the "192" there is unrelated CSS boilerplate (`192dpi`, Unicode range `F191-F192`). This looks like a hallucinated/misattributed figure propagating through search summarizers. VenueOS's own donor-verified 192-*byte* figure (a different, correct number that happens to share digits) is used instead.

## 7. Character-Counting Algorithm and Why

**Unit: UTF-8 byte length**, via `BlockTextLength.CountBytes` (`Encoding.UTF8.GetByteCount`), applied identically to all three destinations. Never `string.Length` (UTF-16 code units) — every block-letter glyph is a single UTF-16 char but a 3-byte UTF-8 sequence, so char-counting would silently under-count by up to 3x for glyph-heavy text.

This choice is directly confirmed for two of the three destinations: Party Finder's Comment field (VenueOS's own already-shipped `CommentByteCount`, donor-verified) and Chat (Dalamud's own byte-sized `InputText` buffer, consistent with FFXIV's UTF-8/SeString client architecture throughout). **Macro Line's counting unit for non-ASCII content specifically was not directly verified by any source** — every forum post testing "181 characters" used plain ASCII macro commands, where UTF-8 bytes, UTF-16 units, and codepoints all coincide. UTF-8 byte counting is used for Macro Line for architectural consistency with the other two confirmed destinations and FFXIV's uniformly UTF-8 internal string representation, but this specific assumption is flagged for live verification (§19 below).

Additionally, ImGui's own `InputTextMultiline` callback (`ImGuiInputTextCallbackData`) indexes `CursorPos`/`SelectionStart`/`SelectionEnd` as **byte offsets into its own UTF-8 buffer**, not C# string character indices — `Utf8Offsets.ToCharIndex`/`ToByteOffset` bridge the two on every callback invocation. This is a real, easy-to-get-wrong detail: without it, any composition containing a block-letter glyph before the cursor would silently miscompute the insertion point.

## 8. Editor Insertion/Selection Behavior

Real cursor/selection-aware editing, not append-only, via `BlockTextEditor.Insert(current, selectionStart, selectionEnd, insertion, maxBytes)` — a pure, ImGui-free function used identically for:

- **Typed characters and pasted text** — via `ImGuiInputTextFlags.CallbackCharFilter`, which Dear ImGui fires once per accepted character for both typing *and* paste (paste is fed through the same per-character filter path internally), so evaluating one character at a time against the remaining byte budget naturally implements "accept as much of a paste as fits, drop the rest" with no paste-specific code.
- **Palette-button glyph insertion** — a button click sets a pending-insertion flag; the next `ImGuiInputTextFlags.CallbackAlways` tick (which fires every frame regardless of focus) applies it via the officially documented `DeleteChars`/`InsertChars` callback-mutation pair, then reports the new cursor position back in ImGui's byte-offset terms.

`BlockTextEditor.Insert` never splits a Unicode scalar value — truncation walks `text.EnumerateRunes()` and only keeps whole runes, so a 3-byte block-letter glyph is either included whole or dropped whole, never emitted as invalid UTF-8.

## 9. Hard-Limit Behavior

Every edit path is byte-budget-checked and clamped/rejected *before* it reaches the composition, matching the requirement that VenueOS "stop accepting input" the way FFXIV does:

- A single typed/pasted character that would overflow is discarded (`CallbackCharFilter` sets `EventChar = 0` and returns non-zero).
- A palette-button insertion that doesn't fully fit is rejected outright (a single glyph is atomic — there is no valid partial insertion of one glyph).
- A selection replacement is accepted only if the replacement text (or the portion of it that fits) keeps the total within budget.

## 10. Destination-Change Over-Limit Behavior

Switching the destination selector **never mutates the composition**. `BlockLettersLimits.ExceedsLimit(text, destination)` is a pure predicate with no side effects; the operator panel uses it purely for display (a `WarningState` explaining the overage) and to gate the Copy button (`ImGui.BeginDisabled`). Once the operator shortens the text back within the newly selected limit, Copy re-enables automatically — there is no separate "confirm" step because nothing was destroyed.

## 11. Clipboard Behavior

`ImGui.SetClipboardText(composition)` — the exact composition, no prefix/suffix/transformation, matching the established pattern already used by ShoutRunner/Bingo/Raffle/Mair's Trivia/TournamentControl. A transient "Copied to clipboard." success line (2-second `ImGui.GetTime()` window) reuses `ShoutRunnerOperatorPanel`'s exact precedent rather than `NotificationService`, which has no renderer (§21/§36).

## 12. Settings / Persistence

**Persisted (per Venue Profile, schema version 1):** `BlockLettersSettings.DefaultDestination` only — the destination the module starts on when opened. `DrawSettings()` renders only this field, genuinely separated from `Draw()`'s live operation from the start (the guide's §8/§21 documented gap that most existing modules still have — Block Letters, Party Finder, and now Guest Notes-style new modules don't).

**Deliberately NOT persisted:** the composition text itself, and the live destination selector's *current* value during a session (it re-seeds from the persisted default on venue change/first draw, but changing it mid-session does not write back to Settings — routine live switching is operational, per §9, not a configuration edit). This matches the task's explicit preference in full: "Persist: DEFAULT DESTINATION per Venue Profile. Do NOT persist the working composition... The composition can be ephemeral."

Verified across the same serialize/deserialize-through-a-fresh-store boundary the guide requires (§13a) — see `BlockLettersServiceTests.Default_destination_persists_through_the_venue_store` and `Two_venue_profiles_do_not_leak_the_default_destination`.

## 13. Files Added/Changed

**Added:**
- `src/VenueOS.Modules.Operations/BlockLetters/BlockLetterCatalog.cs`
- `src/VenueOS.Modules.Operations/BlockLetters/BlockLettersTextEngine.cs`
- `src/VenueOS.Modules.Operations/BlockLetters/BlockLettersService.cs`
- `src/VenueOS.Plugin/BlockLetters/BlockLettersOperatorPanel.cs`
- `tests/VenueOS.Services.Tests/BlockLettersCatalogTests.cs`
- `tests/VenueOS.Services.Tests/BlockLettersTextEngineTests.cs`
- `tests/VenueOS.Services.Tests/BlockLettersServiceTests.cs`
- `docs/BLOCK_LETTERS_IMPLEMENTATION.md` (this file)

**Changed:**
- `src/VenueOS.Plugin/Shell/Icons.cs` — added the `block-letters` icon key + `DrawBlockLetters` (pixel-art "A")
- `src/VenueOS.Plugin/Plugin.cs` — construct `BlockLettersService`/`BlockLettersOperatorPanel`, `modules.Register(new BlockLettersModule(...))`

No other file was touched. `NEW_MODULE_GUIDE.md`, donor repositories, and every other module's code/tests are untouched.

## 14. Tests Added

41 `[Fact]`/`[Theory]` method declarations (several `[Theory]` methods expand to multiple xUnit test cases) across three files:

- **`BlockLettersCatalogTests.cs`** (8): total counts per group, unique labels, no duplicate codepoints across the entire catalog, representative letter/digit/punctuation codepoint values against the researched mapping, every glyph's `Value` is exactly its own codepoint, the "Other" bank never collides with the named letters range.
- **`BlockLettersTextEngineTests.cs`** (22): the three limit values; UTF-8-byte-not-UTF16-char measurement (including a case where char count looks safe but byte count isn't); insert at end/beginning/middle; replace selection; plain ASCII untouched; mixed ASCII+glyph composition; exact-limit acceptance; one-byte-over clamping; glyph insertion fully rejected when nothing fits; paste accepting only the portion that fits; selection replacement when it fits; never splitting a glyph mid-sequence at a non-multiple-of-3 boundary; destination-switch over-limit detection without mutation; Copy-eligibility restoration; UTF-8 byte-offset↔char-index round trip (including out-of-range clamping) — the exact bridge the ImGui callback relies on.
- **`BlockLettersServiceTests.cs`** (11): default destination on a fresh venue, persistence through a rebuilt service instance, two-venue isolation, no-op re-save guard, module ID/display name/`UnderDevelopment`/default-disabled/icon-key assertions, Draw vs. DrawSettings delegate separation, `OnVenueChangedAsync` reloading the new venue's settings.

Consistent with §30: the ImGui-specific parts of `BlockLettersOperatorPanel` (the actual `InputTextMultiline` callback wiring, the palette grid) are not unit-tested — there is no `VenueOS.Plugin` test project anywhere in this repository, deliberately, since ImGui needs a live rendering context. Everything the callback *delegates to* (`BlockTextEditor`, `BlockLettersLimits`, `Utf8Offsets`) is fully covered.

## 15. Final Test Count

**674 total, 0 failed** (`VenueOS.Core.Tests`: 4, `VenueOS.Venues.Tests`: 23, `VenueOS.Services.Tests`: 647 — the last of these includes Block Letters' new tests alongside every pre-existing test in the repository, none of which were modified).

## 16. Debug Result

`dotnet build VenueOS.sln -c Debug` — **Build succeeded. 0 Warning(s). 0 Error(s).**
`dotnet test VenueOS.sln -c Debug` — **674 passed, 0 failed, 0 skipped.**

## 17. Release Result

`dotnet build VenueOS.sln -c Release` — **Build succeeded. 0 Warning(s). 0 Error(s).**

## 18. Known Limitations

- **Macro Line's byte-vs-character counting unit is an assumption, not a directly verified fact** (§6/§7) — the 181-character figure itself is well-corroborated, but no source tested it with non-ASCII content. Flagged explicitly for live QA below.
- **Chat's 500-limit "counted in bytes" mechanism** is strongly consistent with all available evidence (FFXIV's UTF-8 client architecture, Dalamud's byte-sized input buffer) but no source directly demonstrates a multi-byte character consuming more than one unit of the 500 experimentally.
- **The 85 "Other Characters" glyphs have no known semantic meaning** — labeled by hex codepoint only. Dalamud's font may not render every one of them identically to how the game's own chat font does; the underlying codepoint is always correct regardless of how it previews in the editor.
- Dear ImGui's character-filter mechanism (`EventChar` is a 16-bit value) does not support characters outside the Basic Multilingual Plane — irrelevant to this module's own glyph set (all BMP) but means a paste containing an astral-plane character (rare, and not something FFXIV chat supports either) would not be handled specially; this is a Dear ImGui-wide constraint, not something this module can work around.
- No `DiagnosticsService` wiring — this module has no failure mode beyond ordinary UI operations (no backend, no game-state reads), matching the guide's own minimal-example precedent (Guest Notes) for a module that genuinely cannot fail in an attributable way.

## 19. Required Live QA

See the short checklist the task requested — the module cannot be called done until the user performs this in Dalamud:

1. Enable Block Letters (Settings → Modules) and open it embedded.
2. Verify the palette is readable and every letter/digit button produces the exact expected in-game block character when pasted into chat.
3. Insert a glyph in the middle of typed ASCII; replace a selection with a glyph; paste ordinary text — verify all land correctly.
4. Watch the counter while typing/inserting; verify it stops accepting input exactly at each destination's real limit.
5. Fill to the limit, switch to a smaller destination, confirm the text is preserved (not truncated) with a warning shown and Copy disabled; shrink the text and confirm Copy re-enables.
6. Copy and paste into actual FFXIV Chat, Party Finder Comment, and a Macro line — confirm the game accepts exactly what the module predicted for each (this is the one thing automated tests cannot prove — it's the real test of §6/§7's counting-unit assumptions, Macro Line's especially).
7. Verify the detached window, a second Venue Profile's independent default destination, and all four themes (Dark/Light/Neon/Midnight).

## 20. Hardened-Guide Definition-of-Done Review

Walked against §42 in full. Notable observations for the guide's own validation (per the task's §23 instruction — not editing the guide, just recording):

- **Useful:** §8's "DrawSettings shows only persistent config" rule was straightforward to satisfy from a cold start once the module's only real setting (default destination) was identified — having Party Finder as a worked reference example made this fast, not ambiguous.
- **Useful:** §34a's "state authority model, one sentence for a simple module" was exactly right-sized here — "the local per-venue config is the only state, full stop" was easy to state and easy to verify against (the composition text living nowhere but a UI field).
- **Ambiguous in practice:** the guide doesn't have a section specifically addressing ImGui's `InputTextMultiline` callback mechanism (byte-vs-char offsets, `CallbackCharFilter` vs `CallbackAlways`, the "insert text externally" pattern) — this required original research against the actual Dalamud.Bindings.ImGui assembly (via reflection, since no source/docs were locally available) rather than following established VenueOS precedent, since no existing module in this codebase does cursor-aware programmatic text insertion. Worth a future guide note if another module needs the same technique.
- **Not applicable, correctly:** §34 (backend modules), §34b (realtime), §34c (import/export), §13b (archive/delete/reset), §28/§28a (guest identity/target capture), §42b (venue-switch guard) — all correctly out of scope for a module this guide's own intro anticipated ("a Block Letters-style utility does not need WebSockets... GuestIdentity, or an archive/delete/reset lifecycle").

## 21. Git Status

At the start of this task, the repository already had substantial uncommitted work in progress (Raffle/TournamentControl/Brackets reconstruction, a documentation pass, and the hardened `NEW_MODULE_GUIDE.md` itself) — all of it left untouched. This task added the files listed in §13 above on top of that pre-existing working tree.

## 22. Confirmation

**Nothing was staged, committed, pushed, tagged, or released.** No `git add`, `git commit`, `git push`, `git tag`, branch creation, reset, or clean was run at any point during this task. `git status` was used read-only, twice, purely to confirm the above.

---

## LIVE QA FIX — GLYPH FONT / IMMEDIATE INSERTION

Status: fixed, automated-tested, **glyph rendering and insertion behavior not yet re-verified live** — this pass corrects two defects the user found during the first live QA session; it does not itself constitute live verification.

### Defect 1 — palette buttons showed labels/codepoints instead of the actual FFXIV glyph

**Root cause:** the palette buttons never rendered with a font that contains the FFXIV Private Use Area glyphs at all — they drew their text with whatever font ImGui/Dalamud's default UI font currently was (a normal Latin-script font), which simply has no glyph for codepoints like U+E071 or U+E099. ImGui's fallback for a missing glyph in the active font is to show nothing/a blank box, so the buttons were changed at some point to show the semantic label ("A") or hex codepoint ("E099") instead, purely so the button wasn't blank — but that meant the operator could never see the actual glyph they were about to insert.

**Font/API now used:** Dalamud's own **game font atlas** — `IPluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis18))` (`Dalamud.Interface.GameFonts`/`Dalamud.Interface.ManagedFontAtlas`). This is Axis, the same font family FFXIV itself uses to render chat and most UI text, and the same font that already displays this exact glyph set correctly in-game — accessing it through Dalamud's documented game-font API pulls glyphs from the player's own already-installed game files at runtime. **No font file, custom asset, or Square Enix font resource is bundled with VenueOS** — the handle is a live reference into the game's own font data, created once at plugin construction (`Plugin.cs`) and disposed once in `Plugin.Dispose()`, exactly like every other long-lived Dalamud resource this plugin owns.

**Wiring:** `BlockLettersOperatorPanel` now takes this `IFontHandle` in its constructor and pushes it (`IFontHandle.Push()`/`Pop()`, once per palette group rather than once per button) around the glyph buttons in `DrawGlyphGroup`. Each button's visible label is `BlockGlyph.Value` (the actual glyph character) instead of `BlockGlyph.Label`/hex codepoint. This applies uniformly to every group, including Letters/Numbers/Punctuation, per the explicit requirement that ordinary recognizable mappings also show the real glyph, not just their ASCII label.

**Graceful fallback:** `IFontHandle.Available` is checked before every group is drawn (Dalamud font atlases build asynchronously and may not be ready the first frame or two after plugin load). While unavailable, buttons fall back to their hex codepoint label (the pre-fix behavior) rather than rendering blank or in the wrong font. This is expected to be transient in practice, not a steady-state condition. **The underlying catalog mapping (`BlockLetterCatalog`) was not touched** — this fix is purely about how a glyph is displayed, not what codepoint it resolves to.

**Not claimed:** whether the Axis18 font's rasterized glyph range actually covers all 123 catalog entries (particularly the 85 unlabeled "Other Symbols") can only be confirmed by looking at the rendered palette in Dalamud — if any individual glyph is missing from Axis's baked range, ImGui's own no-glyph handling applies to that one button (likely a blank cell) while every other button continues to render normally; this is the "fall back gracefully for that glyph" the task asked for, at the level Dalamud's API actually supports (there is no per-codepoint "is this glyph baked" query exposed by the version of `Dalamud.Bindings.ImGui`/`Dalamud.Interface.ManagedFontAtlas` in this SDK).

### Defect 2 — palette clicks did not appear in the composition until the box was clicked

**Root cause, precisely:** the original design queued a clicked glyph in a `pendingInsertion` field and applied it inside the `InputTextMultiline` callback's `ImGuiInputTextFlags.CallbackAlways` branch. **`CallbackAlways` only fires while the InputText widget is ImGui's current *active item*** (i.e., while it has input focus) — this is standard Dear ImGui behavior, not a bug in the binding. Clicking a palette button is itself an ImGui interaction that makes the *button* the active item, which deactivates the composition box. On the very next frame(s), the composition box is no longer active, so `CallbackAlways` never fires, so the queued glyph was never consumed — until the operator clicked back into the box, reactivating it and finally letting the callback run once. This exactly matches the reported symptom.

**Exact fix:** palette-button insertion no longer goes through the ImGui callback at all. `BlockLettersOperatorPanel.InsertGlyph(string value)` mutates the authoritative `composition` field **directly and synchronously**, via the same pure `BlockTextEditor.Insert` function the typed/pasted path uses, the instant the button is clicked. `ref string` `InputTextMultiline` bindings re-sync their displayed content from whatever the passed-in string currently holds on every frame the widget is *not* the active item (they only retain a separate internal edit buffer while active) — so the next rendered frame after a button click shows the new `composition` value with no further action needed. `ImGuiInputTextFlags.CallbackAlways` is kept, but now serves a narrower, correctly-scoped purpose: while the operator is actively typing, clicking, or dragging a selection inside the box, it keeps two new fields — `selectionStartChars`/`selectionEndChars` (a C# char-index cursor/selection model, converted from ImGui's UTF-8 byte offsets via the existing `Utf8Offsets` bridge) — in sync with the widget's real state. A button click reads and updates these same two fields directly, so both input paths (typing and buttons) share one authoritative cursor/selection model at all times, per the requirement in this task's §4.

**No artificial focus changes were added** — the fix does not call `ImGui.SetKeyboardFocusHere` or otherwise force the box back into focus after a button click, matching the task's explicit instruction not to do so unless actually necessary; it wasn't.

### 5. Cursor / selection / limit behavior after the fix

- **Cursor insertion:** still works — `InsertGlyph` reads `selectionStartChars`/`selectionEndChars` (last known from real editing) and falls back to `composition.Length` (insert-at-end) only if the operator has never yet interacted with the box this session (`hasKnownCursor == false`), matching the one fallback the task explicitly allows.
- **Selection replacement:** still works — a non-empty stored selection is replaced by the inserted glyph exactly as before; only *how* the insertion is triggered changed (immediately, from the button handler, instead of deferred through a callback).
- **Limit enforcement:** unchanged and intact — `InsertGlyph` calls the same `BlockTextEditor.Insert(..., activeMaxBytes)` the typed/paste path uses; a glyph that would overflow the active destination's byte budget is rejected exactly as before (composition left unchanged, no Unicode scalar ever split). Nothing about the fix bypasses or duplicates this validation — there is exactly one insertion code path (`BlockTextEditor.Insert`) for typed input, pasted input, and palette-button input alike.

### Files changed (this pass only)

- `src/VenueOS.Plugin/Plugin.cs` — construct/dispose the shared `IFontHandle` (`blockLettersGlyphFont`, `Axis18`), pass it into `BlockLettersOperatorPanel`.
- `src/VenueOS.Plugin/BlockLetters/BlockLettersOperatorPanel.cs` — added the `IFontHandle` constructor parameter and font-pushing in `DrawGlyphGroup`; buttons now show `glyph.Value` (with hex-codepoint fallback while the font handle isn't yet available); removed `pendingInsertion` and the callback-driven insertion-application branch; added `selectionStartChars`/`selectionEndChars`/`hasKnownCursor` fields and the new `InsertGlyph` method that mutates `composition` directly.
- No changes to `BlockLetterCatalog.cs`, `BlockLettersTextEngine.cs` (`BlockTextEditor`/`BlockLettersLimits`/`Utf8Offsets`), or `BlockLettersService.cs` — the pure logic and the researched mapping/limits were correct already; only the panel's font usage and insertion-triggering mechanism changed.

### Tests added

`tests/VenueOS.Services.Tests/BlockLettersImmediateInsertionTests.cs` (new file, 10 tests) — exercises `BlockTextEditor.Insert` in the exact sequential, synchronous call pattern `InsertGlyph` now uses (each call's result feeding directly into the next call's arguments, with nothing resembling an ImGui callback/event run in between), covering: a single call mutates immediately; insertion needs no intervening editor-input event; insert at end/at a stored mid-text cursor/replacing a stored selection; three chained "clicks" (A, B, C) compose correctly in order; immediate insertion still obeys the destination byte limit; a rejected insertion leaves the composition unchanged right away; and that the returned text is immediately what "Copy" and the byte-count display would read, with no extra synchronization step. Per the task's own instruction, no test claims to prove a glyph visually renders — that is live-ImGui-only and is left to the live QA below.

### Final full test count

**684 total, 0 failed, 0 skipped** (`VenueOS.Core.Tests`: 4, `VenueOS.Venues.Tests`: 23, `VenueOS.Services.Tests`: 657 — up from 647 before this pass, +10 for the new immediate-insertion test file).

### Debug build

`dotnet build VenueOS.sln -c Debug` — **Build succeeded. 0 Warning(s). 0 Error(s).**
`dotnet test VenueOS.sln -c Debug` — **684 passed, 0 failed, 0 skipped.**

### Release build

`dotnet build VenueOS.sln -c Release` — **Build succeeded. 0 Warning(s). 0 Error(s).**

### Remaining live QA

Everything in §19 above still applies. Specific to this fix pass:

1. Open Block Letters and confirm the palette now visibly shows actual FFXIV block-letter/symbol glyphs (not `E099`-style codepoints or bare "A"/"0" labels) for as many buttons as Axis18 actually has baked — note any individual button that still renders blank.
2. Click a glyph **without** clicking the composition box first — confirm it appears immediately, no second click needed.
3. Click several different glyphs consecutively — confirm every single click inserts immediately, in order.
4. Type ASCII, move the cursor into the middle, click a glyph — confirm it inserts at that exact position.
5. Select text, click a glyph — confirm the selection is replaced immediately.
6. Confirm the byte/limit counter and Copy both reflect every palette click immediately, with no need to interact with the composition box afterward.
