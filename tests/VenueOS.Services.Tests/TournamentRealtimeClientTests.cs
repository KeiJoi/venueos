using System.Collections.Concurrent;
using VenueOS.Modules.Operations.Tournament;

namespace VenueOS.Services.Tests;

/// <summary>Exercises <see cref="TournamentRealtimeClient"/>'s connect/forward/reconnect plumbing against a fake
/// <see cref="ITournamentRealtimeTransport"/> — the real <see cref="WebSocketTournamentRealtimeTransport"/> can only
/// be verified live, per NEW_MODULE_GUIDE.md §30's "put the unsafe/engine boundary behind a small interface"
/// guidance. This is the coverage that proves the client actually decodes and forwards frames — the donor's own
/// Dalamud WebSocket client (apps/dalamud/Services/TournamentEventClient.cs) was found dead/unwired specifically
/// because nothing exercised its receive loop end to end.</summary>
public sealed class TournamentRealtimeClientTests
{
    [Fact]
    public async Task Received_messages_are_queued_in_order_for_the_tick_loop_to_drain()
    {
        var transport = new FakeTransport();
        transport.EnqueueIncoming(new(1, "tournament.updated", "CODE", "t1", 2, null));
        transport.EnqueueIncoming(new(1, "tournament.updated", "CODE", "t1", 3, null));
        var client = new TournamentRealtimeClient(() => transport);
        client.Start(new Uri("wss://tournament.test/ws"), "token", "CODE");
        await transport.WaitForMessagesConsumed();

        Assert.True(client.TryDequeue(out var first)); Assert.Equal(2, first.Revision);
        Assert.True(client.TryDequeue(out var second)); Assert.Equal(3, second.Revision);
        Assert.False(client.TryDequeue(out _));
        client.Stop();
    }

    [Fact]
    public async Task First_connect_does_not_raise_a_reconnect_signal_but_a_later_reconnect_does()
    {
        var transport = new FakeTransport();
        var client = new TournamentRealtimeClient(() => transport, TimeSpan.FromMilliseconds(5));
        client.Start(new Uri("wss://tournament.test/ws"), "token", "CODE");
        await transport.WaitForConnectAttempt(1);
        Assert.False(client.ConsumeReconnectSignal());

        transport.DropConnection();
        await transport.WaitForConnectAttempt(2);
        Assert.True(client.ConsumeReconnectSignal());
        Assert.False(client.ConsumeReconnectSignal(), "the signal must be consumed exactly once");
        client.Stop();
    }

    [Fact]
    public async Task Authenticate_and_subscribe_frames_are_sent_on_every_connect()
    {
        var transport = new FakeTransport();
        var client = new TournamentRealtimeClient(() => transport);
        client.Start(new Uri("wss://tournament.test/ws"), "the-token", "CODE-1");
        await transport.WaitForConnectAttempt(1);
        client.Stop();

        Assert.Contains(transport.SentMessages, message => message.Contains("authenticate") && message.Contains("the-token"));
        Assert.Contains(transport.SentMessages, message => message.Contains("subscribe") && message.Contains("CODE-1"));
    }

    private sealed class FakeTransport : ITournamentRealtimeTransport
    {
        private readonly ConcurrentQueue<TournamentEventMessage> incoming = new();
        private int remaining;
        private int connectCount;
        private TaskCompletionSource dropSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> SentMessages = [];

        public void EnqueueIncoming(TournamentEventMessage message) { incoming.Enqueue(message); Interlocked.Increment(ref remaining); }

        /// <summary>Simulates the peer closing the socket: unblocks whichever ReceiveAsync call is currently
        /// pending (as null, matching a real closed-socket read) so the client's reconnect loop actually runs the
        /// backoff-and-retry path instead of staying parked in an idle receive forever.</summary>
        public void DropConnection() => Volatile.Read(ref dropSignal).TrySetResult();

        public Task ConnectAsync(Uri socketUri, CancellationToken cancellationToken) { Interlocked.Increment(ref connectCount); return Task.CompletedTask; }

        public Task SendAsync(object message, CancellationToken cancellationToken) { lock (SentMessages) SentMessages.Add(System.Text.Json.JsonSerializer.Serialize(message)); return Task.CompletedTask; }

        public async Task<TournamentEventMessage?> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (incoming.TryDequeue(out var message)) { Interlocked.Decrement(ref remaining); return message; }
            var currentDropSignal = dropSignal;
            using var registration = cancellationToken.Register(() => currentDropSignal.TrySetCanceled(cancellationToken));
            await currentDropSignal.Task.ConfigureAwait(false);
            // A single drop signal fires once; install a fresh one so the NEXT connection's idle receive can be
            // dropped again independently (mirrors a real socket being reconnected after a close).
            Interlocked.CompareExchange(ref dropSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), currentDropSignal);
            return null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task WaitForMessagesConsumed() { for (var i = 0; i < 200 && Volatile.Read(ref remaining) > 0; i++) await Task.Delay(10); await Task.Delay(20); }
        public async Task WaitForConnectAttempt(int count) { for (var i = 0; i < 200 && Volatile.Read(ref connectCount) < count; i++) await Task.Delay(10); }
    }
}
