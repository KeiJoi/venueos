# VenueBingo4All payout automation — deferred

The current FFXIVBingo4All standalone contains an automatic payout path in its monolithic plugin: it chooses a target, sends `/trade`, observes live trade UI state, fills gil/chunks, accepts/locks/validates the trade, and tracks partial or mismatched payouts.

VenueOS deliberately does **not** include this capability in `games.bingo`. The standalone implementation depends on ECommons helpers, FFXIVClientStructs/addon access, ClickLib-style UI interaction, trade detection, timing/throttling, and direct live-game trade operations. Game UI/node changes, latency, wrong-target risk, and partial-payment states make this unsuitable for unreviewed migration.

Before any adoption, it needs a separate security and version-safety design: explicit target identity verification, fail-closed addon compatibility gates, manual abort/recovery controls, no blind click behavior, payout ledger/reconciliation, isolated automation adapter, and in-game validation across trade failure, cancellation, disconnect, partial-gil, and UI-update scenarios.
