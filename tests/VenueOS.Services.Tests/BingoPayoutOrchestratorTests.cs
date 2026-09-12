using System.Net;
using System.Text;
using VenueOS.Modules.Operations.Bingo;

namespace VenueOS.Services.Tests;

public sealed class BingoPayoutOrchestratorTests
{
    private static readonly BingoConnectionSettings Connection = new("https://bingo.test", RoomKey: "rk");

    [Fact] public async Task Normal_payout_confirms_exactly_once_and_reaches_done()
    {
        var attemptIds = new List<string>();
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts"))
            {
                var id = $"attempt-{attemptIds.Count + 1}";
                attemptIds.Add(id);
                return Json($"{{\"attemptId\":\"{id}\",\"payoutId\":\"p1\",\"amount\":500000,\"status\":\"pending\"}}");
            }
            return Json("{\"attemptId\":\"attempt-1\",\"status\":\"confirmed\",\"note\":\"Trade complete.\",\"obligation\":{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Alice\",\"totalOwed\":500000,\"confirmedPaid\":500000,\"outstanding\":0,\"status\":\"paid\"}}");
        });
        var automation = new FakeAutomation(_ => BingoTradeResult.Confirmed("Trade complete."));
        var orchestrator = new BingoPayoutOrchestrator(automation, new VenueBingoClient(new HttpClient(handler)));

        var result = await orchestrator.RunAsync(Connection, "ROOM", new("p1", "seed1", "Alice", 500_000, 0, 500_000, "open"), "Alice@Balmung", 1_000_000, default);

        Assert.True(result.FullyPaid);
        Assert.Equal(0, result.Outstanding);
        Assert.Single(result.Attempts);
        Assert.Equal(BingoPayoutStage.Done, result.FinalStage);
        Assert.Equal(1, automation.CallCount);
    }

    [Fact] public async Task Canceled_engine_outcome_marks_nothing_paid()
    {
        string? transitionStatus = null;
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts")) return Json("{\"attemptId\":\"attempt-1\",\"payoutId\":\"p1\",\"amount\":500000,\"status\":\"pending\"}");
            var body = await request.Content!.ReadAsStringAsync();
            transitionStatus = body.Contains("\"canceled\"") ? "canceled" : "unexpected";
            return Json("{\"attemptId\":\"attempt-1\",\"status\":\"canceled\",\"note\":null,\"obligation\":{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Alice\",\"totalOwed\":500000,\"confirmedPaid\":0,\"outstanding\":500000,\"status\":\"open\"}}");
        });
        var automation = new FakeAutomation(_ => BingoTradeResult.Canceled("Trade canceled."));
        var orchestrator = new BingoPayoutOrchestrator(automation, new VenueBingoClient(new HttpClient(handler)));

        var result = await orchestrator.RunAsync(Connection, "ROOM", new("p1", "seed1", "Alice", 500_000, 0, 500_000, "open"), "Alice@Balmung", 1_000_000, default);

        Assert.False(result.FullyPaid);
        Assert.Equal(BingoPayoutStage.Canceled, result.FinalStage);
        Assert.Equal("canceled", transitionStatus);
        Assert.Single(result.Attempts);
    }

    [Fact] public async Task Ambiguous_engine_outcome_transitions_to_ambiguous_and_creates_no_further_attempts()
    {
        var attemptCreations = 0;
        string? transitionStatus = null;
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts")) { attemptCreations++; return Json("{\"attemptId\":\"attempt-1\",\"payoutId\":\"p1\",\"amount\":500000,\"status\":\"pending\"}"); }
            var body = await request.Content!.ReadAsStringAsync();
            transitionStatus = body.Contains("\"ambiguous\"") ? "ambiguous" : "unexpected";
            return Json("{\"attemptId\":\"attempt-1\",\"status\":\"ambiguous\",\"note\":null,\"obligation\":{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Alice\",\"totalOwed\":500000,\"confirmedPaid\":0,\"outstanding\":500000,\"status\":\"open\"}}");
        });
        var automation = new FakeAutomation(_ => BingoTradeResult.Ambiguous("Timed out waiting for a chat completion signal."));
        var orchestrator = new BingoPayoutOrchestrator(automation, new VenueBingoClient(new HttpClient(handler)));

        var result = await orchestrator.RunAsync(Connection, "ROOM", new("p1", "seed1", "Alice", 500_000, 0, 500_000, "open"), "Alice@Balmung", 1_000_000, default);

        Assert.False(result.FullyPaid); // ambiguous does NOT mean unpaid, but it must never be reported as fully paid either
        Assert.Equal(BingoPayoutStage.Ambiguous, result.FinalStage);
        Assert.Equal("ambiguous", transitionStatus);
        Assert.Equal(1, attemptCreations); // never auto-retried into a second attempt
    }

    [Fact] public async Task Abort_mid_flight_stops_further_chunk_creation()
    {
        var attemptCreations = 0;
        string? transitionStatus = null;
        BingoPayoutOrchestrator? orchestrator = null;
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts")) { attemptCreations++; return Json($"{{\"attemptId\":\"attempt-{attemptCreations}\",\"payoutId\":\"p1\",\"amount\":1000000,\"status\":\"pending\"}}"); }
            var body = await request.Content!.ReadAsStringAsync();
            transitionStatus = body.Contains("\"ambiguous\"") ? "ambiguous" : "unexpected";
            return Json("{\"attemptId\":\"attempt-1\",\"status\":\"ambiguous\",\"note\":null,\"obligation\":{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Alice\",\"totalOwed\":2000000,\"confirmedPaid\":0,\"outstanding\":2000000,\"status\":\"open\"}}");
        });
        // Simulates the operator clicking Abort WHILE the in-game trade is still in flight: the engine happens to
        // still report Confirmed, but the orchestrator must never trust that once abortRequested is set, and must
        // never create a second chunk for the remaining balance.
        var automation = new FakeAutomation(_ => { orchestrator!.Abort(); return BingoTradeResult.Confirmed("Trade complete."); });
        orchestrator = new BingoPayoutOrchestrator(automation, new VenueBingoClient(new HttpClient(handler)));

        var result = await orchestrator.RunAsync(Connection, "ROOM", new("p1", "seed1", "Alice", 2_000_000, 0, 2_000_000, "open"), "Alice@Balmung", 1_000_000, default);

        Assert.False(result.FullyPaid);
        Assert.Equal(BingoPayoutStage.Ambiguous, result.FinalStage);
        Assert.Equal("ambiguous", transitionStatus); // never confirmed, despite the engine's outcome
        Assert.Equal(1, attemptCreations); // no second chunk was ever created
        Assert.True(automation.AbortCalled);
    }

    [Fact] public async Task Multi_chunk_payout_creates_exactly_two_attempts_and_rederives_the_second_amount_from_the_backend()
    {
        var chunkAmountsSent = new List<int>();
        var outstandingSequence = new Queue<int>([750_000, 0]); // after attempt 1 confirms: 750,000 remaining; after attempt 2: fully paid
        var attemptCount = 0;
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts"))
            {
                attemptCount++;
                var body = await request.Content!.ReadAsStringAsync();
                var marker = "\"amount\":"; var start = body.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                var end = body.IndexOf(',', start);
                chunkAmountsSent.Add(int.Parse(body[start..end]));
                return Json($"{{\"attemptId\":\"attempt-{attemptCount}\",\"payoutId\":\"p1\",\"amount\":{chunkAmountsSent[^1]},\"status\":\"pending\"}}");
            }
            var nextOutstanding = outstandingSequence.Dequeue();
            var confirmedPaid = 1_750_000 - nextOutstanding;
            return Json($"{{\"attemptId\":\"attempt-{attemptCount}\",\"status\":\"confirmed\",\"note\":null,\"obligation\":{{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Alice\",\"totalOwed\":1750000,\"confirmedPaid\":{confirmedPaid},\"outstanding\":{nextOutstanding},\"status\":\"{(nextOutstanding == 0 ? "paid" : "open")}\"}}}}");
        });
        var automation = new FakeAutomation(_ => BingoTradeResult.Confirmed("Trade complete."));
        var orchestrator = new BingoPayoutOrchestrator(automation, new VenueBingoClient(new HttpClient(handler)));

        var result = await orchestrator.RunAsync(Connection, "ROOM", new("p1", "seed1", "Alice", 1_750_000, 0, 1_750_000, "open"), "Alice@Balmung", 1_000_000, default);

        Assert.True(result.FullyPaid);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal([1_000_000, 750_000], chunkAmountsSent); // second chunk's amount came from the backend's own reported outstanding (750,000), never a locally-derived 750,000 that happened to also be correct
    }

    [Fact] public async Task Wrong_target_failure_leaves_paid_and_outstanding_unchanged_and_creates_no_further_attempts()
    {
        var attemptCreations = 0;
        string? transitionStatus = null;
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts")) { attemptCreations++; return Json("{\"attemptId\":\"attempt-1\",\"payoutId\":\"p1\",\"amount\":500000,\"status\":\"pending\"}"); }
            var body = await request.Content!.ReadAsStringAsync();
            transitionStatus = body.Contains("\"failed\"") ? "failed" : "unexpected";
            return Json("{\"attemptId\":\"attempt-1\",\"status\":\"failed\",\"note\":null,\"obligation\":{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Alice\",\"totalOwed\":500000,\"confirmedPaid\":0,\"outstanding\":500000,\"status\":\"open\"}}");
        });
        // Simulates the engine's own pinned-target verification rejecting a mismatched Name@HomeWorld (wrong world,
        // wrong name, or no target at all all surface through the engine as Failed with a detail — never guessed
        // Confirmed and never silently retried).
        var automation = new FakeAutomation(_ => BingoTradeResult.Failed("The currently targeted player does not match the pinned winner (Alice@Balmung)."));
        var orchestrator = new BingoPayoutOrchestrator(automation, new VenueBingoClient(new HttpClient(handler)));

        var result = await orchestrator.RunAsync(Connection, "ROOM", new("p1", "seed1", "Alice", 500_000, 0, 500_000, "open"), "Alice@Balmung", 1_000_000, default);

        Assert.False(result.FullyPaid);
        Assert.Equal(500_000, result.Outstanding); // unchanged — no partial/ambiguous credit for a rejected target
        Assert.Equal(BingoPayoutStage.Failed, result.FinalStage);
        Assert.Equal("failed", transitionStatus);
        Assert.Equal(1, attemptCreations); // never auto-retried into a second attempt
    }

    [Fact] public async Task Engine_exception_during_the_attempt_is_caught_and_transitions_to_ambiguous_never_paid()
    {
        var attemptCreations = 0;
        string? transitionStatus = null;
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts")) { attemptCreations++; return Json("{\"attemptId\":\"attempt-1\",\"payoutId\":\"p1\",\"amount\":500000,\"status\":\"pending\"}"); }
            var body = await request.Content!.ReadAsStringAsync();
            transitionStatus = body.Contains("\"ambiguous\"") ? "ambiguous" : "unexpected";
            return Json("{\"attemptId\":\"attempt-1\",\"status\":\"ambiguous\",\"note\":null,\"obligation\":{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Alice\",\"totalOwed\":500000,\"confirmedPaid\":0,\"outstanding\":500000,\"status\":\"open\"}}");
        });
        // An automation implementation that throws instead of returning a result (e.g. a defect that lets an
        // exception escape the engine's own boundary) must never propagate out of RunAsync, never mark anything
        // paid, and must still reconcile the server-created attempt rather than leaving it "pending" forever.
        var automation = new FakeAutomation(_ => throw new InvalidOperationException("Not on main thread!"));
        var orchestrator = new BingoPayoutOrchestrator(automation, new VenueBingoClient(new HttpClient(handler)));

        var result = await orchestrator.RunAsync(Connection, "ROOM", new("p1", "seed1", "Alice", 500_000, 0, 500_000, "open"), "Alice@Balmung", 1_000_000, default);

        Assert.False(result.FullyPaid);
        Assert.Equal(500_000, result.Outstanding);
        Assert.Equal(BingoPayoutStage.Ambiguous, result.FinalStage);
        Assert.Equal("ambiguous", transitionStatus);
        Assert.Equal(1, attemptCreations);
    }

    [Fact] public async Task Canceled_before_a_result_was_observed_transitions_to_ambiguous_and_never_double_creates()
    {
        var attemptCreations = 0;
        string? transitionStatus = null;
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts")) { attemptCreations++; return Json("{\"attemptId\":\"attempt-1\",\"payoutId\":\"p1\",\"amount\":500000,\"status\":\"pending\"}"); }
            var body = await request.Content!.ReadAsStringAsync();
            transitionStatus = body.Contains("\"ambiguous\"") ? "ambiguous" : "unexpected";
            return Json("{\"attemptId\":\"attempt-1\",\"status\":\"ambiguous\",\"note\":null,\"obligation\":{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Alice\",\"totalOwed\":500000,\"confirmedPaid\":0,\"outstanding\":500000,\"status\":\"open\"}}");
        });
        // Models a cancellation that reaches the engine call while it's genuinely in flight (e.g. a stale
        // main-thread verification callback the operator aborted out from under) — the awaited call throws
        // OperationCanceledException, never trusted as "unpaid", never auto-retried.
        var automation = new FakeAutomation(_ => throw new OperationCanceledException());
        var orchestrator = new BingoPayoutOrchestrator(automation, new VenueBingoClient(new HttpClient(handler)));

        var result = await orchestrator.RunAsync(Connection, "ROOM", new("p1", "seed1", "Alice", 500_000, 0, 500_000, "open"), "Alice@Balmung", 1_000_000, default);

        Assert.False(result.FullyPaid);
        Assert.Equal(BingoPayoutStage.Ambiguous, result.FinalStage);
        Assert.Equal("ambiguous", transitionStatus);
        Assert.Equal(1, attemptCreations);
    }

    [Fact] public async Task Manual_retry_after_a_failed_attempt_begins_cleanly_and_does_not_double_count()
    {
        var attemptCreations = 0;
        var callCount = 0;
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts")) { attemptCreations++; return Json($"{{\"attemptId\":\"attempt-{attemptCreations}\",\"payoutId\":\"p1\",\"amount\":500000,\"status\":\"pending\"}}"); }
            callCount++;
            // First transition (from the failed first attempt) reports outstanding still 500,000; the retry's own
            // confirmed transition reports it fully paid.
            return callCount == 1
                ? Json("{\"attemptId\":\"attempt-1\",\"status\":\"failed\",\"note\":null,\"obligation\":{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Alice\",\"totalOwed\":500000,\"confirmedPaid\":0,\"outstanding\":500000,\"status\":\"open\"}}")
                : Json("{\"attemptId\":\"attempt-2\",\"status\":\"confirmed\",\"note\":null,\"obligation\":{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Alice\",\"totalOwed\":500000,\"confirmedPaid\":500000,\"outstanding\":0,\"status\":\"paid\"}}");
        });
        var outcomes = new Queue<BingoTradeResult>([BingoTradeResult.Failed("Trade window did not open in time."), BingoTradeResult.Confirmed("Trade complete.")]);
        var automation = new FakeAutomation(_ => outcomes.Dequeue());
        var orchestrator = new BingoPayoutOrchestrator(automation, new VenueBingoClient(new HttpClient(handler)));
        var obligation = new BingoPayoutObligation("p1", "seed1", "Alice", 500_000, 0, 500_000, "open");

        var firstAttempt = await orchestrator.RunAsync(Connection, "ROOM", obligation, "Alice@Balmung", 1_000_000, default);
        Assert.False(firstAttempt.FullyPaid);
        Assert.Equal(BingoPayoutStage.Failed, firstAttempt.FinalStage);

        // The operator corrects the problem and retries — a fresh RunAsync call on the SAME orchestrator instance
        // must start cleanly from Stage.LocateWinner, not resume/contaminate the prior failed attempt's state.
        var retry = await orchestrator.RunAsync(Connection, "ROOM", obligation, "Alice@Balmung", 1_000_000, default);

        Assert.True(retry.FullyPaid);
        Assert.Equal(0, retry.Outstanding);
        Assert.Equal(BingoPayoutStage.Done, retry.FinalStage);
        Assert.Equal(2, attemptCreations); // one per RunAsync call — never doubled up
    }

    [Fact] public async Task Gil_entry_failure_leaves_the_full_obligation_outstanding_and_creates_no_further_attempts()
    {
        // Regression guard for the exact live incident (BINGO_PAYOUT_GIL_ENTRY_HOTFIX.md): Maya Baker, owed
        // 7,750,000, first chunk 1,000,000, engine reports "Failed — Could not enter the gil amount into the
        // trade." — the orchestrator must treat this exactly like any other pre-trade Failed outcome: no ledger
        // mutation beyond the backend's own "failed" transition, and no second chunk created.
        var attemptCreations = 0;
        string? transitionStatus = null;
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts")) { attemptCreations++; return Json("{\"attemptId\":\"attempt-1\",\"payoutId\":\"p1\",\"amount\":1000000,\"status\":\"pending\"}"); }
            var body = await request.Content!.ReadAsStringAsync();
            transitionStatus = body.Contains("\"failed\"") ? "failed" : "unexpected";
            return Json("{\"attemptId\":\"attempt-1\",\"status\":\"failed\",\"note\":null,\"obligation\":{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Maya Baker\",\"totalOwed\":7750000,\"confirmedPaid\":0,\"outstanding\":7750000,\"status\":\"open\"}}");
        });
        var automation = new FakeAutomation(_ => BingoTradeResult.Failed("Could not enter the gil amount into the trade."));
        var orchestrator = new BingoPayoutOrchestrator(automation, new VenueBingoClient(new HttpClient(handler)));

        var result = await orchestrator.RunAsync(Connection, "ROOM", new("p1", "seed1", "Maya Baker", 7_750_000, 0, 7_750_000, "open"), "Maya Baker@Rafflesia", 1_000_000, default);

        Assert.False(result.FullyPaid);
        Assert.Equal(7_750_000, result.Outstanding); // unchanged — a pre-trade gil-entry failure moved no gil
        Assert.Equal(BingoPayoutStage.Failed, result.FinalStage);
        Assert.Equal("failed", transitionStatus);
        Assert.Equal(1, attemptCreations);
    }

    [Fact] public async Task A_freshly_constructed_orchestrator_resumes_purely_from_backend_reported_outstanding()
    {
        // Donor-accounting regression guard (BINGO_PAYOUT_GIL_ENTRY_HOTFIX.md §35): the donor's known defect was
        // losing track of cumulative paid/outstanding in its own process memory. Simulates "recreating the payout
        // engine" (a plugin reload between chunks, or a second host taking over) by using a BRAND NEW orchestrator
        // instance — one that never saw the first chunk get confirmed — and proving it still completes the
        // obligation correctly using only the values the (fake) backend hands it, with no in-memory carryover
        // required or possible.
        var attemptCreations = 0;
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts")) { attemptCreations++; return Json($"{{\"attemptId\":\"attempt-{attemptCreations}\",\"payoutId\":\"p1\",\"amount\":6750000,\"status\":\"pending\"}}"); }
            return Json("{\"attemptId\":\"attempt-1\",\"status\":\"confirmed\",\"note\":null,\"obligation\":{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Maya Baker\",\"totalOwed\":7750000,\"confirmedPaid\":7750000,\"outstanding\":0,\"status\":\"paid\"}}");
        });
        var automation = new FakeAutomation(_ => BingoTradeResult.Confirmed("Trade complete."));
        // A fresh orchestrator, constructed with no knowledge of any prior attempt — the obligation passed in is
        // exactly what a fresh VenueBingoService.SyncPayoutsAsync() re-fetch would return: 1,000,000 already
        // confirmedPaid from an earlier session, 6,750,000 still outstanding.
        var orchestrator = new BingoPayoutOrchestrator(automation, new VenueBingoClient(new HttpClient(handler)));

        var result = await orchestrator.RunAsync(Connection, "ROOM", new("p1", "seed1", "Maya Baker", 7_750_000, 1_000_000, 6_750_000, "open"), "Maya Baker@Rafflesia", 10_000_000, default);

        Assert.True(result.FullyPaid);
        Assert.Equal(0, result.Outstanding);
        Assert.Equal(1, attemptCreations); // exactly one chunk for the remaining 6,750,000 — nothing re-derived locally
    }

    [Fact] public async Task Ready_confirm_discovery_failure_leaves_the_full_obligation_outstanding_and_creates_no_further_attempts()
    {
        // Regression guard for the exact live incident (BINGO_PAYOUT_READY_CONFIRM_HOTFIX.md): Rabid
        // Squirrel@Halicarnassus, owed 6,500,000, first chunk 1,000,000 stages successfully (hotfix #2's gil entry
        // and read-back both pass), but the engine cannot find/activate the Ready/Confirm control and reports
        // "Failed — Could not find a Ready/Confirm button on the trade window." Nothing has been submitted to the
        // game at this point, so — like gil-entry failure (hotfix #2) — this must be Failed, never Ambiguous, and
        // must leave the ledger untouched.
        var attemptCreations = 0;
        string? transitionStatus = null;
        var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/attempts")) { attemptCreations++; return Json("{\"attemptId\":\"attempt-1\",\"payoutId\":\"p1\",\"amount\":1000000,\"status\":\"pending\"}"); }
            var body = await request.Content!.ReadAsStringAsync();
            transitionStatus = body.Contains("\"failed\"") ? "failed" : "unexpected";
            return Json("{\"attemptId\":\"attempt-1\",\"status\":\"failed\",\"note\":null,\"obligation\":{\"payoutId\":\"p1\",\"winnerSeed\":\"seed1\",\"winnerName\":\"Rabid Squirrel\",\"totalOwed\":6500000,\"confirmedPaid\":0,\"outstanding\":6500000,\"status\":\"open\"}}");
        });
        var automation = new FakeAutomation(_ => BingoTradeResult.Failed("Could not find a Ready/Confirm button on the trade window."));
        var orchestrator = new BingoPayoutOrchestrator(automation, new VenueBingoClient(new HttpClient(handler)));

        var result = await orchestrator.RunAsync(Connection, "ROOM", new("p1", "seed1", "Rabid Squirrel", 6_500_000, 0, 6_500_000, "open"), "Rabid Squirrel@Halicarnassus", 1_000_000, default);

        Assert.False(result.FullyPaid);
        Assert.Equal(6_500_000, result.Outstanding); // unchanged — nothing was ever submitted to the game
        Assert.Equal(BingoPayoutStage.Failed, result.FinalStage);
        Assert.Equal("failed", transitionStatus);
        Assert.Equal(1, attemptCreations);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request); }

    private sealed class FakeAutomation(Func<int, BingoTradeResult> respond) : IBingoPayoutAutomation
    {
        public string Status { get; private set; } = "Idle";
        public bool IsBusy { get; private set; }
        public int CallCount { get; private set; }
        public bool AbortCalled { get; private set; }
        public void Abort() { AbortCalled = true; IsBusy = false; Status = "Aborted"; }
        public Task<BingoTradeResult> ExecutePayoutAttemptAsync(string targetNameAndWorld, int amount, CancellationToken cancellationToken)
        {
            CallCount++; IsBusy = true;
            var result = respond(amount);
            IsBusy = false; Status = result.Outcome.ToString();
            return Task.FromResult(result);
        }
    }
}
