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
