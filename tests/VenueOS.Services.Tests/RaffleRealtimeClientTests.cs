using System.Collections.Concurrent;
using VenueOS.Modules.Operations.Raffle;

namespace VenueOS.Services.Tests;

/// <summary>Exercises <see cref="RaffleRealtimeClient"/>'s connect/forward/reconnect plumbing against a fake
/// <see cref="IRaffleRealtimeTransport"/> — the real <see cref="WebSocketRaffleRealtimeTransport"/> can only be
/// verified live, per NEW_MODULE_GUIDE.md §30's "put the unsafe/engine boundary behind a small interface"
/// guidance. Mirrors <see cref="TournamentRealtimeClientTests"/>'s fake-transport pattern for the raffle
/// backend's simpler join/state/updated/spin protocol (no authenticate/subscribe split, no revision numbers).</summary>
public sealed class RaffleRealtimeClientTests
{
    [Fact]
    public async Task Received_messages_are_queued_in_order_for_the_tick_loop_to_drain()
    {
        var transport = new FakeTransport();
        transport.EnqueueIncoming(new("updated", WinnerName: "Ada"));
        transport.EnqueueIncoming(new("spin", WinnerName: "Bob"));
        var client = new RaffleRealtimeClient(() => transport);
        client.Start(new Uri("wss://raffle.test/ws"), "raffle-1", "viewer-token");
        await transport.WaitForMessagesConsumed();

        Assert.True(client.TryDequeue(out var first)); Assert.Equal("Ada", first.WinnerName);
        Assert.True(client.TryDequeue(out var second)); Assert.Equal("Bob", second.WinnerName);
        Assert.False(client.TryDequeue(out _));
        client.Stop();
    }

    [Fact]
    public async Task First_connect_does_not_raise_a_reconnect_signal_but_a_later_reconnect_does()
    {
        var transport = new FakeTransport();
        var client = new RaffleRealtimeClient(() => transport, TimeSpan.FromMilliseconds(5));
        client.Start(new Uri("wss://raffle.test/ws"), "raffle-1", "viewer-token");
        await transport.WaitForConnectAttempt(1);
        Assert.False(client.ConsumeReconnectSignal());

        transport.DropConnection();
        await transport.WaitForConnectAttempt(2);
        Assert.True(client.ConsumeReconnectSignal());
        Assert.False(client.ConsumeReconnectSignal(), "the signal must be consumed exactly once");
        client.Stop();
    }

    [Fact]
    public async Task A_join_frame_with_the_viewer_role_is_sent_on_every_connect()
    {
        var transport = new FakeTransport();
        var client = new RaffleRealtimeClient(() => transport);
        client.Start(new Uri("wss://raffle.test/ws"), "raffle-42", "the-token");
        await transport.WaitForConnectAttempt(1);
        client.Stop();

        // VenueOS never spins from here - it only ever joins as a read-only observer (see
        // RaffleRealtimeProtocol.Join's doc comment), so the role must always be "viewer", never "host".
        Assert.Contains(transport.SentMessages, message => message.Contains("\"type\":\"join\"") && message.Contains("raffle-42") && message.Contains("the-token") && message.Contains("\"role\":\"viewer\""));
    }

    [Fact]
    public void Stop_clears_any_queued_but_undrained_messages()
    {
        var transport = new FakeTransport();
        transport.EnqueueIncoming(new("updated", WinnerName: "Ada"));
        var client = new RaffleRealtimeClient(() => transport);
        client.Start(new Uri("wss://raffle.test/ws"), "raffle-1", "viewer-token");
        client.Stop();
        Assert.False(client.TryDequeue(out _));
        Assert.False(client.IsConnected);
    }

    private sealed class FakeTransport : IRaffleRealtimeTransport
    {
        private readonly ConcurrentQueue<RaffleRealtimeMessage> incoming = new();
        private int remaining;
        private int connectCount;
        private TaskCompletionSource dropSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> SentMessages = [];

        public void EnqueueIncoming(RaffleRealtimeMessage message) { incoming.Enqueue(message); Interlocked.Increment(ref remaining); }

        public void DropConnection() => Volatile.Read(ref dropSignal).TrySetResult();

        public Task ConnectAsync(Uri socketUri, CancellationToken cancellationToken) { Interlocked.Increment(ref connectCount); return Task.CompletedTask; }

        public Task SendAsync(object message, CancellationToken cancellationToken) { lock (SentMessages) SentMessages.Add(System.Text.Json.JsonSerializer.Serialize(message)); return Task.CompletedTask; }

        public async Task<RaffleRealtimeMessage?> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (incoming.TryDequeue(out var message)) { Interlocked.Decrement(ref remaining); return message; }
            var currentDropSignal = dropSignal;
            using var registration = cancellationToken.Register(() => currentDropSignal.TrySetCanceled(cancellationToken));
            await currentDropSignal.Task.ConfigureAwait(false);
            Interlocked.CompareExchange(ref dropSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), currentDropSignal);
            return null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task WaitForMessagesConsumed() { for (var i = 0; i < 200 && Volatile.Read(ref remaining) > 0; i++) await Task.Delay(10); await Task.Delay(20); }
        public async Task WaitForConnectAttempt(int count) { for (var i = 0; i < 200 && Volatile.Read(ref connectCount) < count; i++) await Task.Delay(10); }
    }
}
