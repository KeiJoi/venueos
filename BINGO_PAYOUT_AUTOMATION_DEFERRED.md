# VenueBingo4All payout automation — status

**No longer deferred — live-verified as of 0.3.2.** A full forensic audit (`docs/BINGO_FORENSIC_AUDIT.md`) traced the standalone plugin's autopay to a specific, reproducible repeat-payout bug (its payout ledger is process-memory-only and is explicitly cleared on room rejoin, while the backend has never tracked payout state at all), and a safe reconstruction was implemented per the audit's recommended design (`docs/BINGO_V2_PROTOCOL.md` for the backend-authoritative payout ledger; `BingoPayoutOrchestrator`/`IBingoPayoutAutomation`/`BingoPayoutAutomationService` for the VenueOS state machine). Three targeted live-QA hotfix passes (`docs/BINGO_PAYOUT_MAIN_THREAD_HOTFIX.md`, `docs/BINGO_PAYOUT_GIL_ENTRY_HOTFIX.md`, `docs/BINGO_PAYOUT_READY_CONFIRM_HOTFIX.md`) then found and fixed every defect a real end-to-end live test surfaced, and a subsequent full live test completed successfully — see below.

What exists:
- A backend-authoritative payout ledger (`payout_obligations`/`payout_attempts` tables, idempotent attempt creation, and a guarded confirm transition that can never double-apply `confirmedPaid`) — this is what makes a crash/host-handoff resumable from the exact outstanding remainder instead of from scratch, and it is what actually recorded the live-tested payout below.
- `BingoPayoutOrchestrator` (`src/VenueOS.Modules.Operations/Bingo/BingoPayoutOrchestrator.cs`): pure, unit-tested orchestration enforcing "ambiguous does not mean unpaid" (no auto-retry on an inconclusive outcome), a durable per-attempt backend identity, and re-deriving outstanding from the backend after every confirmed chunk rather than a local counter.
- `BingoPayoutAutomationService` (`src/VenueOS.Plugin/Bingo/BingoPayoutAutomationService.cs`): the real, unsafe ECommons/FFXIVClientStructs trade-automation engine, gated behind the `IBingoPayoutAutomation` interface so the orchestrator is testable without a live game.

**Release status:** Bingo's core functionality — room/game lifecycle, players, paid/complimentary cards, short links, calling, the Called Numbers and Card Viewer windows, and the Payout Ledger display — has been production/enabled-by-default since 0.2.0. As of 0.3.2, automated payout itself has also completed successful live end-to-end testing and is no longer an unverified path.

## Live QA result (0.3.2)

A live test against the real FFXIV client completed a full payout, chunked across an obligation of 6,500,000 gil,
end to end:

- Pinned Name@HomeWorld target verification succeeded.
- The real FFXIV Trade window opened with the correct player.
- Gil correctly staged through the native FFXIV addon callbacks used by the engine (`ECommons.Automation.Callback.Fire`), with positive staged-amount verification before Trade submission each time.
- The Trade window's Ready/Confirm control was correctly located and activated.
- The post-Ready confirmation (SelectYesno) step completed correctly, gated on the dialog's own content matching the expected Trade-confirmation prompt.
- Real gil transferred successfully, in six confirmed 1,000,000 chunks plus one correctly-derived 500,000 remainder chunk (never another full 1,000,000 chunk past what was actually owed).
- The backend/server-authoritative ledger recorded every confirmed chunk and reconciled to the correct final state: `paid: 6,500,000`, `outstanding: 0`.
- Transaction history correctly retained an earlier `failed · 1,000,000` attempt (from before the hotfixes above) alongside the seven confirmed chunks — the failed attempt was never counted toward `paid`, never converted to `confirmed`, and never hidden once the obligation was fully paid. The attempt list is an audit history; the server-backed obligation summary is the authoritative accounting; they intentionally serve different purposes and are never merged.
- The displayed paid/outstanding total may lag briefly while VenueOS re-pulls the authoritative server ledger after a confirmed chunk — this is expected and intentional, not a bug: VenueOS never applies an optimistic local `paid += amount` update, precisely because the donor's own historical bug was exactly that kind of local, in-memory accounting drifting from reality.

**What remains genuinely unverified** (called out at each exact call site in `BingoPayoutAutomationService.cs`, per the hotfix reports' own LIVE VERIFICATION REQUIRED notes): the exact `"Trade complete."`/`"Trade canceled."` chat-text stability, the Ready/Confirm node index, and the SelectYesno prompt-text match are all English-client-only observations — non-English game clients have not been separately tested. Hosts running a non-English client should verify a small test payout before relying on automation.

Hosts should still treat an `Ambiguous` or `Failed` result as requiring the Payout Ledger's manual "Mark Paid"/"Mark Not Paid" reconciliation (or another established payout workflow) — this safety behavior is unchanged and is not something live-testing success ever relaxes.
