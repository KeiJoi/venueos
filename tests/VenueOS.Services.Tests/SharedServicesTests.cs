using System.Numerics;
using VenueOS.Services;

namespace VenueOS.Services.Tests;

public sealed class SharedServicesTests
{
    [Fact] public void Scheduler_honors_cancellation_and_repeat() { var clock = new FakeClock(); var scheduler = new SchedulerService(clock); var runs = 0; using var cts = new CancellationTokenSource(); scheduler.Schedule(TimeSpan.FromSeconds(1), () => runs++, cancellationToken: cts.Token); cts.Cancel(); clock.Advance(1); scheduler.Tick(); Assert.Equal(0, runs); scheduler.Schedule(TimeSpan.FromSeconds(1), () => runs++, TimeSpan.FromSeconds(1)); clock.Advance(1); scheduler.Tick(); clock.Advance(1); scheduler.Tick(); Assert.Equal(2, runs); }
    [Fact] public async Task Chat_queue_is_paced_and_reports() { var clock = new FakeClock(); var sent = new List<string>(); var service = new ChatCommandService(clock, new InlineFrameworkDispatcher(), command => { sent.Add(command); return true; }, TimeSpan.FromSeconds(1)); service.Enqueue(new("/tell A hi")); service.Enqueue(new("/tell B hi")); await service.TickAsync(); await service.TickAsync(); Assert.Single(sent); clock.Advance(1); await service.TickAsync(); Assert.Equal(2, sent.Count); }
    [Fact] public void Presence_normalizes_identity_filters_and_emits_once() { var clock = new FakeClock(); var provider = new FakeProvider { Items = [new(" A   B ", " Balmung ", 9, 1, Vector3.Zero), new("No", "World", 2, 2, Vector3.Zero)] }; var presence = new PresenceService(clock, provider); var arrivals = 0; presence.Arrived += (guest, _, _) => { arrivals++; Assert.Equal("A B@BALMUNG", guest.Key); }; presence.Tick(new(1, Vector3.Zero, 10)); Assert.Equal(1, arrivals); clock.Advance(1); presence.Tick(new(1, Vector3.Zero, 10)); Assert.Equal(1, arrivals); provider.Items = []; clock.Advance(1); presence.Tick(new(1, Vector3.Zero, 10)); Assert.Empty(presence.Current); }
    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch; public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds); }
    private sealed class FakeProvider : IObjectSnapshotProvider { public IReadOnlyList<PlayerSnapshot> Items { get; set; } = []; public IReadOnlyList<PlayerSnapshot> Snapshot() => Items; }
}
