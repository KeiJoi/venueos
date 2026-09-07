using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using VenueOS.Modules.Operations.Bingo;

namespace VenueOS.Plugin.Bingo;

/// <summary>
/// Converts raw Dalamud chat messages into Dalamud-free <see cref="BingoRollObservation"/> values for
/// <c>VenueBingoService.HandleRollObservation</c> — the FFXIV/Dalamud adapter half of the
/// "adapter → normalized event → pure correlation service" split (NEW_MODULE_GUIDE.md's boundary convention). All
/// sender-identity resolution happens here, reading the local player via <see cref="IObjectTable.LocalPlayer"/>
/// (matching this codebase's existing convention, not <c>IClientState</c>, which does not expose it) —
/// <see cref="BingoRollCorrelator"/> on the other side only ever sees the resulting plain <c>bool IsFromLocalHost</c>.
///
/// <b>LIVE REGRESSION FIXED (previous pass)</b>: an earlier version pre-filtered Random results on
/// <see cref="XivChatType.RandomNumber"/> (reflection-confirmed to exist, never live-verified as correct) and used a
/// paraphrased regex missing the donor's required parentheses. Both fixed: no chat-type filter for Random, and the
/// regex is transcribed byte-for-byte from the donor's live-verified pattern (see
/// <see cref="BingoRollTextParser"/>'s doc comment).
///
/// <b>STILL FAILING LIVE after that fix — CONCRETE BEHAVIORAL GAP FOUND (this pass)</b>: a line-by-line comparison
/// of the donor's full 16-stage roll lifecycle (Roll & Call → pending state → command emission → chat subscription
/// → callback → text extraction → sender extraction → regex → self check → range → dedup → state mutation →
/// backend POST → response handling → called-number update → announcement) against VenueOS's own found the FIRST
/// actual difference at the self-check stage: the donor's condition for REJECTING a candidate is
/// <c>!string.IsNullOrEmpty(senderName) &amp;&amp; senderName != "You" &amp;&amp; senderName != localName</c> —
/// which, de Morgan'd, ACCEPTS whenever the rendered sender text is empty/blank, not only when it positively
/// matches "You" or the local character's name. VenueOS's prior <c>IsFromLocalHostViaText</c> required a non-blank
/// sender that positively matched, so if the real `/random 75` result's <c>Sender.TextValue</c> is blank (plausible
/// for a self-attributed, system-style roll message that carries no player-name text at all — this has not yet
/// been live-confirmed either way, hence the diagnostics below), every result would be silently rejected with zero
/// trace: exactly the "0 called, no error" symptom. Fixed by delegating to
/// <see cref="BingoRollSenderIdentity.IsLocalHost"/> (Dalamud-free, unit-tested), which now accepts a blank sender.
/// The exception-swallowing hardening from the prior pass (try/catch, pre-parse diagnostic logging) is kept
/// regardless, since Dalamud's own event dispatch swallowing a throwing subscriber remains a real risk independent
/// of this specific fix:
/// 1. Wraps the ENTIRE handler body in try/catch, logging any exception via <paramref name="onDiagnostic"/> — an
///    exception can now never be silently swallowed from this plugin's own perspective again.
/// 2. Logs the raw, always-safe facts (pending-roll state, LogKind, rendered Sender/Message text, whether a
///    PlayerPayload is present, and — when they diverge — each value's <c>ToString()</c> alongside its
///    <c>TextValue</c>) BEFORE attempting any local-host identity resolution, so even if that later step throws,
///    there is already a diagnostic record proving the message arrived and showing exactly what it contained.
/// 3. Only logs while <paramref name="isAwaitingRoll"/>() is true (bounded to the AwaitingRoll window, per the
///    product requirement not to log chat traffic forever) — but the try/catch and forwarding logic run
///    unconditionally regardless of that flag, since suppressing diagnostics must never suppress functionality.
///
/// <b>/random 75</b>: no chat-type filter; regex requires the parenthesized "(1-75)"/"(out of 75)" range;
/// local-host identity is a PLAIN TEXT comparison only (<c>message.Sender.TextValue</c> against the literal "You",
/// the local character's bare name, or a blank sender) — no PlayerPayload requirement, matching the donor exactly.
///
/// <b>/dice 75 — STILL NOT LIVE-VERIFIED.</b> Unchanged in approach from the prior pass: filtered on
/// <see cref="XivChatType.Party"/> (checked FIRST, before Random, so a genuine Party-chat roll is never misrouted),
/// same core text pattern as Random, PlayerPayload-first identity check falling back to plain text.
///
/// Every message is still required to satisfy <see cref="BingoRollCorrelator"/>'s full acceptance rules before it
/// can become an official call — this adapter only ever produces a candidate observation, never submits anything
/// itself.
/// </summary>
internal sealed class BingoRollChatAdapter
{
    private readonly IObjectTable objectTable;
    private readonly Func<BingoRollObservation, Task> onObservation;
    private readonly Action<string>? onDiagnostic;
    private readonly Func<bool> isAwaitingRoll;
    private readonly Func<string> describePendingRoll;

    public BingoRollChatAdapter(IChatGui chatGui, IObjectTable objectTable, Func<BingoRollObservation, Task> onObservation, Action<string>? onDiagnostic = null, Func<bool>? isAwaitingRoll = null, Func<string>? describePendingRoll = null)
    {
        this.objectTable = objectTable;
        this.onObservation = onObservation;
        this.onDiagnostic = onDiagnostic;
        this.isAwaitingRoll = isAwaitingRoll ?? (() => true);
        this.describePendingRoll = describePendingRoll ?? (() => "pending=unknown");
        chatGui.ChatMessage += OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        // Everything below is wrapped so an exception here can NEVER silently vanish again — see this class's doc
        // comment for exactly why this matters. Dalamud's own event dispatch may otherwise swallow a throwing
        // subscriber with no visible trace at all.
        try
        {
            HandleChatMessage(message);
        }
        catch (Exception ex)
        {
            onDiagnostic?.Invoke($"EXCEPTION in Bingo chat handler: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void HandleChatMessage(IHandleableChatMessage message)
    {
        var text = message.Message.TextValue;
        if (string.IsNullOrWhiteSpace(text)) return;

        var logging = isAwaitingRoll();

        // Dice is routed FIRST, by channel — this is the one thing that lets a Party-chat roll (which shares the
        // same underlying "You roll a N (1-75)." text as Random) be correctly distinguished from a Random result
        // instead of always being claimed by whichever check happens to run first.
        if (message.LogKind == XivChatType.Party)
        {
            HandleCandidate(message, text, BingoRollMode.Dice, logging);
            return;
        }

        // Random: NO chat-type filter, matching the donor's own live-verified implementation exactly (see this
        // class's doc comment for the live-bug postmortem this fixes). Every non-Party message is a candidate.
        HandleCandidate(message, text, BingoRollMode.Random, logging);
    }

    private void HandleCandidate(IHandleableChatMessage message, string text, BingoRollMode mode, bool logging)
    {
        // Logged BEFORE the parser/identity checks run — these facts are always safe to read (no game-state edge
        // case can make TextValue or LogKind throw), so even if something LATER in this method throws, there is
        // already a diagnostic record proving the message arrived and showing exactly what it contained. The
        // pending-roll description is read first of all so a diagnostic trace can answer "was a roll even pending
        // when this event arrived" independent of anything the correlator later decides.
        if (logging)
        {
            var hasPlayerPayload = message.Sender.Payloads.OfType<PlayerPayload>().Any();
            onDiagnostic?.Invoke($"Chat event: {describePendingRoll()}, expectedMode={mode}, logKind={message.LogKind} ({(int)message.LogKind}), sender.TextValue=\"{message.Sender.TextValue}\", message.TextValue=\"{text}\", playerPayload={hasPlayerPayload}");
            // "Raw/rendered variants if they differ" — ToString() on a Dalamud SeString is NOT assumed equivalent
            // to .TextValue; both are logged only when they actually diverge, so a live trace can show whether the
            // donor's exact .TextValue-based extraction is seeing something different from a naive ToString().
            var messageToString = message.Message.ToString();
            var senderToString = message.Sender.ToString();
            if (messageToString != text) onDiagnostic?.Invoke($"Message representation diverges: TextValue=\"{text}\" vs ToString()=\"{messageToString}\"");
            if (senderToString != message.Sender.TextValue) onDiagnostic?.Invoke($"Sender representation diverges: TextValue=\"{message.Sender.TextValue}\" vs ToString()=\"{senderToString}\"");
        }

        var number = BingoRollTextParser.TryParseRollResult(text);
        if (logging) onDiagnostic?.Invoke($"Parser: matched={number is not null}, parsed={(number?.ToString() ?? "null")}");
        if (number is null) return; // ordinary chat — never treated as a roll candidate; nothing further to log

        var isFromLocalHost = mode == BingoRollMode.Dice ? IsFromLocalHostViaPayloadOrText(message) : IsFromLocalHostViaText(message);
        if (logging) onDiagnostic?.Invoke($"Local-host check: {isFromLocalHost} (sender.TextValue=\"{message.Sender.TextValue}\")");

        _ = onObservation(new BingoRollObservation(mode, isFromLocalHost, number, DateTimeOffset.UtcNow));
    }

    /// <summary>Donor-exact self check — delegates the actual decision rule to the Dalamud-free
    /// <see cref="BingoRollSenderIdentity.IsLocalHost"/> (see its doc comment for the exact rule and the live-bug
    /// postmortem) so it is unit-tested directly rather than only by inspection; this method's only job is reading
    /// the two raw strings out of Dalamud types.</summary>
    private bool IsFromLocalHostViaText(IHandleableChatMessage message)
        => BingoRollSenderIdentity.IsLocalHost(message.Sender.TextValue, objectTable.LocalPlayer?.Name.TextValue);

    /// <summary>Dice's sender check: a structured <see cref="PlayerPayload"/> (Name+World) is tried first — Party
    /// messages conventionally carry one for their author — falling back to the same plain-text comparison
    /// <see cref="IsFromLocalHostViaText"/> uses if no payload is present. Whether a self-authored `/dice 75` Party
    /// message actually carries a PlayerPayload has not been confirmed live. World comparison is defensive (only
    /// applied when both sides actually resolve one) specifically so an unpopulated/edge-case
    /// <c>RowRef&lt;World&gt;</c> can never throw or wrongly reject a same-named local player.</summary>
    private bool IsFromLocalHostViaPayloadOrText(IHandleableChatMessage message)
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer is null) return false;
        var localName = localPlayer.Name.TextValue?.Trim();
        if (string.IsNullOrWhiteSpace(localName)) return false;

        var playerPayload = message.Sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (playerPayload is not null)
        {
            if (!string.Equals(playerPayload.PlayerName?.Trim(), localName, StringComparison.OrdinalIgnoreCase)) return false;
            string? localWorld = null;
            string? senderWorld = null;
            try { localWorld = localPlayer.HomeWorld.ValueNullable?.Name.ExtractText().Trim(); } catch { /* defensive — never let a world lookup edge case reject a name-matched local player */ }
            try { senderWorld = playerPayload.World.ValueNullable?.Name.ExtractText().Trim(); } catch { /* same */ }
            return string.IsNullOrWhiteSpace(senderWorld) || string.IsNullOrWhiteSpace(localWorld)
                || string.Equals(senderWorld, localWorld, StringComparison.OrdinalIgnoreCase);
        }

        return IsFromLocalHostViaText(message);
    }
}
