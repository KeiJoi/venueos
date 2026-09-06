using System.Net;
using System.Text;
using System.Text.Json;
using VenueOS.Core;
using VenueOS.Modules.Operations;
using VenueOS.Modules.Operations.QuestionLibrary;
using VenueOS.Modules.Operations.Trivia;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Services.Tests;

public sealed class MairsTriviaClientTests
{
    [Fact] public async Task Login_preserves_v1_camel_case_and_required_authentication_headers()
    {
        string? body = null; var handler = new Handler(async request => { Assert.Equal("/v1/auth/login", request.RequestUri!.AbsolutePath); Assert.Equal("secret", request.Headers.GetValues("X-Server-Access-Password").Single()); body = await request.Content!.ReadAsStringAsync(); return Json("{\"accessToken\":\"access\",\"refreshToken\":\"refresh\",\"user\":{\"id\":\"0a1b2c3d-4e5f-6789-8abc-def012345678\",\"username\":\"Mair\",\"createdAt\":\"2026-01-01T00:00:00Z\"}}"); });
        var result = await new MairsTriviaClient(new HttpClient(handler)).LoginAsync(new("https://trivia.test", ServerAccessPassword: "secret"), "Mair", "password", default);
        Assert.True(result.Success); Assert.Contains("\"username\":\"Mair\"", body); Assert.Equal("access", result.Value!.AccessToken);
    }
    [Fact] public async Task Errors_are_read_as_dtos_without_echoing_credentials()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":{\"code\":\"invalid_token\",\"message\":\"Sign in again.\"}}") }));
        var result = await new MairsTriviaClient(new HttpClient(handler)).GetProfileAsync(new("https://trivia.test", AccessToken: "super-secret"), default);
        Assert.False(result.Success); Assert.Equal("invalid_token", result.Error!.Code); Assert.DoesNotContain("super-secret", result.Error.Message);
    }
    [Fact] public async Task Requests_cancel_cleanly()
    {
        var handler = new Handler((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith<HttpResponseMessage>(_ => throw new OperationCanceledException())); using var cancellation = new CancellationTokenSource();
        var task = new MairsTriviaClient(new HttpClient(handler)).HealthAsync(new("https://trivia.test"), cancellation.Token); cancellation.Cancel(); var result = await task;
        Assert.False(result.Success); Assert.Equal("cancelled", result.Error!.Code);
    }
    [Fact] public void Websocket_authentication_is_protocol_one_and_player_projection_has_no_answer_key()
    {
        var json = JsonSerializer.Serialize(TriviaWebSocketProtocol.CreateHostAuthentication("access"));
        Assert.Contains("\"protocolVersion\":1", json); Assert.Equal("wss", TriviaWebSocketProtocol.BuildUri("https://trivia.test")!.Scheme);
        Assert.Null(typeof(TriviaPlayerQuestion).GetProperty("CorrectAnswer"));
    }
    [Fact] public async Task Venue_switch_rejects_a_late_response_from_the_previous_venue_and_isolates_credentials()
    {
        var handler = new Handler((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith<HttpResponseMessage>(_ => throw new OperationCanceledException())); var profiles = new VenueProfileService(new InMemoryVenueStore(), new ModuleHost()); var first = profiles.Current; var second = profiles.Create("Second");
        // First venue deliberately has NO stored refresh token: Load() now auto-reconnects whenever one is present
        // (see MairsTriviaService.Load), and that auto-refresh would otherwise itself occupy the single-mutation
        // slot before this test's own explicit RefreshAsync() call below ever runs — this isolates the two.
        profiles.SaveModuleConfig(first.Id, "games.trivia", 1, new MairsTriviaSettings(new("https://one", AccessToken: "one", RefreshToken: null), "Game", new(1, 0, 0)));
        profiles.SaveModuleConfig(second.Id, "games.trivia", 1, new MairsTriviaSettings(new("https://two", AccessToken: "two", RefreshToken: "r2"), "Game", new(1, 0, 0)));
        var diagnostics = new DiagnosticsService(new ModuleHost(), profiles, new FakeClock());
        var service = new MairsTriviaService(new MairsTriviaClient(new HttpClient(handler)), profiles, new FakeQuestionSetRepository(), diagnostics);
        service.Load(first.Id); var request = service.RefreshAsync(); service.Load(second.Id); var refreshed = await request;
        Assert.False(refreshed); Assert.Equal("two", service.Settings.Connection.AccessToken); Assert.Equal("r2", service.Settings.Connection.RefreshToken);
    }
    [Fact] public void Question_sets_validate_the_standalone_contract()
    {
        var question = new TriviaQuestion(Guid.NewGuid(), "Question", "Correct", ["A", "B", "C"], null, []);
        var valid = new TriviaQuestionSet("fftrivia-question-set", 2, Guid.NewGuid(), "Set", "", "Mair", "1", ["General"], [], [question]);
        Assert.True(valid.IsValidHostSet()); Assert.False((valid with { SchemaVersion = 3 }).IsValidHostSet());
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action;
        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : this((request, _) => action(request)) { }
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) => this.action = action;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
    private sealed class FakeQuestionSetRepository : IQuestionSetRepository
    {
        private readonly Dictionary<Guid, TriviaQuestionSet> sets = [];
        public IReadOnlyList<QuestionLibraryIndexEntry> List() => [.. sets.Values.Select((s, i) => new QuestionLibraryIndexEntry(s.Id, s.Title, i, "", DateTimeOffset.UnixEpoch, QuestionSetValidator.Validate(s).Status))];
        public TriviaQuestionSet? Read(Guid id) => sets.GetValueOrDefault(id);
        public void Save(TriviaQuestionSet set) => sets[set.Id] = set;
        public bool Delete(Guid id, Func<Guid, bool> isInUse) { if (isInUse(id)) return false; return sets.Remove(id); }
        public TriviaQuestionSet Duplicate(Guid id, string? title = null) { var copy = sets[id].Duplicate(title); Save(copy); return copy; }
        public ImportCollisionInfo? CheckImportCollision(string fftriviaJson) => null;
        public TriviaQuestionSet ImportReplacing(string fftriviaJson) => throw new NotSupportedException();
        public TriviaQuestionSet ImportAsNew(string fftriviaJson, string? title = null) => throw new NotSupportedException();
        public string Export(Guid id) => "";
        public void Reorder(IReadOnlyList<Guid> orderedIdsInDesiredOrder) { }
        public IReadOnlyList<string> RebuildIndex() => [];
    }
}
