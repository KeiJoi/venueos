# Block Letters — caret / insertion fix (VenueOS 0.3.8 maintenance, Work Package 5)

Status: **IMPLEMENTED — awaiting live QA.** Automated tests and both builds pass, and the state machine was replayed
against the game's native ImGui headlessly (see §14/§15 for exactly what that does and does not prove). It has **not**
been verified inside FFXIV. Ledger row: `POST_0.3.7_TRAINING_AUDIT.md` #6.

## 1. Exact root cause

`BlockLettersOperatorPanel` treated ImGui's `SelectionStart` / `SelectionEnd` (from `ImGuiInputTextCallbackData`,
sampled in `CallbackAlways`) as the caret. **They are not the caret.** Verified against the native ImGui the game uses
(1.88, loaded through `Dalamud.Bindings.ImGui` from both the `dev` and `15.0.3.5` Dalamud hooks):

* The caret is **`CursorPos`**.
* `SelectionStart`/`SelectionEnd` are only meaningful **while they differ** (a real selection; then
  `CursorPos == SelectionEnd`, and a backwards selection reports `Start > End`).
* **Typing advances `CursorPos` but leaves `SelectionStart == SelectionEnd` frozen** at wherever the last click /
  selection change / replaced selection left them (this is Dear ImGui's embedded stb_textedit behavior).

Captured trace (buffer `<block A>` then typed `ab`; block A is 3 UTF-8 bytes):

```
click in box        ALWAYS cursor=3 sel=(3,3)
type a              ALWAYS cursor=4 sel=(3,3)
type b              ALWAYS cursor=5 sel=(3,3)     <- caret is 5; the selection pair is stale at 3
```

The old code converted the stale `(3,3)` to a char index (`1`, "right after the block") and inserted there.

## 2. Old composition / caret state flow

* `composition` (a `string`) is the single authoritative text, bound to `ImGui.InputTextMultiline("##blockletters-composition", ref composition, …)` with `CallbackAlways | CallbackCharFilter`.
* Normal typing: ImGui edits its own internal buffer while the box is active; the binding's `ref string` overload encodes `composition` into a buffer each frame, calls `InputTextEx`, and decodes the buffer back into `composition` every frame (verified by dumping the binding's IL). The callback is forwarded to the user delegate untouched (`InputTextCallbackStatic` → `ImGuiInputTextCallbackDelegate.Invoke`), so the panel sees raw ImGui data.
* `CallbackAlways` (fires only while the box is the active item) copied `SelectionStart`/`SelectionEnd` into `selectionStartChars`/`selectionEndChars` and set `hasKnownCursor = true`.
* A palette click called `InsertGlyph`, which read those two fields, called `BlockTextEditor.Insert`, wrote `composition` directly, and set both fields to the new caret.

## 3. Why typing caused the next block to land after the previous block

1. Click block A → `InsertGlyph` stores selection `(1,1)` (char indices, right after A).
2. Operator clicks into the box and types `ab`. The callback now reports `CursorPos` = end but `SelectionStart/End` = the frozen click position — right after A — and the panel stores *that*.
3. Click block B → inserted at the stored position: directly after A. **Reported symptom exactly.**

The same defect applies mid-text (type after clicking in the middle, then insert → block lands at the click position, not the caret). The previous block was merely the most common place for the frozen position to be, because `InsertGlyph` itself seeds it.

## 4. Why clicking the end worked around it

A mouse click in the box makes stb_textedit set `select_start = select_end = cursor`, re-synchronising the pair with the real caret. Clicking at the end of the text therefore repaired the model until the next character was typed.

## 5. Why multiple clicks could appear necessary

Established, not guessed: the pair is re-frozen by **every** typing burst, and `InsertGlyph` also resets it. Each cycle "insert block → type → insert block" therefore needed a fresh repair click; a click that landed anywhere other than the true caret position repaired it to the wrong place, hence "sometimes repeatedly".

## 6. Caret coordinate / indexing semantics

* ImGui reports `CursorPos`, `SelectionStart`, `SelectionEnd` as **UTF-8 byte offsets** into `BufTextSpan`. Verified: after `<block A>` (3 bytes) the cursor reads 3; after typing `z` past a block the cursor reads 6 for `AB<block>z`.
* The panel's model uses **.NET `string` (UTF-16) char indices** into `composition`. `Utf8Offsets` bridges them; the two are never mixed. A block glyph (U+E071…) is 3 bytes / 1 char; an astral character is 4 bytes / 2 chars (surrogate pair).

## 7. Selection semantics

Selection **is** supported and reliable while the operator is editing:

* Forward, backward (drag or Shift+Left), select-all (Ctrl+A) and selection-at-start/end all report a differing pair with `CursorPos == SelectionEnd` — verified against native ImGui.
* A block click moves ImGui's active item to the button on the **mouse-down** frame, but the composition box's `CallbackAlways` still fires on that frame with the correct caret/selection (verified frame by frame). The state captured on the last active frame is therefore exactly the state at click time — selection survives the click in the *model*. Whether it survives in the live FFXIV widget is a live-QA item.
* Pure-logic rule: **selected text is replaced by the inserted block**, exactly as ordinary editors do.

## 8. Exact implementation

New pure file `src/VenueOS.Modules.Operations/BlockLetters/BlockEditorCaret.cs` (no ImGui):

* `BlockEditorSelection(Start, End)` — normalized char-index range; empty = caret.
* `BlockEditorObservation(Selection, TextLength)` — what the editor last reported, with the length of the text it described.
* `BlockEditorCaret.Observe(utf8, cursorBytes, selStartBytes, selEndBytes)` — **if the selection pair differs it is a real selection (min/max); otherwise the caret is `CursorPos`.** Converts bytes → chars (snapping an offset that lands inside a multi-byte sequence back to its start).
* `BlockEditorCaret.Resolve(observation, composition)` — returns the selection only if `observation.TextLength == composition.Length`; otherwise `null`.
* `BlockEditorCaret.InsertAtEditorPosition(composition, observation, insertion, maxBytes)` — resolves; inserts via the existing `BlockTextEditor.Insert`; returns `BlockInsertOutcome(Text, Selection, Inserted, Truncated, UsedEndFallback)`. On a rejected insertion the text **and selection** are returned untouched.
* `BlockEditorCaret.ToByteOffset` — char index → UTF-8 byte offset, never inside a surrogate pair.

`BlockTextEditor.Insert` (existing) received two small hardening changes:

1. A caret between the halves of a surrogate pair snaps back; a selection widens to cover the pair — an insertion can no longer split an astral character.
2. If **nothing** fits the byte budget the call is now a clean rejection. Previously, with a selection and a budget of 1–2 bytes against a 3-byte block, it deleted the selection and inserted nothing (the docstring already promised "original text returned unchanged"; the code did not do that in this corner).

`BlockLettersOperatorPanel` (only Block Letters code touched):

* `selectionStartChars` / `selectionEndChars` / `hasKnownCursor` replaced by `BlockEditorObservation? observation`.
* `CallbackAlways` now calls `BlockEditorCaret.Observe(data.BufTextSpan, data.CursorPos, data.SelectionStart, data.SelectionEnd)`.
* `InsertGlyph` calls `InsertAtEditorPosition`, writes `composition` **directly and synchronously** (the earlier accepted design is preserved — no deferred Apply, staging buffer, modal or multi-step flow), sets the observation to the resulting caret, and queues the focus/caret restoration below.
* Clear and venue switch reset `observation` and `pendingRestore`.

## 9. Fallback behavior

If there is no trustworthy caret — never interacted with the box this session, Clear, venue switch, or an observation whose text length no longer matches the composition — the block is appended at **the end of the current composition**. The fallback never uses a position remembered from a previous insertion (`An_observation_of_a_different_text_is_not_trusted…` proves it). It is used only when accurate caret information is genuinely unavailable; it is not a blanket "always append".

## 10. Focus / caret restoration

Audited and implemented, local to Block Letters (no shared component touched):

* After an insertion the panel sets a one-shot `pendingRestore` (target selection, expected text length, expiry `frame+4`, `FocusRequested=false`).
* Next `DrawEditor` call: if unexpired and not yet requested → `ImGui.SetKeyboardFocusHere()` immediately before the `InputTextMultiline`, then marks `FocusRequested`. Expired requests are dropped. It is never re-issued, so there is no focus-stealing loop.
* Verified in native ImGui: focus takes effect one frame later, and a freshly focused multiline box starts with its caret at **byte 0** — so restoration of the caret is required, not optional. In that first active `CallbackAlways`, and only if the callback's text length equals the expected length, the panel sets `CursorPos`, `SelectionStart`, `SelectionEnd` (all three, because the selection pair does not follow the caret) and clears the request. The caret therefore lands immediately after the inserted block; typing and further block clicks continue from there with no click into the box.
* The mutation itself never goes through this callback (per the standing lesson from the earlier live-QA fix: external insertion must not depend on `CallbackAlways`). The callback only places the caret.
* A rejected insertion restores the *original* selection rather than collapsing it.

## 11. Unicode / index conversion handling

Byte↔char conversion is done only in `BlockEditorCaret`/`Utf8Offsets`; offsets inside a multi-byte sequence snap to its start; char indices inside a surrogate pair snap to the pair edge (back for a caret / selection start, forward for a selection end); `Rune`-based fitting in `BlockTextEditor` already prevents splitting when truncating. Block glyphs before/after the caret are preserved byte-for-byte.

## 12. Chat-byte / length implications

Unchanged policy and limits (Chat 500, Party Finder comment 192, Macro line 181, UTF-8 bytes). Insertion runs through the same `BlockTextEditor.Insert` budget: `budget = max − bytes(prefix) − bytes(suffix)`, so **the bytes of a replaced selection are counted as freed**; a block that does not fit is rejected whole (text and selection unchanged), never truncated or corrupted. The only behavior change is the corner in §8 (delete-selection-and-insert-nothing → clean rejection).

### Observed, NOT changed (out of scope — reported for your decision)

Verified against native ImGui: the `CallbackCharFilter` event carries only `EventChar` — `BufTextSpan` is empty and the selection is 0 (trace above: `CHARFILTER char=a len=0 selStart=0 selEnd=0`). The typed/pasted-character budget check in `EditorCallback` therefore evaluates against an empty buffer and can never truncate: typed or pasted text is not stopped at the limit; instead the composition goes over-limit and the existing over-limit warning + disabled Copy apply. This contradicts the comment above that branch and the sentence in `USER_MANUAL.md` §15 ("input simply stops accepting more once you're at the limit"), and `BLOCK_LETTERS_IMPLEMENTATION.md` §8. The work-package rule was to preserve existing limit behavior and not change unrelated chat-limit policy, so it was left exactly as it is; only the comment now flags it. Palette insertions **are** budget-limited (they go through `Insert` with the real composition).

## 13. Tests added

`tests/VenueOS.Services.Tests/BlockLettersCaretTests.cs` — **31 new tests** (existing Block Letters tests untouched; 86 Block Letters tests total pass). Coverage against the requested list: (1) empty + block; (2) text + block at end; (3) block → type → block lands after typed text, using the *captured real ImGui numbers*, plus a test documenting that the old reading yields index 1; (4) beginning; (5) middle, including middle-after-typing; (6) consecutive blocks; (7) type-after-block sequence; (8) forward and backward selection replacement; (9) selection at start; (10) at end; (11) whole-text; (12) no-observation fallback; (13) stale/previous position never reused; (14) equal pair = caret; (15) non-ASCII before; (16) non-ASCII after; (17) no surrogate / mid-sequence split (byte and char level); (18) existing glyphs stay intact; (19) resulting caret in chars and in bytes; (20) byte accounting, selection-freed bytes, non-fitting block leaves text and selection unchanged, over-limit composition, exact-fill.

Mutation check: temporarily restoring the old "read the selection pair" logic made 8 of the new tests fail (including the original-reproduction test); it was reverted.

## 14. What automated tests prove

* The insertion position/replacement, index conversion (bytes↔chars, surrogates), fallback selection, resulting caret and byte-budget accounting are correct, for the exact callback numbers ImGui produces.
* Supplementary, **not a repository test**: a throwaway harness (kept in the session scratchpad, not committed) ran the real native `cimgui.dll` (ImGui 1.88) headlessly with simulated mouse/keyboard/character events and a line-for-line mirror of the panel's callback / `InsertGlyph` / focus-restoration flow using the real `BlockEditorCaret`. All 14 scenarios passed on both the `dev` and `15.0.3.5` Dalamud hooks: block→type→block with no re-click; block-type ×3; middle; beginning; reverse and forward selection replacement; select-all; non-ASCII around the caret; typing continuing immediately after each block with no click; no-observation fallback. This is strong evidence about ImGui semantics but is **not FFXIV**.

## 15. What automated tests cannot prove

Behavior inside the live game client: that Dalamud's ImGui build/version and input plumbing in FFXIV behave like the headless run; that `SetKeyboardFocusHere` after a button click behaves in the real (possibly detached / Auto-Pop-Out) window without fighting game input capture; that the selection highlight and caret visually restore; scroll position after long text; glyph rendering; and the natural operator feel of type → block → type.

## 16. Live QA checklist (FFXIV)

**A — Original reproduction.** Clear the composition. Insert a block. Type several ordinary characters after it. *Without clicking the end*, insert another block. PASS: it appears after the newly typed characters, not directly after the first block.

**B — Repeated mixed editing.** Repeat `block → type → block → type → block`. PASS: every insertion is at the logical caret with no manual caret repair.

**C — Middle insertion.** Type ordinary text, click into the middle, insert a block. PASS: it appears at that caret — not at the end, not after the previous block. Then type a character: it appears right after the block.

**D — Beginning insertion.** Put the caret at the very beginning (Home / click), insert a block. PASS: it appears at the beginning.

**E — Selection replacement.** Select several ordinary characters (drag, Shift+arrows, and Ctrl+A each), insert a block. PASS: the selection is replaced by the block and the caret lands after it. If the selection does not survive the click in the live client, record that — the implemented behavior then falls back per §9 and this is a limitation to report, not a silent pass.

**F — Unicode.** Use some non-ASCII text (é, ñ, ü, ideographs, an emoji) around the caret; insert blocks before, inside and after it. PASS: no corruption, broken characters or misplaced insertion.

**G — Continue typing after insertion.** Insert a block, then immediately type ordinary text without clicking. PASS: typing continues right after the inserted block. Also confirm the focus is taken exactly once (no flicker/loop, no fight with other windows), and that clicking somewhere else in the box afterwards moves the caret there normally.

**H — Byte limit.** Build a composition close to the limit (Chat 500 / Macro 181 / PF 192) and insert/replace. PASS: existing limit behavior stands — a block that does not fit is rejected whole (text and selection unchanged), nothing is silently truncated or corrupted; a selection replacement that frees enough bytes succeeds. (Also note the pre-existing typing/paste behavior in §12.)

**I — Reload / regression.** `/xlreload`. Block Letters still opens and operates normally; default-destination setting and the never-persisted composition behave as before; Copy/Clear/destination switching unchanged; venue switch still clears the composition.

## 17. Remaining limitations

* Live-client behavior unverified (§15).
* If a block button is activated by keyboard/gamepad navigation while the box is still the active item, ImGui keeps its own edit buffer for the active box and would overwrite the external mutation — a pre-existing property of the direct-mutation design, out of scope here (mouse-driven clicks deactivate the box first).
* Typed/pasted-text byte limit is not enforced at typing time (§12) — pre-existing, left as is, needs your decision.
* `BLOCK_LETTERS_IMPLEMENTATION.md` still describes the older designs historically; this report supersedes its caret/selection description.

## Files

Modified: `src/VenueOS.Modules.Operations/BlockLetters/BlockLettersTextEngine.cs`, `src/VenueOS.Plugin/BlockLetters/BlockLettersOperatorPanel.cs`, `docs/POST_0.3.7_TRAINING_AUDIT.md` (row 6 + test count), `docs/USER_MANUAL.md` (one sentence in §15 Composing).
Created: `src/VenueOS.Modules.Operations/BlockLetters/BlockEditorCaret.cs`, `tests/VenueOS.Services.Tests/BlockLettersCaretTests.cs`, this report.
