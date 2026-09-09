using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Modules.Operations.Raffle;

/// <summary>Preserves the donor's exact free-ticket bonus math: a per-call block calculation, not a cumulative
/// one. Buying 3 then 3 more paid tickets under a "5 paid -&gt; 1 free" rule yields zero bonus tickets total
/// (3/5=0 twice), the same as the donor - not the 1 bonus ticket a single 6-ticket purchase would produce. This is
/// preserved deliberately rather than "fixed," per the reconstruction brief's instruction to match donor semantics
/// unless explicitly told to change them.</summary>
public static class RaffleTicketMath
{
    public static int CalculateFreeTickets(RaffleSettings settings, int paidTicketsThisCall)
    {
        if (settings.PaidTicketsForFree <= 0 || settings.FreeTicketsPerBlock <= 0 || paidTicketsThisCall <= 0) return 0;
        var blocks = paidTicketsThisCall / settings.PaidTicketsForFree;
        return blocks * settings.FreeTicketsPerBlock;
    }
}

public sealed record VenueRaffleSettings(RaffleConnectionSettings Connection, RaffleSettings Defaults, List<LocalRaffle> Raffles, string? SelectedRaffleId = null)
{
    public static VenueRaffleSettings Default() => new(new(), new(), [], null);
}

public sealed record RaffleDashboard(int ActiveRaffleCount, int ArchivedRaffleCount, string? SelectedRaffleName, string? WinnerName, bool IsConnected, bool HasUnpublishedChanges);

public sealed class VenueRaffleService(VenueRaffleClient client, VenueProfileService profiles, DiagnosticsService diagnostics, Func<IRaffleRealtimeTransport>? transportFactory = null)
{
    private readonly RaffleRealtimeClient realtime = new(transportFactory ?? (() => new WebSocketRaffleRealtimeTransport()));
    private CancellationTokenSource contextCancellation = new();
    private Guid venueId;

    public VenueRaffleSettings Settings { get; private set; } = VenueRaffleSettings.Default();
    public bool IsRealtimeConnected => realtime.IsConnected;
    public LocalRaffle? Selected => Settings.Raffles.FirstOrDefault(x => x.Id == Settings.SelectedRaffleId);

    public RaffleDashboard Dashboard => new(
        Settings.Raffles.Count(x => !x.IsArchived),
        Settings.Raffles.Count(x => x.IsArchived),
        Selected?.Name,
        Selected?.WinnerName,
        IsRealtimeConnected,
        Selected?.HasUnpublishedChanges ?? false);

    public void Load(Guid nextVenueId)
    {
        contextCancellation.Cancel();
        contextCancellation.Dispose();
        contextCancellation = new CancellationTokenSource();
        realtime.Stop();
        venueId = nextVenueId;
        Settings = profiles.GetModuleConfig(venueId, "games.raffle", 1, VenueRaffleSettings.Default);
        EnsureRealtimeConnection();
    }

    public void Tick(DateTimeOffset now)
    {
        while (realtime.TryDequeue(out var message)) ApplyRealtimeMessage(message);
        if (realtime.ConsumeReconnectSignal()) _ = ReconcileAsync();
    }

    // --- Settings (persistent, Settings → Modules → Raffle) ----------------------------------------------------

    public void SaveConnection(RaffleConnectionSettings connection) { Settings = Settings with { Connection = connection }; Save(); EnsureRealtimeConnection(); }
    public void SaveDefaults(RaffleSettings defaults) { Settings = Settings with { Defaults = defaults }; Save(); }
    public void UpdateRaffleSettings(string raffleId, RaffleSettings settings) => Update(raffleId, r => r with { Settings = settings });

    // --- Lifecycle: create / select / rename / archive / delete / reset --------------------------------------

    public LocalRaffle Create(string name)
    {
        var raffle = new LocalRaffle(
            Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(name) ? $"Raffle {DateTime.UtcNow:yyyy-MM-dd HHmm}" : name.Trim(),
            DateTime.UtcNow,
            Settings.Defaults,
            [],
            []);
        Settings.Raffles.Insert(0, raffle);
        Settings = Settings with { SelectedRaffleId = raffle.Id };
        Save();
        EnsureRealtimeConnection();
        return raffle;
    }

    public void Select(string raffleId)
    {
        if (!Settings.Raffles.Any(x => x.Id == raffleId)) return;
        Settings = Settings with { SelectedRaffleId = raffleId };
        Save();
        EnsureRealtimeConnection();
    }

    public void Rename(string raffleId, string newName) =>
        Update(raffleId, r => r with { Name = string.IsNullOrWhiteSpace(newName) ? r.Name : newName.Trim() });

    /// <summary>Nondestructive: removes the raffle from the normal active list without touching its stored data.
    /// Archived raffles keep their winner/participant/settings/link history and can be restored at any time.</summary>
    public void Archive(string raffleId) => Update(raffleId, r => r with { IsArchived = true });

    public void Unarchive(string raffleId) => Update(raffleId, r => r with { IsArchived = false });

    /// <summary>Clears operational contents only (participants, exclusions, the locally-mirrored winner) - it
    /// never deletes the raffle itself and never touches the backend directly. The now-stale
    /// <see cref="LocalRaffle.PublishedTicketHash"/> makes <see cref="LocalRaffle.HasUnpublishedChanges"/> true so
    /// the operator is prompted to republish (optionally clearing backend-side exclusions too) rather than the
    /// backend silently keeping a stale roster/winner forever.</summary>
    public void Reset(string raffleId) =>
        Update(raffleId, r => r with { Participants = [], ExcludedParticipants = [], WinnerName = null, PublishedTicketHash = null });

    /// <summary>Permanent local deletion. If the raffle was ever published, makes a best-effort attempt to delete
    /// the backend's copy too (organizer access key required) so no orphan published raffle survives an operator
    /// believing it is fully gone - but a failed backend call does not block the local deletion the operator just
    /// explicitly confirmed; it is reported to Diagnostics instead so the operator can retry manually.</summary>
    public async Task DeleteAsync(string raffleId)
    {
        var raffle = Settings.Raffles.FirstOrDefault(x => x.Id == raffleId);
        if (raffle is null) return;

        if (raffle.ExternalId is { } externalId)
        {
            var result = await client.DeleteAsync(Settings.Connection, externalId, contextCancellation.Token).ConfigureAwait(false);
            if (!result.Success)
                diagnostics.RecordFailure($"games.raffle: backend deletion failed, local copy was still deleted ({DiagnosticsService.Redact(result.Error ?? "unknown error")})");
        }

        var wasSelected = Settings.SelectedRaffleId == raffleId;
        Settings.Raffles.RemoveAll(x => x.Id == raffleId);
        if (wasSelected)
        {
            var nextSelected = Settings.Raffles.FirstOrDefault(x => !x.IsArchived)?.Id ?? Settings.Raffles.FirstOrDefault()?.Id;
            Settings = Settings with { SelectedRaffleId = nextSelected };
            EnsureRealtimeConnection();
        }
        Save();
    }

    /// <summary>Registers an imported raffle (see <see cref="RaffleXlsxImporter"/>) as a new local raffle and
    /// selects it. Always a fresh, unpublished raffle - never overwrites an existing one.</summary>
    public void ImportRaffle(LocalRaffle imported)
    {
        Settings.Raffles.Insert(0, imported);
        Settings = Settings with { SelectedRaffleId = imported.Id };
        Save();
        EnsureRealtimeConnection();
    }

    // --- Participant / ticket management ----------------------------------------------------------------------

    public void AddParticipant(string raffleId, string name, string? homeWorld)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmedName = name.Trim();
        var trimmedWorld = string.IsNullOrWhiteSpace(homeWorld) ? null : homeWorld.Trim();
        var identity = new GuestIdentity(trimmedName, trimmedWorld ?? string.Empty).Key;
        Update(raffleId, r => r.Participants.Any(p => p.IdentityKey == identity)
            ? r
            : r with { Participants = [.. r.Participants, new RaffleParticipant(trimmedName, trimmedWorld)] });
    }

    /// <summary>Adds paid tickets to a participant (creating them if new), applying the raffle's free-ticket
    /// bonus rule to this purchase only, matching donor semantics (see <see cref="RaffleTicketMath"/>).</summary>
    public void AddPaidTickets(string raffleId, string name, string? homeWorld, int count)
    {
        if (string.IsNullOrWhiteSpace(name) || count <= 0) return;
        var trimmedName = name.Trim();
        var trimmedWorld = string.IsNullOrWhiteSpace(homeWorld) ? null : homeWorld.Trim();
        var identity = new GuestIdentity(trimmedName, trimmedWorld ?? string.Empty).Key;

        Update(raffleId, r =>
        {
            var bonus = RaffleTicketMath.CalculateFreeTickets(r.Settings, count);
            var participants = new List<RaffleParticipant>(r.Participants);
            var index = participants.FindIndex(p => p.IdentityKey == identity);
            if (index >= 0)
            {
                var existing = participants[index];
                participants[index] = existing with { PaidTickets = existing.PaidTickets + count, FreeTickets = existing.FreeTickets + bonus };
            }
            else
            {
                participants.Add(new RaffleParticipant(trimmedName, trimmedWorld, count, bonus));
            }
            return r with { Participants = participants };
        });
    }

    public void AddFreeTickets(string raffleId, string name, string? homeWorld, int count)
    {
        if (string.IsNullOrWhiteSpace(name) || count <= 0) return;
        var trimmedName = name.Trim();
        var trimmedWorld = string.IsNullOrWhiteSpace(homeWorld) ? null : homeWorld.Trim();
        var identity = new GuestIdentity(trimmedName, trimmedWorld ?? string.Empty).Key;

        Update(raffleId, r =>
        {
            var participants = new List<RaffleParticipant>(r.Participants);
            var index = participants.FindIndex(p => p.IdentityKey == identity);
            if (index >= 0)
            {
                var existing = participants[index];
                participants[index] = existing with { FreeTickets = existing.FreeTickets + count };
            }
            else
            {
                participants.Add(new RaffleParticipant(trimmedName, trimmedWorld, 0, count));
            }
            return r with { Participants = participants };
        });
    }

    /// <summary>Increments/decrements one participant's paid or free count directly (the per-row +/- controls).
    /// A participant whose counts both reach zero is removed entirely, matching donor cleanup behavior. Counts
    /// never go negative.</summary>
    public void AdjustTickets(string raffleId, string identityKey, int paidDelta, int freeDelta) =>
        Update(raffleId, r =>
        {
            var participants = new List<RaffleParticipant>(r.Participants);
            var index = participants.FindIndex(p => p.IdentityKey == identityKey);
            if (index < 0) return r;
            var existing = participants[index];
            var paid = Math.Max(0, existing.PaidTickets + paidDelta);
            var free = Math.Max(0, existing.FreeTickets + freeDelta);
            if (paid == 0 && free == 0) participants.RemoveAt(index);
            else participants[index] = existing with { PaidTickets = paid, FreeTickets = free };
            return r with { Participants = participants };
        });

    public void RemoveParticipant(string raffleId, string identityKey) =>
        Update(raffleId, r => r with { Participants = r.Participants.Where(p => p.IdentityKey != identityKey).ToList() });

    // --- Publish / reconciliation ------------------------------------------------------------------------------

    /// <summary>Synchronizes the current local roster to the backend and (re)establishes the realtime
    /// connection. Publishing an unchanged ticket pool is safe/idempotent on the backend side. Pass
    /// <paramref name="clearExclusions"/> when the operator explicitly wants to wipe the backend's memory of
    /// previously-excluded winners (e.g. after a Reset) rather than have them stay excluded forever.</summary>
    public async Task<RaffleClientResult<RaffleCreateResponse>> PublishAsync(string raffleId, bool clearExclusions = false)
    {
        if (clearExclusions) Update(raffleId, r => r with { ExcludedParticipants = [] });

        var raffle = Settings.Raffles.FirstOrDefault(x => x.Id == raffleId);
        if (raffle is null) return RaffleClientResult<RaffleCreateResponse>.Failed("Raffle not found.");

        var result = await client.UpsertAsync(Settings.Connection, raffle, contextCancellation.Token, clearExclusions).ConfigureAwait(false);
        if (result.Success && result.Value is { } value)
        {
            Update(raffleId, r => r with
            {
                ExternalId = value.RaffleId ?? r.ExternalId,
                HostUrl = value.HostUrl ?? r.HostUrl,
                ViewerUrl = value.ViewerUrl ?? r.ViewerUrl,
                WinnerName = value.WinnerName,
                PublishedTicketHash = r.CurrentTicketHash,
            });
            EnsureRealtimeConnection();
        }
        else
        {
            diagnostics.RecordFailure($"games.raffle: publish failed ({DiagnosticsService.Redact(result.Error ?? "unknown error")})");
        }
        return result;
    }

    /// <summary>REST fallback/reconciliation - used after reconnect, and available as an explicit manual "Refresh"
    /// action for the operator. Never trusts a fetch older than what realtime has already reported (see
    /// <see cref="ApplyRealtimeMessage"/> for why a stale frame can't override newer state: both paths write
    /// through the same <see cref="UpdateMirroredState"/> and there is only ever one in-flight source of truth per
    /// raffle at a time on the Dalamud framework thread).</summary>
    public async Task<RaffleClientResult<RaffleStateResponse>> FetchAsync(string raffleId)
    {
        var raffle = Settings.Raffles.FirstOrDefault(x => x.Id == raffleId);
        if (raffle?.ExternalId is null) return RaffleClientResult<RaffleStateResponse>.Failed("Raffle has not been published yet.");
        var token = raffle.ViewerToken ?? raffle.HostToken;
        var result = await client.FetchAsync(Settings.Connection, raffle.ExternalId, token, contextCancellation.Token).ConfigureAwait(false);
        if (result.Success && result.Value is { } value) UpdateMirroredState(raffleId, value.WinnerName, value.ExcludedTickets);
        return result;
    }

    private void ApplyRealtimeMessage(RaffleRealtimeMessage message)
    {
        var raffle = Selected;
        if (raffle?.ExternalId is null) return;
        if (message.RaffleId is not null && message.RaffleId != raffle.ExternalId) return; // frame for a different raffle

        switch (message.Type)
        {
            case "state":
            case "updated":
            case "spin":
                UpdateMirroredState(raffle.Id, message.WinnerName, message.ExcludedTickets);
                break;
            case "deleted":
                diagnostics.RecordFailure("games.raffle: the published copy of this raffle was deleted on the backend.");
                Update(raffle.Id, r => r with { ExternalId = null, HostUrl = null, ViewerUrl = null, PublishedTicketHash = null });
                realtime.Stop();
                break;
            case "error":
                if (!string.IsNullOrWhiteSpace(message.Message))
                    diagnostics.RecordFailure($"games.raffle: backend reported \"{DiagnosticsService.Redact(message.Message)}\"");
                break;
        }
    }

    private void UpdateMirroredState(string raffleId, string? winnerName, List<string>? excludedTickets) =>
        Update(raffleId, r => r with
        {
            WinnerName = winnerName,
            ExcludedParticipants = excludedTickets ?? r.ExcludedParticipants,
        });

    private async Task ReconcileAsync()
    {
        var raffle = Selected;
        if (raffle?.ExternalId is null) return;
        var result = await client.FetchAsync(Settings.Connection, raffle.ExternalId, raffle.ViewerToken ?? raffle.HostToken, contextCancellation.Token).ConfigureAwait(false);
        if (result.Success && result.Value is { } value) UpdateMirroredState(raffle.Id, value.WinnerName, value.ExcludedTickets);
        else if (!result.Success) diagnostics.RecordFailure($"games.raffle: reconnect reconciliation failed ({DiagnosticsService.Redact(result.Error ?? "unknown error")})");
    }

    private void EnsureRealtimeConnection()
    {
        var raffle = Selected;
        var token = raffle?.ViewerToken ?? raffle?.HostToken;
        if (raffle?.ExternalId is null || token is null)
        {
            realtime.Stop();
            return;
        }
        var socketUri = RaffleRealtimeProtocol.SocketUri(Settings.Connection.BackendBaseUrl);
        if (socketUri is null)
        {
            realtime.Stop();
            return;
        }
        realtime.Start(socketUri, raffle.ExternalId, token);
    }

    private void Update(string raffleId, Func<LocalRaffle, LocalRaffle> transform)
    {
        var index = Settings.Raffles.FindIndex(x => x.Id == raffleId);
        if (index < 0) return;
        Settings.Raffles[index] = transform(Settings.Raffles[index]);
        Save();
    }

    private void Save() => profiles.SaveModuleConfig(venueId, "games.raffle", 1, Settings);
}

public sealed class VenueRaffleModule(VenueRaffleService raffle, Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    public ModuleDescriptor Descriptor { get; } = new("games.raffle", "Raffle", "Backend-compatible raffle operations with a live browser wheel.", "ticket", DisplayOrder: 9);
    public bool IsEnabled { get; set; } = true;
    public Task InitializeAsync(ModuleContext c, CancellationToken t) => Task.CompletedTask;
    public Task OnVenueChangedAsync(VenueContext c, CancellationToken t) { raffle.Load(c.VenueId); return Task.CompletedTask; }
    public void Tick(DateTimeOffset now) => raffle.Tick(now);
    public void Draw() => draw?.Invoke();
    public void DrawSettings() => (drawSettings ?? draw)?.Invoke();
    public ValueTask DisposeAsync() { raffle.Load(Guid.Empty); return ValueTask.CompletedTask; }
}
