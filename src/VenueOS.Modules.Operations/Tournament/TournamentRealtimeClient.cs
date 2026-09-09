using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace VenueOS.Modules.Operations.Tournament;

/// <summary>The actual socket I/O boundary, kept behind a tiny interface per NEW_MODULE_GUIDE.md §30 so
/// <see cref="TournamentRealtimeClient"/>'s reconnect/backoff/message-forwarding logic can be unit tested with a
/// fake transport — a real <see cref="ClientWebSocket"/> can only ever be exercised against a live backend.
/// <see cref="ReceiveAsync"/> returns null exactly once to signal the socket closed (whether cleanly or the peer
/// dropped); the caller treats that identically to a thrown exception — both trigger a reconnect attempt.</summary>
public interface ITournamentRealtimeTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri socketUri, CancellationToken cancellationToken);
    Task SendAsync(object message, CancellationToken cancellationToken);
    Task<TournamentEventMessage?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>Real transport: a <see cref="ClientWebSocket"/> speaking the donor's version-1 authenticate/subscribe
/// frame protocol (<see cref="TournamentEventProtocol"/>). This is the part of the donor's own Dalamud client
/// (apps/dalamud/Services/TournamentEventClient.cs) the audit found to be dead code — constructed nowhere, and its
/// receive loop discarded every message it read instead of decoding and forwarding it. This implementation
/// actually decodes each frame and is genuinely wired into <see cref="TournamentControlService.Tick"/>.</summary>
public sealed class WebSocketTournamentRealtimeTransport : ITournamentRealtimeTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private ClientWebSocket? socket;

    public async Task ConnectAsync(Uri socketUri, CancellationToken cancellationToken)
    {
        var next = new ClientWebSocket();
        await next.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);
        socket = next;
    }

    public async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var current = socket;
        if (current is null || current.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await current.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TournamentEventMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var current = socket;
        if (current is null) return null;
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await current.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        stream.Position = 0;
        return stream.Length == 0 ? null : JsonSerializer.Deserialize<TournamentEventMessage>(stream, Json);
    }

    public async ValueTask DisposeAsync()
    {
        var current = socket;
        socket = null;
        if (current is null) return;
        try { if (current.State == WebSocketState.Open) await current.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).ConfigureAwait(false); } catch { /* best-effort close */ }
        current.Dispose();
    }
}

/// <summary>Owns the connect/authenticate/subscribe/reconnect lifecycle for one tournament's realtime stream.
/// <see cref="TournamentControlService.Tick"/> drains <see cref="TryDequeue"/> on the Dalamud framework thread every
/// frame — the background receive loop below never touches the module's own state directly, only this thread-safe
/// queue, so there is no race between the socket thread and Draw()/Tick(). REST full-state refetch remains the
/// authoritative recovery mechanism: <see cref="ConsumeReconnectSignal"/> tells the service exactly once per
/// reconnect that it should refetch full state via <see cref="TournamentControlClient.GetStateAsync"/> rather than
/// trusting the socket alone to have delivered everything it missed while disconnected.</summary>
public sealed class TournamentRealtimeClient(Func<ITournamentRealtimeTransport> transportFactory, TimeSpan? retryBackoffUnit = null)
{
    private readonly TimeSpan retryBackoffUnit = retryBackoffUnit ?? TimeSpan.FromSeconds(1);
    private readonly ConcurrentQueue<TournamentEventMessage> inbox = new();
    private CancellationTokenSource? loopCancellation;
    private volatile bool connected;
    private volatile bool reconnectSignal;

    public bool IsConnected => connected;

    public void Start(Uri socketUri, string accessToken, string tournamentCode)
    {
        Stop();
        var cancellation = new CancellationTokenSource();
        loopCancellation = cancellation;
        _ = RunAsync(socketUri, accessToken, tournamentCode, cancellation.Token);
    }

    public void Stop()
    {
        loopCancellation?.Cancel();
        loopCancellation?.Dispose();
        loopCancellation = null;
        connected = false;
        reconnectSignal = false;
        while (inbox.TryDequeue(out _)) { }
    }

    public bool TryDequeue(out TournamentEventMessage message) => inbox.TryDequeue(out message!);

    /// <summary>Returns true at most once per successful reconnect (never for the very first connect after
    /// <see cref="Start"/>, since the caller already just did a fresh REST load before starting the socket).</summary>
    public bool ConsumeReconnectSignal()
    {
        if (!reconnectSignal) return false;
        reconnectSignal = false;
        return true;
    }

    private async Task RunAsync(Uri socketUri, string accessToken, string tournamentCode, CancellationToken cancellationToken)
    {
        var hasConnectedOnce = false;
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            ITournamentRealtimeTransport? transport = null;
            try
            {
                transport = transportFactory();
                await transport.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);
                await transport.SendAsync(TournamentEventProtocol.Authenticate(accessToken), cancellationToken).ConfigureAwait(false);
                await transport.SendAsync(TournamentEventProtocol.Subscribe(tournamentCode, accessToken), cancellationToken).ConfigureAwait(false);
                connected = true;
                if (hasConnectedOnce) reconnectSignal = true;
                hasConnectedOnce = true;
                attempt = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var message = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    if (message is null) break;
                    inbox.Enqueue(message);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch { /* transient transport failure; fall through to backoff and retry */ }
            finally
            {
                connected = false;
                if (transport is not null) { try { await transport.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ } }
            }
            if (cancellationToken.IsCancellationRequested) return;
            attempt++;
            try { await Task.Delay(TimeSpan.FromTicks(retryBackoffUnit.Ticks * Math.Min(10, attempt)), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
