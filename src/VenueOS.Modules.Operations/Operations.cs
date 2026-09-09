using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;
using VenueOS.Modules.Operations.Raffle;

namespace VenueOS.Modules.Operations;

/// <summary>Persistent Attendance configuration (Settings → Modules → Attendance). Territory lock and the
/// distance-filter center are deliberately *not* persisted fields here — per the donor reference
/// (<c>venuestatusandgreet</c>), both are captured fresh from wherever the operator actually is at the moment a
/// session starts (see <see cref="AttendanceService.StartSession"/>), not a pre-typed coordinate.
/// <see cref="VenueAddress"/>/<see cref="AutoDetectVenueAddress"/>/<see cref="TrackingPollIntervalSeconds"/>/
/// <see cref="ExportDirectory"/>/<see cref="StatsRangeDays"/> restore the donor's Venue Details/tracking/export
/// configuration that the previous reconstruction pass omitted; venue *identity* is still never duplicated here —
/// only location/address text and tracking cadence, which the donor kept separate from the venue's name.
/// <b>Live-verified product correction: Venue Area Type is no longer a field here.</b> It used to be
/// (<c>AreaMode</c>), a single venue-wide/global toggle edited in Settings — too operationally important to be
/// buried there, and wrong as a *global* value besides: an operator running one outdoor event must not have that
/// choice silently apply to every ordinary housing-venue opening afterward. It is now chosen per-opening, right
/// above "Start New Opening" on Attendance's own Live screen, and snapshotted onto the opening itself
/// (see <see cref="AttendanceService.ActiveAreaMode"/> and <see cref="IVenueDatabase.StartSession"/>'s persisted
/// columns) rather than living as a mutable setting at all.</summary>
public sealed record AttendanceSettings(
    bool LockToOpenTerritory = true,
    bool UseDistanceFilter = true,
    float RadiusYalms = 35f,
    int TrackingPollIntervalSeconds = 900,
    string VenueAddress = "",
    bool AutoDetectVenueAddress = true,
    string ExportDirectory = "",
    int StatsRangeDays = 7);

/// <summary>Full standalone-parity Attendance: persisted session ("opening") lifecycle, per-night visitor history,
/// guest-count sampling, and daily stats — all via <see cref="IVenueDatabase"/> (donor: <c>DatabaseService</c> +
/// <c>VenueTrackerService</c>), scoped to whichever venue is currently attached. A "session" here is exactly the
/// donor's "opening": Start/Pause (resumable)/Resume/Close/Delete, mapped 1:1 onto <see cref="IVenueDatabase"/>'s
/// session methods. Switching venues (<see cref="DetachSession"/>) never auto-closes or auto-resumes a session —
/// matching the donor's own behavior of never silently reopening a session the operator didn't explicitly pick.</summary>
public sealed class AttendanceService(PresenceService presence, IVenueDatabase database, IClock clock, Func<uint>? currentTerritory = null, Func<System.Numerics.Vector3?>? currentPosition = null, GreeterService? greeter = null)
{
    private readonly Dictionary<string, PlayerSnapshot> guests = new(StringComparer.Ordinal);
    private Guid venueId;
    private DateTimeOffset nextSampleAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastSampleBucket = DateTimeOffset.MinValue;
    /// <summary>Session-scoped, in-memory only — never persisted, never a database concept. Tracks which guests have
    /// already had their one genuine (non-seed) arrival this opening, purely to decide whether
    /// <see cref="AutomaticGreetingEligible"/> should fire again. See that event's doc comment for the live-verified
    /// bug this exists to fix: the database's own "first visit tonight" bookkeeping (<see cref="FirstVisitTonight"/>)
    /// is consumed by opening-start seeding regardless of whether that event is raised, so it is the wrong signal to
    /// gate automatic greeting on — a guest seeded at opening start would otherwise never again register as a
    /// "first visit" for the rest of that calendar night, even after a completely genuine later arrival.</summary>
    private readonly HashSet<string> autoGreetEligibilityConsumed = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, PlayerSnapshot> Guests => guests;
    public long? CurrentSessionId { get; private set; }
    /// <summary>Captured when the session starts if the corresponding <see cref="AttendanceSettings"/> toggle asks
    /// for it — a runtime value, not a saved setting (see the type-level remark above), but (live-verified
    /// correction) persisted onto the opening itself in <see cref="IVenueDatabase"/> so <see cref="ResumeSession"/>
    /// restores the exact value the opening actually started with, rather than re-capturing a fresh one from
    /// wherever the operator happens to be standing at resume time.</summary>
    public uint? ActiveTerritoryLock { get; private set; }
    /// <summary>The fixed origin an Outdoor-mode opening's radius stays centered on for its entire lifetime —
    /// persisted (see <see cref="ActiveTerritoryLock"/>'s remark) so a plugin reload/crash followed by
    /// <see cref="ResumeSession"/> restores the *original* origin, never a new one from the operator's current,
    /// possibly different, position.</summary>
    public System.Numerics.Vector3? ActiveFixedCenter { get; private set; }
    /// <summary>Live-verified product correction: which area mode the *currently active opening* is actually using
    /// — chosen once when it started (see <see cref="StartSession"/>) or restored from storage on
    /// <see cref="ResumeSession"/>, snapshotted for that opening's entire lifetime. This is deliberately not a
    /// <see cref="AttendanceSettings"/> field any more (a single global/venue-wide value was the actual product bug:
    /// an operator's one-off outdoor event must never silently leak into the next ordinary housing-venue opening).
    /// Defaults to <see cref="PresenceAreaMode.FollowOperator"/> ("Normal") whenever no opening is active — the safe
    /// value for the shared continuous presence scan (VIP still needs guest detection with no session open) and for
    /// what the *next* opening will use unless the operator explicitly picks Outdoor before starting it.</summary>
    public PresenceAreaMode ActiveAreaMode { get; private set; } = PresenceAreaMode.FollowOperator;
    public event Action<GuestIdentity>? GuestArrived;
    public event Action<GuestIdentity>? GuestDeparted;
    /// <summary>Fires once per calendar night per guest, backed by <see cref="IVenueDatabase.MarkPresent"/>'s
    /// database-persisted "first visit tonight" bookkeeping — a purely informational/reporting signal now. <b>Not
    /// the automatic-greeting trigger</b> (see <see cref="AutomaticGreetingEligible"/> for that) — donor:
    /// <c>VenueTrackerService.FirstVisitTonightDetected</c>. Never fires for a guest seeded at opening start/resume
    /// (see <see cref="Arrive"/>'s <c>isSessionSeed</c> parameter), and — because the database row it reads is
    /// created/updated regardless of <c>isSessionSeed</c> — never fires again for that guest for the rest of the
    /// calendar night either, even after a completely genuine later arrival. That consumption is exactly why this
    /// event must never again be used to drive automatic greeting.</summary>
    public event Action<GuestIdentity>? FirstVisitTonight;
    /// <summary>The sole automatic-greeting trigger — fires exactly once per guest per opening, on their first
    /// genuine (non-seed) arrival, regardless of what <see cref="FirstVisitTonight"/>/the database's calendar-night
    /// visit bookkeeping says. <b>Live-verified bug this exists to fix:</b> a guest seeded at opening start (already
    /// standing in the venue when it began) still had <see cref="IVenueDatabase.MarkPresent"/> called for them during
    /// seeding, which unconditionally consumes that guest's "first visit tonight" database flag — so even though the
    /// <see cref="FirstVisitTonight"/> *event* was correctly suppressed for the seed itself, that guest's next
    /// genuine arrival (after a real leave and return) came back as visit #2, not #1, and <see cref="FirstVisitTonight"/>
    /// silently never fired again for the rest of that calendar night — automatic greeting could never trigger for
    /// that guest again, no matter how many times they genuinely left and returned. This event is entirely
    /// independent, in-memory, session-scoped (see <see cref="autoGreetEligibilityConsumed"/>) — it does not read or
    /// write any database "first visit" state, so opening-start seeding can never consume it: it fires on the first
    /// arrival <see cref="Arrive"/> ever sees with <c>isSessionSeed: false</c> for a given guest, once per opening,
    /// full stop. Whether that guest is already greeted is deliberately not checked here — that decision belongs
    /// entirely to <see cref="GreetingCoordinator.TryGreet"/>, which already treats "already greeted" as a no-op.</summary>
    public event Action<GuestIdentity>? AutomaticGreetingEligible;
    /// <summary>Fires from <see cref="StartSession"/>/<see cref="ResumeSession"/> — the composition root uses this
    /// to reset any other module's own arrival-idempotence tracking in lockstep with
    /// <see cref="PresenceService.ResetKnownPresence"/>, so a player already standing in the venue when a session
    /// opens is tracked as present everywhere (not just in Attendance) without any module treating them as an
    /// automatic-greeting-eligible arrival — see <see cref="Arrive"/>'s <c>isSessionSeed</c> parameter.</summary>
    public event Action? SessionOpened;

    public void AttachVenue(Guid id) => venueId = id;

    public void Attach()
    {
        presence.Arrived += Arrive; presence.Departed += Depart;
    }

    /// <summary>Repopulates the live guest list from whatever the shared presence scan already has in hand — used
    /// right after a venue switch so players already detected as present show up immediately, instead of only
    /// appearing on their next arrival/departure diff.</summary>
    public void RebuildCurrent(IEnumerable<PlayerSnapshot> snapshots)
    {
        guests.Clear();
        foreach (var snapshot in snapshots) guests[new GuestIdentity(snapshot.Name, snapshot.HomeWorld).Key] = snapshot;
    }

    /// <summary>Switches away from whatever session/venue this instance was tracking without touching persisted
    /// state — the donor never auto-resumes a session on its own, so neither does this; the session (if any) simply
    /// stays open/resumable in its own venue's history until the operator explicitly returns and resumes it.</summary>
    public void DetachSession()
    {
        CurrentSessionId = null; ActiveTerritoryLock = null; ActiveFixedCenter = null; ActiveAreaMode = PresenceAreaMode.FollowOperator; guests.Clear();
        nextSampleAt = DateTimeOffset.MinValue; lastSampleBucket = DateTimeOffset.MinValue;
        autoGreetEligibilityConsumed.Clear();
    }

    /// <summary>Donor parity for presence re-discovery: the donor's tracker only ever scans while its venue is
    /// "open," so the very first scan after opening naturally rediscovers everyone already inside. This service's
    /// <see cref="PresenceService"/> scans continuously (VIP needs that even with no session open), so the same
    /// effect has to be produced explicitly: <see cref="PresenceService.ResetKnownPresence"/> clears what presence
    /// already knows, so the very next scan (next frame) fires <c>Arrived</c> for every currently-observed player —
    /// including the operator's own character — with <c>isSessionSeed: true</c>. <b>Live-verified correction:</b>
    /// this population is opening-start <i>initialization</i>, not an arrival event for greeting purposes — everyone
    /// already present is seeded into Attendance as Not Greeted (counted, visit recorded) but is explicitly excluded
    /// from both <see cref="FirstVisitTonight"/> and <see cref="AutomaticGreetingEligible"/> (see <see cref="Arrive"/>) —
    /// VIP recognition is decided entirely inside <see cref="GreetingCoordinator.TryGreet"/>, reached only through
    /// <see cref="AutomaticGreetingEligible"/>, so it is covered by the same exclusion, not a separate one. Only a
    /// genuine arrival after the opening is already active can automatically enter the greeting pipeline; a seeded
    /// occupant remains reachable only through the operator's manual Greet/Mark Greeted actions.
    /// <paramref name="areaMode"/> is the Venue Area Type the operator picked on the Live screen immediately before
    /// pressing Start — it is snapshotted for this opening's entire lifetime (see <see cref="ActiveAreaMode"/>) and
    /// persisted onto the opening itself, along with the resulting fixed origin if applicable, so
    /// <see cref="ResumeSession"/> can restore exactly this, never the *next* opening's own default.</summary>
    public void StartSession(bool lockToOpenTerritory, PresenceAreaMode areaMode, DateTimeOffset now)
    {
        var territoryLock = lockToOpenTerritory ? currentTerritory?.Invoke() : null;
        var fixedCenter = areaMode == PresenceAreaMode.FixedPoint ? currentPosition?.Invoke() : null;
        CurrentSessionId = database.StartSession(venueId, now, areaMode, territoryLock, fixedCenter);
        ActiveTerritoryLock = territoryLock;
        ActiveFixedCenter = fixedCenter;
        ActiveAreaMode = areaMode;
        guests.Clear(); nextSampleAt = DateTimeOffset.MinValue; lastSampleBucket = DateTimeOffset.MinValue;
        autoGreetEligibilityConsumed.Clear();
        presence.ResetKnownPresence(); SessionOpened?.Invoke();
    }

    /// <summary>Restores the opening exactly as it was — its saved <see cref="ActiveAreaMode"/>, territory lock, and
    /// (for Outdoor) fixed origin all come from what <see cref="StartSession"/> persisted for this specific opening,
    /// never re-captured from wherever the operator happens to be standing right now. There is deliberately no
    /// "area mode"/"lock to territory" parameter here any more — resuming is not a second place to choose those.</summary>
    public bool ResumeSession(long sessionId)
    {
        var record = database.GetSession(venueId, sessionId);
        if (record is null || !database.ResumeSession(venueId, sessionId)) return false;
        CurrentSessionId = sessionId;
        ActiveAreaMode = record.AreaMode;
        ActiveTerritoryLock = record.TerritoryLock;
        ActiveFixedCenter = record.FixedCenter;
        guests.Clear();
        autoGreetEligibilityConsumed.Clear();
        presence.ResetKnownPresence(); SessionOpened?.Invoke();
        return true;
    }

    public bool PauseSession(DateTimeOffset now)
    {
        if (CurrentSessionId is not long id || !database.PauseSession(venueId, id, now)) return false;
        CurrentSessionId = null; ActiveTerritoryLock = null; ActiveFixedCenter = null; ActiveAreaMode = PresenceAreaMode.FollowOperator; guests.Clear();
        return true;
    }

    public bool CloseSession(DateTimeOffset now) => CurrentSessionId is long id && CloseSession(id, now);

    public bool CloseSession(long sessionId, DateTimeOffset now)
    {
        if (!database.CloseSession(venueId, sessionId, now)) return false;
        if (CurrentSessionId == sessionId) { CurrentSessionId = null; ActiveTerritoryLock = null; ActiveFixedCenter = null; ActiveAreaMode = PresenceAreaMode.FollowOperator; guests.Clear(); }
        return true;
    }

    public bool DeleteSession(long sessionId) => sessionId != CurrentSessionId && database.DeleteSession(venueId, sessionId);

    public IReadOnlyList<AttendanceSessionRecord> GetRecentSessions(int maxRows = 100) => database.GetRecentSessions(venueId, maxRows);

    /// <summary>Donor: <c>VenueTrackerService.Tick</c>'s 5-minute-bucketed sample, gated by
    /// <paramref name="pollIntervalSeconds"/> — a sample is only ever recorded once per 5-minute wall-clock bucket,
    /// and only after at least <paramref name="pollIntervalSeconds"/> have elapsed since the last one, so the
    /// donor's default 900s poll interval yields one sample every 15 minutes despite the "5-minute" bucket size.</summary>
    public void Tick(DateTimeOffset now, int pollIntervalSeconds)
    {
        if (CurrentSessionId is not long sessionId || now < nextSampleAt) return;
        var bucket = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute / 5 * 5, 0, TimeSpan.Zero);
        if (bucket == lastSampleBucket) return;
        nextSampleAt = now.AddSeconds(Math.Clamp(pollIntervalSeconds, 5, 3600));
        lastSampleBucket = bucket;
        database.RecordSample(venueId, sessionId, now, guests.Count);
    }

    public AttendanceNightSummary? GetTonightSummary() => CurrentSessionId is long id ? database.GetSessionSummary(venueId, id) : null;
    /// <summary>Per-opening Max/Min/Unique/Total Visits for any historical session, not just the active one — a
    /// thin pass-through to <see cref="IVenueDatabase.GetSessionSummary"/>, which already computes this correctly
    /// for an arbitrary <paramref name="sessionId"/> (it was simply never called with anything but
    /// <see cref="CurrentSessionId"/> before). Callers should only pass a session id already known to belong to this
    /// venue (e.g. from <see cref="GetRecentSessions"/>) — matching how <see cref="CloseSession(long, DateTimeOffset)"/>/
    /// <see cref="DeleteSession"/> are used today.</summary>
    public AttendanceNightSummary GetSessionSummary(long sessionId) => database.GetSessionSummary(venueId, sessionId);
    public IReadOnlyList<AttendanceVisitorRecord> GetTonightVisitors() => CurrentSessionId is long id ? database.GetSessionVisitors(venueId, id) : [];
    public IReadOnlyList<AttendanceGuestSample> GetTonightSamples(int maxRows = 288) => CurrentSessionId is long id ? database.GetSessionSamples(venueId, id, maxRows) : [];
    public IReadOnlyList<AttendanceDailyStat> GetDailyStats(DateOnly fromInclusive, DateOnly toInclusive) => database.GetDailyStats(venueId, fromInclusive, toInclusive);
    /// <summary>Attendance is the single authoritative source of greeted state for the current opening — this is
    /// the one place that writes it (via <see cref="IVenueDatabase.MarkGreeted"/>). Greeter and VIP read it back
    /// through <see cref="IsGreeted"/> rather than keeping their own competing flag; marking a guest greeted here
    /// (manually, or automatically — see <see cref="GreeterService"/>'s <c>onGreetingCompleted</c> callback, which
    /// calls this same method) also tells Greeter to drop any queued/in-flight attempt for that guest
    /// (<see cref="GreeterService.CancelPending"/>) so it never wastes a slot on someone already handled. Marking a
    /// guest "not greeted" makes them eligible again but does not itself re-queue them.</summary>
    public void MarkGreeted(GuestIdentity guest, bool greeted, DateTimeOffset now)
    {
        if (CurrentSessionId is long id) database.MarkGreeted(venueId, id, guest, greeted, now);
        if (greeted) greeter?.CancelPending(guest);
    }
    /// <summary>The authoritative greeted-state query for the current opening — backed by the same
    /// <see cref="IVenueDatabase.GetSessionVisitors"/> data <see cref="GetTonightVisitors"/> already exposes, not a
    /// second store. Returns false (never greeted) when no opening is active, matching a fresh session's semantics.</summary>
    public bool IsGreeted(GuestIdentity guest) => CurrentSessionId is long id && database.GetSessionVisitors(venueId, id).Any(v => v.Identity.Key == guest.Key && v.Greeted);
    public string ExportRange(AttendanceExportService exporter, DateOnly fromInclusive, DateOnly toInclusive, string directory) => exporter.ExportRangeToExcel(venueId, fromInclusive, toInclusive, directory);

    /// <summary><paramref name="isSessionSeed"/> is true for the one presence scan that immediately follows
    /// <see cref="StartSession"/>/<see cref="ResumeSession"/> (everyone already standing in the venue when the
    /// opening began) — this guest is still seeded into the database (present, visit counted, first-visit-tonight
    /// bookkeeping updated truthfully for reporting) but neither <see cref="FirstVisitTonight"/> nor
    /// <see cref="AutomaticGreetingEligible"/> is raised for them, since opening-start seeding is initialization, not
    /// an arrival. <see cref="AutomaticGreetingEligible"/> is deliberately gated on <see cref="autoGreetEligibilityConsumed"/>
    /// alone, never on <c>change.IsFirstVisitTonight</c> — see that event's doc comment for exactly why conflating the
    /// two was a bug.</summary>
    private void Arrive(GuestIdentity guest, PlayerSnapshot snapshot, bool isSessionSeed)
    {
        guests[guest.Key] = snapshot;
        GuestArrived?.Invoke(guest);
        if (CurrentSessionId is not long id) return;
        var change = database.MarkPresent(venueId, id, guest, clock.UtcNow);
        if (change.IsFirstVisitTonight && !isSessionSeed) FirstVisitTonight?.Invoke(guest);
        if (!isSessionSeed && autoGreetEligibilityConsumed.Add(guest.Key)) AutomaticGreetingEligible?.Invoke(guest);
    }

    private void Depart(GuestIdentity guest, PlayerSnapshot snapshot)
    {
        guests.Remove(guest.Key);
        GuestDeparted?.Invoke(guest);
        if (CurrentSessionId is long id) database.MarkAbsent(venueId, id, guest, clock.UtcNow);
    }
}

/// <summary>Persistent Greeter configuration (Settings → Modules → Greeter) — the small, simple-value half of
/// Greeter's config; the preset *library* and hotbar assignments live in <see cref="IVenueDatabase"/> instead
/// (donor: presets/hotbar in SQLite, everything else in <c>Configuration</c> — the same split, reproduced).
/// <see cref="ActivePresetId"/> is persisted so switching venues and back restores whichever DJ was active, rather
/// than always resetting to "None."</summary>
public sealed record GreeterSettings(bool AutoGreetEnabled = true, int GreetDelaySeconds = 0, bool Enabled = true, long? ActivePresetId = null);

/// <summary>Full standalone-parity Greeter: a saved preset *library* (any number of presets, via
/// <see cref="IVenueDatabase"/>) plus five hotbar slots each assignable to any library preset — donor's exact
/// "Hot Buttons" + "Preset Management" model, not VenueOS's previous fixed-five-presets simplification.
/// <b>Greeter does not own greeted state.</b> <see cref="AttendanceService"/> is the single authoritative source of
/// truth for whether a guest has been greeted in the current opening (a live-verified bug: Greeter previously kept
/// its own <c>HashSet</c> that was never cleared when a new opening started, so it could show a stale "N greeted
/// this session" count and silently refuse to re-queue someone Attendance correctly showed as Not Greeted, left over
/// from an earlier opening in the same venue visit). Greeter now queries Attendance via <paramref name="isGreeted"/>
/// before queuing, and reports a successful completion back via <paramref name="onGreetingCompleted"/> — it never
/// decides or stores the boolean itself. <paramref name="isPresent"/>/<paramref name="isGreeted"/>/
/// <paramref name="onGreetingCompleted"/> follow the same lightweight delegate-injection pattern already used
/// elsewhere in this codebase rather than a direct circular reference to <see cref="AttendanceService"/> (which
/// itself already holds an optional reference to this class).</summary>
public sealed class GreeterService(IClock clock, ChatCommandService chat, IVenueDatabase database, Func<GuestIdentity, bool>? isPresent = null, Action<string>? logDecision = null, Func<GuestIdentity, bool>? isGreeted = null, Action<GuestIdentity>? onGreetingCompleted = null)
{
    private const int InterGuestDelaySeconds = 2;
    private readonly Queue<GuestIdentity> queue = new(); private readonly HashSet<string> queued = new(StringComparer.Ordinal);
    private CancellationTokenSource contextCancellation = new(); private GreetingJob? current; private GreeterSettings settings = new(); private DateTimeOffset nextGuestStartAt = DateTimeOffset.MinValue;
    private Guid venueId;

    public event Action<GuestIdentity>? GreetingReadyToFinalize; public event Action<GuestIdentity>? GreetingCompleted;
    public int PendingCount => queue.Count + (current is null ? 0 : 1);
    /// <summary>Transient, UI-facing only — never a second source of greeted-state truth. Backs Attendance's
    /// Visitors row states ("Queued"/"Greeting..." vs. the Greet/Mark Greeted actions) so the operator can see a
    /// greeting attempt in flight without it being persisted anywhere; on failure/cancellation a guest simply
    /// reverts to <see cref="GreetingProgress.None"/> with Attendance still Not Greeted, ready to retry.</summary>
    public GreetingProgress GetProgress(GuestIdentity guest)
    {
        if (current is not null && current.Guest.Key == guest.Key) return GreetingProgress.InProgress;
        return queued.Contains(guest.Key) ? GreetingProgress.Queued : GreetingProgress.None;
    }
    public long? ActivePresetId => settings.ActivePresetId;
    public string ActivePresetName => settings.ActivePresetId is long id ? database.GetPreset(venueId, id)?.Name ?? "(Missing preset)" : "None";
    public bool IsEnabled => settings.Enabled;
    public GreeterSettings Settings => settings;

    public void AttachVenue(Guid id) => venueId = id;
    public void Configure(GreeterSettings value) => settings = value;
    public void SelectPreset(long? presetId) => settings = settings with { ActivePresetId = presetId };

    public IReadOnlyList<GreetPresetRecord> GetPresetLibrary() => database.GetPresets(venueId);
    public GreetPresetRecord? GetPreset(long presetId) => database.GetPreset(venueId, presetId);
    public long SavePreset(long? presetId, string name, string line1, string line2, string line3, string command) => database.SavePreset(venueId, presetId, name, line1, line2, line3, command);
    public void DeletePreset(long presetId)
    {
        database.DeletePreset(venueId, presetId);
        if (settings.ActivePresetId == presetId) settings = settings with { ActivePresetId = null };
    }
    public IReadOnlyDictionary<int, long?> GetHotbarAssignments() => database.GetHotbarAssignments(venueId);
    public void SetHotbarAssignment(int slot, long? presetId) => database.SetHotbarAssignment(venueId, slot, presetId);

    /// <summary>Logs exactly one line per decision (never per frame — this is only ever called once per arrival
    /// event, from <see cref="GreetingCoordinator.TryGreet"/>, reached via <see cref="AttendanceService.AutomaticGreetingEligible"/>
    /// or a manual Greet action), via <paramref name="logDecision"/> if wired — a non-spammy trace of "was this guest
    /// queued, and if not, why." The "already greeted" check queries Attendance (<paramref name="isGreeted"/>), never
    /// a local flag.</summary>
    public bool QueueGreeting(GuestIdentity guest)
    {
        if (!settings.Enabled) { logDecision?.Invoke($"Skipped {guest.Key}: Greeter is disabled."); return false; }
        if (isGreeted?.Invoke(guest) ?? false) { logDecision?.Invoke($"Skipped {guest.Key}: already greeted."); return false; }
        if (!queued.Add(guest.Key)) { logDecision?.Invoke($"Skipped {guest.Key}: already queued."); return false; }
        var preset = ActivePreset();
        if (preset is null) { queued.Remove(guest.Key); logDecision?.Invoke($"Skipped {guest.Key}: no active preset assigned."); return false; }
        if (!preset.HasActions) { queued.Remove(guest.Key); logDecision?.Invoke($"Skipped {guest.Key}: active preset '{preset.Name}' has no lines or command."); return false; }
        queue.Enqueue(guest); logDecision?.Invoke($"Queued {guest.Key}: preset '{preset.Name}'."); return true;
    }
    /// <summary>Best-effort local queue cleanup only — Attendance owns whether a guest is greeted (see the
    /// type-level remark); this just stops Greeter from continuing to work on someone Attendance has just been told
    /// is already handled (e.g. a manual "Mark as Greeted"), removing them from the pending queue and, if they're
    /// the in-flight job, cancelling the remaining steps. It never sets or clears a "greeted" flag of its own.</summary>
    public void CancelPending(GuestIdentity guest)
    {
        if (queued.Remove(guest.Key))
        {
            var remaining = queue.Where(x => x.Key != guest.Key).ToArray();
            queue.Clear(); foreach (var item in remaining) queue.Enqueue(item);
        }
        if (current is not null && current.Guest.Key == guest.Key) { current = null; nextGuestStartAt = clock.UtcNow.AddSeconds(InterGuestDelaySeconds); }
    }
    public void ResetForVenue() { contextCancellation.Cancel(); contextCancellation.Dispose(); contextCancellation = new(); queue.Clear(); queued.Clear(); current = null; nextGuestStartAt = DateTimeOffset.MinValue; }
    /// <summary>A step is only ever considered sent once <see cref="ChatCommandService"/> confirms the transport
    /// actually accepted it (see <see cref="OnStepDispatched"/>) — merely calling <see cref="ChatCommandService.Enqueue"/>
    /// is not "sent." This was a live-verified bug: the previous version advanced <c>Step</c>/called <see cref="Complete"/>
    /// immediately after enqueueing, so a guest could be marked greeted (via <paramref name="onGreetingCompleted"/>)
    /// even when the underlying transport never actually transmitted anything to FFXIV — indistinguishable, from
    /// Attendance's perspective, from a real completed greeting.</summary>
    public void Tick()
    {
        if (current is null)
        {
            if (clock.UtcNow < nextGuestStartAt || !queue.TryDequeue(out var guest)) return;
            if (!CanGreetNow(guest)) { Drop(guest, "no longer present when their turn came up"); return; }
            var preset = ActivePreset();
            if (preset is null || !preset.HasActions) { Drop(guest, "active preset changed/cleared before their turn came up"); return; }
            logDecision?.Invoke($"Started greeting {guest.Key} with preset '{preset.Name}'.");
            current = new(guest, BuildSteps(preset), clock.UtcNow.AddSeconds(settings.GreetDelaySeconds));
        }
        if (current.AwaitingDispatch) return; // a step is already in flight — wait for the transport to confirm or fail it
        if (clock.UtcNow < current.NextAt) return;
        if (!CanGreetNow(current.Guest)) { Drop(current.Guest, "left mid-greeting"); return; }
        if (current.Step >= current.Steps.Count) { Complete(current.Guest); return; }
        var step = current.Steps[current.Step];
        var message = step.IsCommand
            ? step.Content.StartsWith('/') ? step.Content : $"/{step.Content}"
            : $"/tell {Target(current.Guest)} {step.Content.Replace("<name>", current.Guest.Name, StringComparison.OrdinalIgnoreCase)}";
        message = message.Replace("<name>", current.Guest.Name, StringComparison.OrdinalIgnoreCase);
        var sendingGuest = current.Guest; var stepIndex = current.Step; var stepCount = current.Steps.Count;
        logDecision?.Invoke($"Transport: submitting {(step.IsCommand ? "command/raw" : "tell")} step {stepIndex + 1}/{stepCount} for {sendingGuest.Key}.");
        current = current with { AwaitingDispatch = true };
        chat.Enqueue(new(message, contextCancellation.Token, (success, error) => OnStepDispatched(sendingGuest, success, error)));
    }
    /// <summary>The real acceptance gate for a greeting step. A stale callback — the guest has since been dropped,
    /// completed, or a new job for the same key started — is ignored via the guest-key/<see cref="GreetingJob.AwaitingDispatch"/>
    /// check, since <see cref="CancelPending"/>/<see cref="ResetForVenue"/> can replace or null out <see cref="current"/>
    /// before an already-enqueued command's result comes back.</summary>
    private void OnStepDispatched(GuestIdentity guest, bool success, Exception? error)
    {
        if (current is null || current.Guest.Key != guest.Key || !current.AwaitingDispatch) return;
        if (!success) { Drop(guest, $"chat transport failed to send a step: {error?.Message ?? "no confirmation from the transport"}"); return; }
        logDecision?.Invoke($"Transport: step confirmed sent for {guest.Key}.");
        current = current with { AwaitingDispatch = false, Step = current.Step + 1, NextAt = clock.UtcNow.AddSeconds(2) };
        if (current.Step >= current.Steps.Count) Complete(guest);
    }
    private GreetPresetRecord? ActivePreset() => settings.ActivePresetId is long id ? database.GetPreset(venueId, id) : null;
    private static IReadOnlyList<GreetingStep> BuildSteps(GreetPresetRecord preset)
    {
        var steps = preset.MessageLines.Select(x => new GreetingStep(false, x)).ToList();
        if (!string.IsNullOrWhiteSpace(preset.Command)) steps.Add(new(true, preset.Command));
        return steps;
    }
    /// <summary>Re-validated before starting a guest's greeting and before every step — a guest who leaves mid
    /// sequence is dropped cleanly instead of continuing to receive tells after they've gone (donor parity:
    /// <c>canGreetNow</c> in <c>venuestatusandgreet</c>'s <c>GreeterService</c>).</summary>
    private bool CanGreetNow(GuestIdentity guest) => isPresent?.Invoke(guest) ?? true;
    /// <summary>Fires <see cref="GreetingReadyToFinalize"/> first (VIP's public shout/yell depends on this
    /// happening before the guest is recorded as greeted — the documented arrival order is unchanged), then reports
    /// completion to Attendance via <paramref name="onGreetingCompleted"/> — the single call site that makes a
    /// guest "greeted," now living in <see cref="AttendanceService.MarkGreeted"/>, not here. Only ever reached once
    /// every step of the sequence has been confirmed actually sent (see <see cref="OnStepDispatched"/>).</summary>
    private void Complete(GuestIdentity guest) { GreetingReadyToFinalize?.Invoke(guest); queued.Remove(guest.Key); current = null; nextGuestStartAt = clock.UtcNow.AddSeconds(InterGuestDelaySeconds); onGreetingCompleted?.Invoke(guest); logDecision?.Invoke($"Greeting completed for {guest.Key}."); GreetingCompleted?.Invoke(guest); }
    private void Drop(GuestIdentity guest, string reason) { queued.Remove(guest.Key); current = null; nextGuestStartAt = clock.UtcNow.AddSeconds(InterGuestDelaySeconds); logDecision?.Invoke($"Dropped {guest.Key}: {reason}."); }
    /// <summary>Donor-proven <c>/tell</c> recipient format — unquoted <c>Name@World</c>, no spaces around <c>@</c>,
    /// the name's own internal space (e.g. "Kei Joi") preserved exactly. The game's own chat parser recognizes
    /// <c>Name@World</c> as a single target token up to the next space, then treats everything after that as the
    /// message — a quoted <c>"Name@World"</c> variant (this class's own previous format) is not the primary
    /// live-verified-working syntax and must not be used here.</summary>
    private static string Target(GuestIdentity guest) => $"{guest.Name.Replace("\"", "'", StringComparison.Ordinal)}@{guest.HomeWorld.Replace("\"", "'", StringComparison.Ordinal)}";
    private readonly record struct GreetingStep(bool IsCommand, string Content);
    private sealed record GreetingJob(GuestIdentity Guest, IReadOnlyList<GreetingStep> Steps, DateTimeOffset NextAt, int Step = 0, bool AwaitingDispatch = false);
}

public enum VipAnnouncementChannel { Shout, Yell }
/// <summary><see cref="CustomTell"/> is this VIP's own private recognition message — live-verified correction:
/// there used to be exactly one shared/global recognition-tell template for every VIP (<c>VipSettings.RecognitionTemplate</c>,
/// now removed); the operator wanted a distinct private message per VIP instead. Empty is a valid, deliberate value
/// (see <see cref="GreetingCoordinator.TryGreet"/>'s doc comment) — it means "no private tell for this VIP," not
/// "not configured yet," and must never fall back to any other value.</summary>
public sealed record VipRecord(string CharacterName, string HomeWorld, bool Enabled, string CustomTell, string PublicAnnouncement, VipAnnouncementChannel Channel, string? Notes = null)
{ public string Key => new GuestIdentity(CharacterName, HomeWorld).Key; }
public sealed record VipSettings(List<VipRecord> Records)
{
    /// <summary>Migration-only: reads the old, now-removed global <c>RecognitionTemplate</c> value out of a payload
    /// saved before this field existed (the JSON property name is unchanged so old data still binds here), purely so
    /// <see cref="VipOrchestrationService.MigrateLegacyRecognitionTemplate"/> can backfill it into any record whose
    /// own <see cref="VipRecord.CustomTell"/> is still empty. Never read anywhere else, and never round-tripped back
    /// into storage once migration clears it — see that method's doc comment for why this makes the migration a
    /// genuine one-time event rather than something that could silently reapply after an operator deliberately
    /// empties a VIP's custom tell later.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("RecognitionTemplate")]
    public string? LegacyRecognitionTemplate { get; init; }
    public static VipSettings Default() => new([]);
}

/// <summary>VIP now owns exactly one thing: its settings/roster, plus a pure lookup — <b>not</b> arrival processing,
/// idempotence, or any greeting sequencing (that moved to <see cref="GreetingCoordinator"/>, the one shared
/// orchestration entry point every greeting path — automatic arrival, VIP or not, and manual Greet from
/// Attendance — now goes through). <b>History (both now fixed):</b> an earlier design had VIP's own <c>OnArrival</c>
/// subscribed directly to <see cref="PresenceService.Arrived"/> *alongside* a second, independent subscription
/// (Attendance's arrival event, wired in the composition root) that could also call
/// <see cref="GreeterService.QueueGreeting"/> for the very same arrival — whichever fired first won the queue slot,
/// and if VIP's own call lost that race, it still believed it had queued the guest, silently dropping the VIP public
/// announcement. That dual-subscription design was later consolidated into the single
/// <see cref="AttendanceService.AutomaticGreetingEligible"/> trigger described on <see cref="GreetingCoordinator"/>'s
/// own doc comment. A second, separate bug (also fixed): using <see cref="AttendanceService.FirstVisitTonight"/> as
/// that single trigger meant a guest seeded at opening start could never automatically re-trigger for the rest of
/// the calendar night, VIP or not, since the seeding still consumed the database's own "first visit tonight" flag —
/// see <see cref="AttendanceService.AutomaticGreetingEligible"/>'s doc comment for the fix.</summary>
public sealed class VipOrchestrationService
{
    private VipSettings settings = VipSettings.Default();
    public bool IsEnabled => settings.Records.Any(x => x.Enabled);
    public int EnabledCount => settings.Records.Count(x => x.Enabled);
    public VipSettings Settings => settings;
    public void Configure(VipSettings value) => settings = value;
    /// <summary>The only VIP-specific decision left: does this guest resolve to a currently-enabled VIP record?
    /// A disabled record (present in the roster but toggled off) returns null here — deliberately treated identically
    /// to "not a VIP at all," so a disabled VIP still receives the normal Greeter greeting rather than being dropped.</summary>
    public VipRecord? TryFindEnabledVip(GuestIdentity guest) => settings.Records.FirstOrDefault(x => x.Enabled && x.Key == guest.Key);

    /// <summary>One-time, idempotent migration for the removed global recognition-tell setting: any record whose
    /// <see cref="VipRecord.CustomTell"/> is still empty is backfilled from <see cref="VipSettings.LegacyRecognitionTemplate"/>
    /// (never overwriting an already-populated custom tell — requirement: existing per-VIP data is never overwritten
    /// by migration), and the legacy value is cleared from the returned settings so it is never consulted again once
    /// the result is persisted — a caller who saves the returned settings makes this permanent, so a user who later
    /// deliberately empties a VIP's custom tell is never silently re-populated by stale legacy data. Returns the
    /// original instance unchanged, with <c>Migrated: false</c>, if there was nothing to migrate (already migrated,
    /// or a fresh install that never had the global setting) — the caller should only re-save when <c>Migrated</c>
    /// is true, to avoid writing on every single load.</summary>
    public static (VipSettings Settings, bool Migrated) MigrateLegacyRecognitionTemplate(VipSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LegacyRecognitionTemplate)) return (settings, false);
        var template = settings.LegacyRecognitionTemplate;
        var migratedRecords = settings.Records.Select(r => string.IsNullOrWhiteSpace(r.CustomTell) ? r with { CustomTell = template } : r).ToList();
        return (settings with { Records = migratedRecords, LegacyRecognitionTemplate = null }, true);
    }
}

public enum GreetingSource { AutomaticArrival, ManualAttendance }
/// <summary>Purely transient — see <see cref="GreeterService.GetProgress"/>'s doc comment. Not persisted, not a
/// second greeted-state authority.</summary>
public enum GreetingProgress { None, Queued, InProgress }

/// <summary>The single greeting orchestration entry point — used by the composition root's ONE automatic-arrival
/// trigger (<see cref="AttendanceService.AutomaticGreetingEligible"/>, for VIP <i>and</i> non-VIP guests alike) and
/// the manual "Greet" action in Attendance's Visitors tab. <b>Live-verified correction (this exists to fix a specific
/// bug):</b> an earlier revision routed VIP arrivals through a second, independent <see cref="PresenceService.Arrived"/>
/// subscription of this class's own, competing with the normal-guest trigger — the two event chains raced and the
/// non-VIP path could silently never reach this method. There must be exactly one automatic entry point; this class
/// performs the VIP-vs-normal decision internally (see <see cref="TryGreet"/>) so the caller never has to. A second,
/// separate live-verified bug fixed later: that ONE trigger was originally <see cref="AttendanceService.FirstVisitTonight"/>,
/// whose underlying database "first visit tonight" flag is silently consumed by opening-start seeding — see
/// <see cref="AttendanceService.AutomaticGreetingEligible"/>'s own doc comment for why it replaced
/// <c>FirstVisitTonight</c> as the trigger. See <see cref="VipOrchestrationService"/>'s type-level remark for more
/// on the VIP-lookup split. This class decides
/// *whether and how* a guest gets greeted (VIP recognition tell first if applicable, then the normal Greeter
/// sequence, then the optional VIP public announcement once Greeter finishes) — it never itself records "greeted":
/// that remains <see cref="AttendanceService"/>'s job alone, via <see cref="GreeterService"/>'s existing
/// <c>onGreetingCompleted</c> callback (unchanged), so a VIP recognition tell being sent — or even a failed Greeter
/// handoff — can never by itself mark a guest greeted. <paramref name="isGreeted"/> is a delegate (not a direct
/// <see cref="AttendanceService"/> reference) for the same reason <see cref="GreeterService"/> uses one — testable
/// without a full Attendance/database session, and no risk of a circular constructor dependency; production wires
/// it to <see cref="AttendanceService.IsGreeted"/> directly (no captured-reference trick needed here, since
/// <c>AttendanceService</c> already exists by the time this is constructed in <c>Plugin.cs</c>).</summary>
public sealed class GreetingCoordinator(Func<GuestIdentity, bool> isGreeted, GreeterService greeter, VipOrchestrationService vip, ChatCommandService chat, Action<string>? logDecision = null)
{
    private readonly Dictionary<string, VipRecord> awaitingPublic = new(StringComparer.Ordinal);
    private readonly HashSet<string> awaitingVipHandoff = new(StringComparer.Ordinal);
    private CancellationTokenSource contextCancellation = new();

    public void Attach() => greeter.GreetingReadyToFinalize += OnGreetingReadyToFinalize;
    public void ResetForVenue() { contextCancellation.Cancel(); contextCancellation.Dispose(); contextCancellation = new(); awaitingPublic.Clear(); awaitingVipHandoff.Clear(); }

    /// <summary>Attempts to greet <paramref name="guest"/> right now: skips if Attendance already reports them
    /// greeted; otherwise resolves a VIP record — if found and its own <see cref="VipRecord.CustomTell"/> is
    /// non-empty, sends that VIP's private recognition tell (unconditional on Greeter's own Auto Greet toggle,
    /// matching donor parity — <see cref="GreeterSettings.AutoGreetEnabled"/> only gates the plain-guest branch
    /// below, and even then only for an automatic arrival, never a manual one) and, only once the transport actually
    /// confirms that tell was sent (see <see cref="OnVipTellDispatched"/>), hands off to Greeter; if the VIP's
    /// <see cref="VipRecord.CustomTell"/> is empty (a deliberate, supported configuration — "public recognition
    /// only"), skips the private tell entirely and hands off to Greeter directly, with no <c>/tell</c> ever sent for
    /// an empty message. Either way, once Greeter accepts the guest, the VIP's own <see cref="VipRecord.PublicAnnouncement"/>
    /// is remembered for <see cref="OnGreetingReadyToFinalize"/> only if it is itself non-empty — an empty public
    /// message is likewise a deliberate "private-only" VIP, not a failure. A disabled VIP record (treated identically
    /// to no record at all) hands off to Greeter directly, exactly like a normal guest — its custom tell/public
    /// message are never read or sent. Never marks anyone greeted itself — see the type-level remark. Returns whether
    /// a greeting attempt was actually started (for a VIP guest with a non-empty custom tell this means "the tell
    /// was submitted," not yet "Greeter accepted the guest" — that confirmation arrives asynchronously; false covers
    /// "already greeted," "Auto Greet disabled," "already queued," "Greeter disabled," "no active preset," etc. —
    /// see <see cref="GreeterService.QueueGreeting"/>'s own <c>logDecision</c> output for the exact reason).</summary>
    public bool TryGreet(GuestIdentity guest, GreetingSource source)
    {
        if (isGreeted(guest)) { logDecision?.Invoke($"{source}: {guest.Key} skipped — already greeted."); return false; }
        var record = vip.TryFindEnabledVip(guest);
        if (record is not null)
        {
            if (!awaitingVipHandoff.Add(guest.Key)) { logDecision?.Invoke($"{source}: {guest.Key} skipped — a VIP recognition attempt is already in flight."); return false; }
            if (string.IsNullOrWhiteSpace(record.CustomTell))
            {
                logDecision?.Invoke($"{source}: {guest.Key} matched an enabled VIP record with no custom private tell configured — continuing straight to Greeter.");
                awaitingVipHandoff.Remove(guest.Key);
                return HandOffVipToGreeter(guest, record, source);
            }
            logDecision?.Invoke($"{source}: {guest.Key} matched an enabled VIP record — sending this VIP's custom private tell.");
            // Donor-proven /tell recipient format: unquoted Name@World, no spaces around @ — see GreeterService.Target's
            // doc comment. A quoted "Name@World" variant is not the live-verified-working syntax.
            var safeName = guest.Name.Replace("\"", "'", StringComparison.Ordinal); var safeWorld = guest.HomeWorld.Replace("\"", "'", StringComparison.Ordinal);
            chat.Enqueue(new($"/tell {safeName}@{safeWorld} {record.CustomTell.Replace("<name>", guest.Name, StringComparison.OrdinalIgnoreCase)}", contextCancellation.Token,
                (success, error) => OnVipTellDispatched(guest, record, source, success, error)));
            return true;
        }
        if (source == GreetingSource.AutomaticArrival && !greeter.Settings.AutoGreetEnabled) { logDecision?.Invoke($"{source}: {guest.Key} skipped — Auto Greet First Visit Tonight is disabled."); return false; }
        logDecision?.Invoke($"{source}: {guest.Key} is not an enabled VIP — continuing to the normal Greeter.");
        return greeter.QueueGreeting(guest);
    }

    /// <summary>The real acceptance gate for the VIP recognition tell — the exact live-verified bug this exists to
    /// fix: a VIP arrival was previously handed to Greeter (and could end up marked greeted once Greeter eventually
    /// completed) regardless of whether the recognition tell itself ever actually transmitted, because nothing
    /// checked. Greeter is now only ever queued after the tell is confirmed sent; a failed/unconfirmed tell aborts
    /// the whole attempt (Greeter is never queued, nothing is ever marked greeted for it). A stale callback (a venue
    /// switch cancelled this attempt, or it was otherwise already resolved) is ignored via the guard on
    /// <see cref="awaitingVipHandoff"/>.</summary>
    private void OnVipTellDispatched(GuestIdentity guest, VipRecord record, GreetingSource source, bool success, Exception? error)
    {
        if (!awaitingVipHandoff.Remove(guest.Key)) return;
        if (!success) { logDecision?.Invoke($"{source}: {guest.Key} VIP recognition tell failed to send ({error?.Message ?? "no confirmation from the transport"}) — aborting; Greeter was never handed this guest."); return; }
        logDecision?.Invoke($"{source}: {guest.Key} VIP recognition tell confirmed sent — handing off to Greeter.");
        HandOffVipToGreeter(guest, record, source);
    }

    /// <summary>Shared tail of the VIP flow, reached either immediately (empty custom tell) or after the tell is
    /// confirmed sent. Remembers the VIP's own <see cref="VipRecord.PublicAnnouncement"/> for
    /// <see cref="OnGreetingReadyToFinalize"/> only when it is non-empty — an empty public message is a deliberate,
    /// supported "private-only" VIP configuration, not a failure, and must never dispatch an empty <c>/shout</c>
    /// or <c>/yell</c>.</summary>
    private bool HandOffVipToGreeter(GuestIdentity guest, VipRecord record, GreetingSource source)
    {
        if (!greeter.QueueGreeting(guest)) { logDecision?.Invoke($"{source}: {guest.Key} VIP handoff to Greeter did not queue (see Greeter's own skip reason)."); return false; }
        if (!string.IsNullOrWhiteSpace(record.PublicAnnouncement)) awaitingPublic[guest.Key] = record;
        return true;
    }

    /// <summary>Fire-and-forget by design: this fires only after Greeter's sequence has already completed and
    /// Attendance has already been told the guest is greeted (see <see cref="GreeterService.Complete"/>'s ordering),
    /// so there is nothing left to abort even if this specific message fails to transmit — only logged for
    /// diagnostics, never gating anything. Never reached for a VIP whose <see cref="VipRecord.PublicAnnouncement"/>
    /// was empty — see <see cref="HandOffVipToGreeter"/>, which never adds such a guest to <see cref="awaitingPublic"/>.</summary>
    private void OnGreetingReadyToFinalize(GuestIdentity guest)
    {
        if (!awaitingPublic.Remove(guest.Key, out var record)) return;
        var command = record.Channel == VipAnnouncementChannel.Shout ? "/shout" : "/yell";
        logDecision?.Invoke($"Transport: submitting VIP public announcement ({command}) for {guest.Key}.");
        chat.Enqueue(new($"{command} {record.PublicAnnouncement}", contextCancellation.Token,
            (success, error) => { if (!success) logDecision?.Invoke($"{guest.Key}: VIP public announcement failed to send ({error?.Message ?? "no confirmation from the transport"})."); }));
    }

    /// <summary>Pass-through so UI code (Attendance's Visitors row actions) can read the transient Queued/InProgress
    /// state without needing its own direct <see cref="GreeterService"/> reference — see
    /// <see cref="GreeterService.GetProgress"/>'s doc comment for why this is transient UI state, not a second
    /// greeted-state authority. A VIP recognition tell awaiting its own dispatch confirmation (before Greeter even
    /// knows about the guest) reports <see cref="GreetingProgress.Queued"/> so the operator never sees a false
    /// "Not Greeted"/actionable row during that brief window.</summary>
    public GreetingProgress GetProgress(GuestIdentity guest) => awaitingVipHandoff.Contains(guest.Key) ? GreetingProgress.Queued : greeter.GetProgress(guest);
}

public sealed class AttendanceModule(AttendanceService attendance, PresenceService presence, VenueProfileService profiles, Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    public ModuleDescriptor Descriptor { get; } = new("core.attendance", "Attendance", "Venue attendance and session history.", "users", DisplayOrder: 2);
    public bool IsEnabled { get; set; } = true;
    private AttendanceSettings settings = new();
    public AttendanceSettings Settings => settings;
    public PresencePolicy Policy => new(attendance.ActiveTerritoryLock, attendance.ActiveFixedCenter, settings.UseDistanceFilter ? settings.RadiusYalms : null, attendance.ActiveAreaMode);
    public Task InitializeAsync(ModuleContext c, CancellationToken t) { attendance.Attach(); return Task.CompletedTask; }
    public Task OnVenueChangedAsync(VenueContext c, CancellationToken t)
    {
        attendance.DetachSession();
        attendance.AttachVenue(c.VenueId);
        settings = profiles.GetModuleConfig(c.VenueId, Descriptor.Id, 1, () => new AttendanceSettings());
        attendance.RebuildCurrent(presence.Current);
        return Task.CompletedTask;
    }
    public void Tick(DateTimeOffset now) => attendance.Tick(now, settings.TrackingPollIntervalSeconds);
    public void Draw() => draw?.Invoke();
    public void DrawSettings() => (drawSettings ?? draw)?.Invoke();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class GreeterModule(GreeterService greeter, VenueProfileService profiles, Action? draw = null, Action? drawSettings = null) : IVenueModule
{
    public ModuleDescriptor Descriptor { get; } = new("core.greeter", "Greeter", "DJ greeting presets and guest greetings.", "message", ["core.attendance"], DisplayOrder: 3);
    public bool IsEnabled { get; set; } = true;
    public Task InitializeAsync(ModuleContext c, CancellationToken t) => Task.CompletedTask;
    public Task OnVenueChangedAsync(VenueContext c, CancellationToken t)
    {
        greeter.ResetForVenue();
        greeter.AttachVenue(c.VenueId);
        greeter.Configure(profiles.GetModuleConfig(c.VenueId, Descriptor.Id, 1, () => new GreeterSettings()));
        return Task.CompletedTask;
    }
    public void Tick(DateTimeOffset now) => greeter.Tick();
    public void Draw() => draw?.Invoke();
    public void DrawSettings() => (drawSettings ?? draw)?.Invoke();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
/// <summary>VIP's own lifecycle is now just settings load/save; the shared <see cref="GreetingCoordinator"/>'s
/// lifecycle (<c>Attach</c>/<c>ResetForVenue</c>) is driven from here too, since it's still naturally tied to the
/// VIP module's own dependency slot (<c>core.vip</c> depends on <c>core.greeter</c>) — moving it doesn't change
/// module ordering or require a new module registration. <c>Attach</c> no longer takes a <c>PresenceService</c>: the
/// coordinator has no arrival subscription of its own (see its type-level remark) — the composition root's single
/// <see cref="AttendanceService.AutomaticGreetingEligible"/> handler is the only automatic trigger, for VIP and
/// non-VIP guests alike.</summary>
public sealed class VipModule(VipOrchestrationService vip, GreetingCoordinator coordinator, VenueProfileService profiles, Action? draw = null, Action? drawSettings = null) : IVenueModule
{ public ModuleDescriptor Descriptor { get; } = new("core.vip", "VIP", "VIP recognition and entrance announcements.", "star", ["core.greeter"], DisplayOrder: 4); public bool IsEnabled { get; set; } = true; public Task InitializeAsync(ModuleContext c, CancellationToken t) { coordinator.Attach(); return Task.CompletedTask; }
    public Task OnVenueChangedAsync(VenueContext c, CancellationToken t)
    {
        coordinator.ResetForVenue();
        var loaded = profiles.GetModuleConfig(c.VenueId, Descriptor.Id, 1, VipSettings.Default);
        var (migrated, changed) = VipOrchestrationService.MigrateLegacyRecognitionTemplate(loaded);
        vip.Configure(migrated);
        if (changed) profiles.SaveModuleConfig(c.VenueId, Descriptor.Id, 1, migrated);
        return Task.CompletedTask;
    }
    public void Tick(DateTimeOffset now) { } public void Draw() => draw?.Invoke(); public void DrawSettings() => (drawSettings ?? draw)?.Invoke(); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }

// VenueRaffleSettings/VenueRaffleService/RaffleDashboard/VenueRaffleModule moved to
// VenueOS.Modules.Operations.Raffle.VenueRaffleService.cs (alongside VenueRaffleClient.cs and
// RaffleRealtimeClient.cs) as part of the full Raffle reconstruction (see docs/RAFFLE_RECONSTRUCTION.md) — a
// backend-backed module with this much surface (live wheel realtime client, redraw/exclusion, archive/delete,
// XLSX import/export) gets its own file/folder per NEW_MODULE_GUIDE.md §21.

// Mair's Trivia's settings/service/module moved to VenueOS.Modules.Operations.Trivia.MairsTriviaService.cs — a
// backend-backed module with this much surface (Series, occurrences, player management) gets its own file/folder
// per NEW_MODULE_GUIDE.md §21, matching the precedent already set by PartyFinder/ShoutRunner/Raffle/Tournament/Bingo.

// TournamentCalloutSettings/TournamentCalloutService/TournamentModuleSettings/TournamentDashboard/
// TournamentControlService/TournamentControlModule moved to
// VenueOS.Modules.Operations.Tournament.TournamentControlService.cs — a backend-backed module with this much
// surface (browser, setup, seeding, round operation, correction/rollback, realtime) gets its own file/folder per
// NEW_MODULE_GUIDE.md §21, matching the precedent already set by PartyFinder/ShoutRunner/Raffle/Trivia/Bingo.
// Display name is "Brackets" (module ID remains games.tournament — see VENUEOS_ARCHITECTURE-facing notes in
// TOURNAMENT_CONTROL_BRACKETS_AUDIT.md for why the ID never changes with the display name).

// VenueBingoSettings/VenueBingoService/VenueBingoModule/BingoDashboard moved to
// VenueOS.Modules.Operations.Bingo.VenueBingoService.cs — Bingo, like Party Finder/Trivia/Tournament, gets its own
// file/folder rather than growing this shared file further (NEW_MODULE_GUIDE.md §21).
