using System.Text.Json;
using VenueOS.Core;

namespace VenueOS.Venues;

public sealed class VenueProfileService(IVenueStore store, ModuleHost modules)
{
    private VenueContext? current;
    private readonly List<string> recoveryWarnings = [];
    public VenueProfile Current => GetActive(store.Read());
    public IReadOnlyList<VenueProfile> Profiles => store.Read().Venues;
    public IReadOnlyList<string> RecoveryWarnings => recoveryWarnings.ToArray();
    public event Action<VenueProfile>? ActiveVenueChanged;
    public event Action<string>? ConfigurationRecovered;

    public static VenueStoreSnapshot CreateInitialSnapshot()
    {
        var first = new VenueProfile(Guid.NewGuid(), "My Venue", BuiltInThemes.Dark);
        return new VenueStoreSnapshot(1, first.Id, [first], new Dictionary<string, ModulePayload>(StringComparer.Ordinal));
    }

    public VenueProfile Create(string displayName, VenueTheme? theme = null)
    {
        var snapshot = store.Read(); var venue = new VenueProfile(Guid.NewGuid(), ValidateName(displayName), theme ?? BuiltInThemes.Dark);
        snapshot.Venues.Add(venue); store.Write(snapshot); return venue;
    }

    public VenueOperationResult Rename(Guid id, string displayName)
    {
        var snapshot = store.Read(); var index = snapshot.Venues.FindIndex(x => x.Id == id); if (index < 0) return new(false, "Venue was not found.");
        snapshot.Venues[index] = snapshot.Venues[index] with { DisplayName = ValidateName(displayName) }; store.Write(snapshot); return new(true);
    }

    public VenueProfile Duplicate(Guid sourceId, string displayName)
    {
        var snapshot = store.Read(); var source = snapshot.Venues.SingleOrDefault(x => x.Id == sourceId) ?? throw new InvalidOperationException("Venue was not found.");
        var copy = source with { Id = Guid.NewGuid(), DisplayName = ValidateName(displayName) }; snapshot.Venues.Add(copy);
        foreach (var pair in snapshot.ModulePayloads.Where(x => x.Key.StartsWith(sourceId.ToString("N"), StringComparison.Ordinal)).ToArray())
        {
            var parts = pair.Key.Split(':'); var key = new VenueModuleConfigKey(copy.Id, parts[1], int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
            snapshot.ModulePayloads[key.ToString()] = pair.Value; // ModulePayload is immutable (string-backed) — no reparse/reclone needed
        }
        store.Write(snapshot); return copy;
    }

    /// <summary>Switches the active venue. A module's own <c>OnVenueChangedAsync</c> failure (e.g. an incomplete
    /// Party Finder integration missing a required third-party initialization step) is isolated inside
    /// <see cref="ModuleHost.NotifyVenueChangedAsync"/> — recorded via <c>ModuleHost.ModuleFailed</c> — and can
    /// never block or roll back the switch; every other enabled module still receives the new venue context and
    /// <see cref="Current"/> still becomes the destination venue. This method's own <c>try/catch</c> exists only for
    /// a genuine infrastructure failure (the store itself failing to persist, or an <see cref="ActiveVenueChanged"/>
    /// subscriber throwing) — not for a module's lifecycle exception, which never reaches it.</summary>
    public async Task<VenueOperationResult> SwitchAsync(Guid destinationId, CancellationToken cancellationToken = default)
    {
        var before = store.Read(); var destination = before.Venues.SingleOrDefault(x => x.Id == destinationId); if (destination is null) return new(false, "Destination venue was not found.");
        if (before.ActiveVenueId == destinationId && current is not null) return new(true);
        try
        {
            var next = before with { ActiveVenueId = destinationId }; store.Write(next); // durable before lifecycle notification
            var context = ToContext(destination);
            await modules.NotifyVenueChangedAsync(context, cancellationToken).ConfigureAwait(false);
            current = context; ActiveVenueChanged?.Invoke(destination); return new(true);
        }
        catch (Exception ex)
        {
            store.Write(before);
            // Best-effort compensation restores modules that had already observed the destination.
            await modules.NotifyVenueChangedAsync(ToContext(GetActive(before)), cancellationToken).ConfigureAwait(false);
            current = ToContext(GetActive(before));
            return new(false, $"Venue switch failed: {ex.Message}");
        }
    }

    public async Task<VenueOperationResult> DeleteAsync(Guid id, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed) return new(false, "Venue deletion requires confirmation.");
        var snapshot = store.Read(); if (snapshot.Venues.Count == 1) return new(false, "The final venue cannot be deleted.");
        var target = snapshot.Venues.SingleOrDefault(x => x.Id == id); if (target is null) return new(false, "Venue was not found.");
        if (snapshot.ActiveVenueId == id)
        {
            var alternative = snapshot.Venues.First(x => x.Id != id); var switched = await SwitchAsync(alternative.Id, cancellationToken).ConfigureAwait(false); if (!switched.Success) return switched;
            snapshot = store.Read();
        }
        snapshot.Venues.RemoveAll(x => x.Id == id);
        foreach (var key in snapshot.ModulePayloads.Keys.Where(x => x.StartsWith(id.ToString("N"), StringComparison.Ordinal)).ToArray()) snapshot.ModulePayloads.Remove(key);
        store.Write(snapshot); return new(true);
    }

    public T GetModuleConfig<T>(Guid venueId, string moduleId, int schemaVersion, Func<T> createDefault)
    {
        var snapshot = store.Read(); var key = new VenueModuleConfigKey(venueId, moduleId, schemaVersion).ToString();
        if (!snapshot.ModulePayloads.TryGetValue(key, out var payload)) return createDefault();
        if (payload.SchemaVersion != schemaVersion) return Recover(moduleId, schemaVersion, "schema version is unsupported", createDefault);
        if (string.IsNullOrEmpty(payload.Json)) return Recover(moduleId, schemaVersion, "payload is empty", createDefault);
        try { return JsonSerializer.Deserialize<T>(payload.Json) ?? Recover(moduleId, schemaVersion, "payload deserialized as null", createDefault); }
        catch (JsonException) { return Recover(moduleId, schemaVersion, "payload JSON is malformed", createDefault); }
        catch (NotSupportedException) { return Recover(moduleId, schemaVersion, "payload format is unsupported", createDefault); }
    }
    public void SaveModuleConfig<T>(Guid venueId, string moduleId, int schemaVersion, T config)
    {
        var snapshot = store.Read(); var key = new VenueModuleConfigKey(venueId, moduleId, schemaVersion).ToString();
        snapshot.ModulePayloads[key] = new ModulePayload(schemaVersion, JsonSerializer.Serialize(config)); store.Write(snapshot);
    }
    /// <summary>True only if a payload was actually saved under this exact (venueId, moduleId, schemaVersion) key —
    /// unlike <see cref="GetModuleConfig{T}"/>, this never falls back to a default, so a module can distinguish
    /// "never saved" from "saved and happens to equal the default" when deciding whether an in-place schema
    /// migration still needs to run (see, e.g., a module's own <c>SchemaVersion</c>-bump migration, per
    /// NEW_MODULE_GUIDE.md §13's "that module owns writing it" guidance). Checking this before writing under the new
    /// schema version, and never re-checking the old version once the new one exists, is what makes such a migration
    /// idempotent.</summary>
    public bool HasModuleConfig(Guid venueId, string moduleId, int schemaVersion) =>
        store.Read().ModulePayloads.ContainsKey(new VenueModuleConfigKey(venueId, moduleId, schemaVersion).ToString());
    public VenueOperationResult SetTheme(Guid venueId, VenueTheme theme)
    {
        var snapshot = store.Read(); var index = snapshot.Venues.FindIndex(x => x.Id == venueId); if (index < 0) return new(false, "Venue was not found."); snapshot.Venues[index] = snapshot.Venues[index] with { Theme = theme }; store.Write(snapshot); return new(true);
    }
    public async Task InitializeAsync(CancellationToken ct = default) { var profile = Current; current = ToContext(profile); await modules.NotifyVenueChangedAsync(current, ct).ConfigureAwait(false); }
    private static VenueProfile GetActive(VenueStoreSnapshot snapshot) => snapshot.Venues.Single(x => x.Id == snapshot.ActiveVenueId);
    private static VenueContext ToContext(VenueProfile venue) => new(venue.Id, venue.DisplayName, venue.Theme);
    private static string ValidateName(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Venue name is required.", nameof(value)) : value.Trim();
    /// <summary>Secondary hardening, applied after fixing the actual root cause (a persisted payload that could
    /// never survive a save/reload — see <see cref="ModulePayload"/>'s doc comment) and the per-frame re-read that
    /// used to amplify one bad payload into thousands of identical entries per minute: even with both of those
    /// fixed, this still protects Diagnostics against ever reporting the *same* recovery condition unboundedly, in
    /// case some future caller reintroduces a high-frequency read. A recovery is recorded once per distinct
    /// module/schema/reason combination in a row — the same condition repeating back-to-back is a rendering
    /// artifact, not new information — while a genuinely different failure (a different module, or the same module
    /// recovering for a different reason later) is never hidden. The list itself is bounded like
    /// <see cref="DiagnosticsService"/>'s own error queue, rather than growing forever.</summary>
    private T Recover<T>(string moduleId, int schemaVersion, string reason, Func<T> createDefault)
    {
        var warning = $"Recovered {moduleId} configuration v{schemaVersion}: {reason}. The stored payload was preserved.";
        if (recoveryWarnings.Count == 0 || recoveryWarnings[^1] != warning)
        {
            if (recoveryWarnings.Count >= 50) recoveryWarnings.RemoveAt(0);
            recoveryWarnings.Add(warning);
            ConfigurationRecovered?.Invoke(warning);
        }
        return createDefault();
    }
}
