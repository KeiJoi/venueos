using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using VenueOS.Modules.Operations.Giveaways;
using VenueOS.Services;

namespace VenueOS.Plugin.Giveaways;

/// <summary>
/// Converts raw Dalamud chat messages into Dalamud-free <see cref="ObservedRandomRoll"/> values for
/// <see cref="GiveawayService.HandleRollObservation"/> — the FFXIV/Dalamud adapter half of the "adapter → normalized
/// event → pure correlation service" split (NEW_MODULE_GUIDE.md's boundary convention, also used by
/// <c>VenueOS.Plugin.Bingo.BingoRollChatAdapter</c> for the same underlying `/random` message family).
///
/// <b>Research findings (GIVEAWAYS spec §13/§14/§34):</b> the text SHAPE of a Random result ("Random! You Roll a
/// N.", "Random! You Roll a N (out of M).", "Random! &lt;name&gt; rolls a N.", "Random! &lt;name&gt; rolls a N (out
/// of M).") is recognized purely textually by the Dalamud-free <see cref="RandomRollParser"/> — this mirrors the
/// already-proven, live-verified approach <c>BingoRollTextParser</c> uses for the same message family, and no chat
/// -type filter is applied (Bingo's own adapter doc comment documents a real live regression from over-filtering by
/// <c>XivChatType</c> here). What this adapter does DIFFERENTLY from Bingo's is IDENTITY resolution for another
/// player's roll: Bingo only ever needs a bool ("was this MY roll"), so a plain-text sender comparison is
/// sufficient for it. Giveaways needs the OTHER player's actual <see cref="GuestIdentity"/> (Name+HomeWorld) to
/// track a leaderboard, and the user explicitly warned that Chat2 visually replaces a cross-world player's "@World"
/// text with its own flower-icon glyph — so the rendered/copyable text can NOT be trusted for identity the way
/// Bingo's plain "is this my own roll" bool check trusts it. Per spec §13, this adapter therefore prefers the
/// structured <see cref="PlayerPayload"/> Dalamud embeds inline in the message's own <c>SeString</c> payload list
/// at the other player's name (giving Name+World directly, unaffected by Chat2's purely visual rendering) and
/// falls back to the parser's plain-text name capture ONLY when no such payload is present — in which case
/// HomeWorld is deliberately left unresolved ("", never guessed — NEW_MODULE_GUIDE.md §28's ambiguity rule) rather
/// than trusting the rendered text's (possibly Chat2-altered) representation of it.
///
/// <b>Self identity</b> (spec §16) never depends on chat payload data at all — "You Roll" resolves directly to the
/// local player's own <c>IPlayerCharacter</c> via <see cref="IObjectTable.LocalPlayer"/> (this codebase's
/// established convention for local-player lookups — see <c>BingoRollChatAdapter</c>/<c>DalamudTargetedPlayerProvider</c>),
/// never a participant literally named "You".
///
/// <b>Not yet live-verified</b> (must not be claimed as live-tested until the user actually runs a giveaway in
/// Dalamud — NEW_MODULE_GUIDE.md §30/§41a/§43): whether a same-world player's Random-result name segment reliably
/// carries a <see cref="PlayerPayload"/> the same way a cross-world one does, and Chat2's exact effect (if any) on
/// the underlying SeString payload structure vs. only its rendering. The plain-text fallback path exists
/// specifically to degrade safely (an ambiguous, never-invented HomeWorld) if either assumption turns out wrong.
/// </summary>
internal sealed class GiveawaysRollChatAdapter
{
    private readonly IObjectTable objectTable;
    private readonly Action<ObservedRandomRoll> onObservation;
    private readonly Func<bool> isAcceptingRolls;
    private readonly Action<string>? onDiagnostic;

    public GiveawaysRollChatAdapter(IChatGui chatGui, IObjectTable objectTable, Action<ObservedRandomRoll> onObservation, Func<bool> isAcceptingRolls, Action<string>? onDiagnostic = null)
    {
        this.objectTable = objectTable;
        this.onObservation = onObservation;
        this.isAcceptingRolls = isAcceptingRolls;
        this.onDiagnostic = onDiagnostic;
        chatGui.ChatMessage += OnChatMessage;
    }

    public void Unsubscribe(IChatGui chatGui) => chatGui.ChatMessage -= OnChatMessage;

    private void OnChatMessage(IHandleableChatMessage message)
    {
        // Wrapped so a throwing subscriber can never be silently swallowed by Dalamud's own event dispatch — the
        // exact same defensive shape BingoRollChatAdapter uses for this reason.
        try
        {
            HandleChatMessage(message);
        }
        catch (Exception ex)
        {
            onDiagnostic?.Invoke($"EXCEPTION in Giveaways chat handler: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void HandleChatMessage(IHandleableChatMessage message)
    {
        // Cheap short-circuit — GIVEAWAYS spec §19: a Random message arriving before the giveaway is actively
        // accepting rolls (before the Start block finishes, after the window closes, or with nothing running at
        // all) is never a candidate. GiveawayService.HandleRollObservation re-checks this authoritatively too, so
        // this is purely a fast-path, not the actual gate.
        if (!isAcceptingRolls()) return;

        var text = message.Message.TextValue;
        var parsed = RandomRollParser.TryParse(text);
        if (parsed is null) return; // ordinary chat — never a roll candidate

        var player = parsed.Value.IsSelf ? ResolveSelf() : ResolveOtherPlayer(message, parsed.Value.OtherPlayerNameText!);
        if (player is null) { onDiagnostic?.Invoke($"Giveaways: could not resolve roll identity for \"{text}\""); return; }

        onObservation(new ObservedRandomRoll(player, parsed.Value.Value, parsed.Value.Kind));
    }

    private GuestIdentity? ResolveSelf()
    {
        var local = objectTable.LocalPlayer;
        if (local is null) return null;
        var name = local.Name.TextValue?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;
        var world = SafeWorldName(() => local.HomeWorld.ValueNullable?.Name.ExtractText()) ?? SafeWorldName(() => local.CurrentWorld.ValueNullable?.Name.ExtractText()) ?? "";
        return new GuestIdentity(name, world);
    }

    /// <summary>Structured payload preferred over the parser's plain-text name capture — see this class's doc
    /// comment for exactly why (Chat2's cross-world rendering). <paramref name="fallbackNameText"/> is used only
    /// when no <see cref="PlayerPayload"/> is present at all, and even then HomeWorld is left unresolved rather
    /// than guessed.</summary>
    private static GuestIdentity ResolveOtherPlayer(IHandleableChatMessage message, string fallbackNameText)
    {
        var payload = message.Message.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (payload is null) return new GuestIdentity(fallbackNameText, "");

        var name = string.IsNullOrWhiteSpace(payload.PlayerName) ? fallbackNameText : payload.PlayerName.Trim();
        var world = SafeWorldName(() => payload.World.ValueNullable?.Name.ExtractText()) ?? "";
        return new GuestIdentity(name, world);
    }

    private static string? SafeWorldName(Func<string?> read)
    {
        try { var value = read()?.Trim(); return string.IsNullOrWhiteSpace(value) ? null : value; }
        catch { return null; } // defensive — a world lookup edge case must never crash roll capture (matches BingoRollChatAdapter's own precedent)
    }
}
