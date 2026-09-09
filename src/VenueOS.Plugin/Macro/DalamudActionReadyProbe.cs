using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using VenueOS.Modules.Operations.Macro;

namespace VenueOS.Plugin.Macro;

/// <summary>The real game-state boundary behind <c>/actionready</c> (MACRO spec §17-§20). Kept entirely inside
/// VenueOS.Plugin, implementing the small <see cref="IActionReadyProbe"/> interface the pure
/// <c>MacroRunner</c> depends on (NEW_MODULE_GUIDE.md §30's "unsafe engine behind a small interface" pattern) — no
/// FFXIVClientStructs/ECommons type ever appears in VenueOS.Modules.Operations.
///
/// RESEARCH (spec §18, full sourcing in docs/MACRO_IMPLEMENTATION.md §17-§18): Macro sends arbitrary, unparsed
/// operator-authored text (spec §9 — VenueOS never reimplements FFXIV command parsing), so unlike a dedicated
/// crafting-rotation plugin (Artisan, PunishXIV) this probe does NOT know which specific action ID the next line
/// will invoke and therefore cannot call <c>ActionManager.GetActionStatus(actionType, actionId, ...)</c> the way
/// action-targeted plugins do (confirmed pattern: UnknownX7/ReAction's <c>ActionStackManager</c>, which gates on
/// <c>GetActionStatus(...) == 0</c> for a KNOWN action id). Instead this probe answers the more general question
/// "/actionready" actually needs — "is the player currently busy executing something" — using the same underlying
/// signals those plugins use for that broader check:
/// <list type="bullet">
/// <item><b>Animation lock</b> — <c>ActionManager.Instance()-&gt;AnimationLock</c> (FFXIVClientStructs, confirmed
/// field). Per xiv.dev's Animation Lock documentation: "an internal timer that player has to wait certain amount of
/// time before they are allowed to use any actions" — nonzero means genuinely busy, covering general
/// abilities/weaponskills/items regardless of job.</item>
/// <item><b>Casting</b> — <see cref="IPlayerCharacter"/>'s own <c>IsCasting</c> property (a fully-managed Dalamud
/// API, Dalamud.Game.ClientState.Objects.Types.IBattleChara — no unsafe access needed for this one), backed up by
/// <see cref="ConditionFlag.Casting"/>/<see cref="ConditionFlag.Casting87"/> for redundancy.</item>
/// <item><b>Crafting/gathering</b> — <see cref="ConditionFlag.ExecutingCraftingAction"/> (value 40) and
/// <see cref="ConditionFlag.ExecutingGatheringAction"/> (value 42) are Dalamud's own documented flags for "an action
/// is currently resolving" during a craft/gather step (the exact busy window while progress/quality is animating),
/// distinct from <see cref="ConditionFlag.Crafting"/>/<see cref="ConditionFlag.PreparingToCraft"/> which just mean
/// "currently inside a crafting session" — this probe treats <c>PreparingToCraft</c> as busy too (the synthesis
/// screen is still opening; no craft action can be sent yet).</item>
/// </list>
/// Crafting and general (combat/weaponskill/item) actions are treated uniformly by this probe — the same "not
/// locked, not casting, not mid-craft/gather-resolution" test answers both, which is why no crafting-vs-general
/// branch exists here; the underlying signals differ but the readiness QUESTION does not. This exact combination of
/// signals has NOT been exercised against a live game session in this environment — see docs/MACRO_IMPLEMENTATION.md's
/// known-limitations section; live QA (spec §56 item 13/14) is required before this is trusted for real crafting
/// macros.</summary>
internal sealed unsafe class DalamudActionReadyProbe(IObjectTable objectTable, ICondition condition) : IActionReadyProbe
{
    public ActionReadyState Query()
    {
        // LocalPlayer lives on IObjectTable in this Dalamud version (see DalamudObjectSnapshotProvider/
        // GiveawaysRollChatAdapter for the same accessor elsewhere in this codebase), not on IClientState.
        var player = objectTable.LocalPlayer;
        if (player is null) return ActionReadyState.Unknown; // not logged in / between zones — never guess "ready"

        if (condition[ConditionFlag.Casting] || condition[ConditionFlag.Casting87] || player.IsCasting) return ActionReadyState.Busy;
        if (condition[ConditionFlag.PreparingToCraft] || condition[ConditionFlag.ExecutingCraftingAction]) return ActionReadyState.Busy;
        if (condition[ConditionFlag.ExecutingGatheringAction]) return ActionReadyState.Busy;

        var actionManager = ActionManager.Instance();
        if (actionManager is null) return ActionReadyState.Unknown;
        if (actionManager->AnimationLock > 0f) return ActionReadyState.Busy;

        return ActionReadyState.Ready;
    }
}
