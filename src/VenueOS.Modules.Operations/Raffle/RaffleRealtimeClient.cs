using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace VenueOS.Modules.Operations.Raffle;

/// <summary>One flat envelope covering every frame shape the backend's /ws endpoint can send: 'state' (on join),
/// 'updated' (a republish), 'spin' (a draw/redraw result), 'deleted', and 'error'. The donor/reconstructed
/// protocol has no authenticate/subscribe split or revision numbers - it is simpler than Tournament's.</summary>
public sealed record RaffleRealtimeMessage(
    string Type,
    string? RaffleId = null,
    string? Name = null,
    List<string>? Tickets = null,
    double? Rotation = null,
    string? WinnerName = null,
    bool? HasWinner = null,
    List<string>? ExcludedTickets = null,
    int? WinnerIndex = null,
    int? DurationMs = null,
    string? Message = null,
    string? Code = null);

/// <summary>The actual socket I/O boundary, kept behind a tiny interface per NEW_MODULE_GUIDE.md §30 so
/// <see cref="RaffleRealtimeClient"/>'s reconnect/backoff/message-forwarding logic can be unit tested with a fake
/// transport - a real <see cref="ClientWebSocket"/> can only ever be exercised against a live backend.</summary>
public interface IRaffleRealtimeTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri socketUri, CancellationToken cancellationToken);
    Task SendAsync(object message, CancellationToken cancellationToken);
    Task<RaffleRealtimeMessage?> ReceiveAsync(CancellationToken cancellationToken);
}

public sealed class WebSocketRaffleRealtimeTransport : IRaffleRealtimeTransport
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

    public async Task<RaffleRealtimeMessage?> ReceiveAsync(CancellationToken cancellationToken)
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
        return stream.Length == 0 ? null : JsonSerializer.Deserialize<RaffleRealtimeMessage>(stream, Json);
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

public static class RaffleRealtimeProtocol
{
    /// <summary>VenueOS never spins from here - the browser host page owns the Spin/Redraw control per product
    /// decision. VenueOS always joins as a read-only observer so it can learn results the instant they happen,
    /// without ever being able to trigger a draw itself.</summary>
    public static object Join(string raffleId, string token) => new { type = "join", raffleId, token, role = "viewer" };

    public static Uri? SocketUri(string? backendBaseUrl)
    {
        var baseUri = VenueRaffleClient.NormalizeBaseUri(backendBaseUrl);
        if (baseUri is null) return null;
        var builder = new UriBuilder(baseUri) { Scheme = string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws", Path = "/ws" };
        return builder.Uri;
    }
}

/// <summary>Owns the connect/join/reconnect lifecycle for one raffle's realtime stream. The owning
/// <c>VenueRaffleService.Tick</c> drains <see cref="TryDequeue"/> on the Dalamud framework thread every frame - the
/// background receive loop below never touches raffle state directly, only this thread-safe queue. REST
/// reconciliation (<see cref="VenueRaffleClient.FetchAsync"/>) remains authoritative recovery:
/// <see cref="ConsumeReconnectSignal"/> tells the service exactly once per reconnect to refetch full state rather
/// than trusting the socket alone to have delivered everything missed while disconnected.</summary>
public sealed class RaffleRealtimeClient(Func<IRaffleRealtimeTransport> transportFactory, TimeSpan? retryBackoffUnit = null)
{
    private readonly TimeSpan retryBackoffUnit = retryBackoffUnit ?? TimeSpan.FromSeconds(1);
    private readonly ConcurrentQueue<RaffleRealtimeMessage> inbox = new();
    private CancellationTokenSource? loopCancellation;
    private volatile bool connected;
    private volatile bool reconnectSignal;

    public bool IsConnected => connected;

    public void Start(Uri socketUri, string raffleId, string token)
    {
        Stop();
        var cancellation = new CancellationTokenSource();
        loopCancellation = cancellation;
        _ = RunAsync(socketUri, raffleId, token, cancellation.Token);
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

    public bool TryDequeue(out RaffleRealtimeMessage message) => inbox.TryDequeue(out message!);

    /// <summary>Returns true at most once per successful reconnect (never for the very first connect after
    /// <see cref="Start"/>, since the caller already just did a fresh REST load before starting the socket).</summary>
    public bool ConsumeReconnectSignal()
    {
        if (!reconnectSignal) return false;
        reconnectSignal = false;
        return true;
    }

    private async Task RunAsync(Uri socketUri, string raffleId, string token, CancellationToken cancellationToken)
    {
        var hasConnectedOnce = false;
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            IRaffleRealtimeTransport? transport = null;
            try
            {
                transport = transportFactory();
                await transport.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);
                await transport.SendAsync(RaffleRealtimeProtocol.Join(raffleId, token), cancellationToken).ConfigureAwait(false);
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
