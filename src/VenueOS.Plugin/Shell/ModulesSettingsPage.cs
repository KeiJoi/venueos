using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>Module enable/disable and the entry point into each module's own settings contribution. This page owns
/// zero module-specific field knowledge — "Configure" just calls the selected module's own
/// <see cref="IVenueModule.DrawSettings"/>, so a module can grow its settings surface without this file changing.
/// Settings owns navigation/layout/frame; the module owns its configuration content (§27 of the Phase 3D brief).</summary>
internal sealed class ModulesSettingsPage(ModuleHost modules, DiagnosticsService diagnostics, GlobalSettingsService globalSettings, VenueProfileService venues)
{
    private string? configuringModuleId;

    /// <summary>Jumps straight to a module's Configure detail view — the destination for a detached module
    /// window's Settings gear (and available for any future "configure this" entry point).</summary>
    public void OpenConfigure(string moduleId) => configuringModuleId = moduleId;

    public void Draw(VenueTheme theme)
    {
        var configuring = configuringModuleId is null ? null : modules.Modules.FirstOrDefault(x => x.Descriptor.Id == configuringModuleId);
        if (configuring is not null) { DrawConfigureDetail(theme, configuring); return; }

        AppFrame.Draw(theme, "grid", "Modules", "Enable, disable, and configure VenueOS modules.", () => DrawList(theme));
    }

    private void DrawList(VenueTheme theme)
    {
        UiKit.InfoBanner(theme, "Manage which modules are available in VenueOS.", "Disabled modules will not appear on the Home screen and will not load until re-enabled.");
        ImGui.Spacing(); ImGui.Spacing();

        DrawColumnHeaders(theme);
        UiKit.Divider(theme);
        foreach (var module in modules.Modules)
        {
            ImGui.PushID(module.Descriptor.Id);
            DrawModuleRow(theme, module);
            UiKit.Divider(theme);
            ImGui.PopID();
        }

        ImGui.Spacing();
        UiKit.InfoBanner(theme, "Module configuration", "Each module's specific settings can be configured in its dedicated section. Disabling a module preserves its configuration.");
    }

    private static void DrawColumnHeaders(VenueTheme theme)
    {
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextUnformatted("Module");
        ImGui.SameLine(width - 360); ImGui.TextUnformatted("Status");
        ImGui.SameLine(width - 250); ImGui.TextUnformatted("Enabled");
        ImGui.SameLine(width - 120); ImGui.TextUnformatted("Actions");
        ImGui.PopStyleColor();
    }

    private void DrawModuleRow(VenueTheme theme, IVenueModule module)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var rowStart = ImGui.GetCursorScreenPos();
        var descriptionWrapWidth = MathF.Max(80, width - 360 - 40 - 12);
        var descriptionHeight = ImGui.CalcTextSize(module.Descriptor.Description, false, descriptionWrapWidth).Y;

        var iconCenter = rowStart + new Vector2(20, 20);
        AppIcons.Draw(module.Descriptor.Icon, iconCenter, 16, UiKit.ColorU32(module.IsEnabled ? theme.Tokens.TextPrimary : theme.Tokens.TextSecondary));
        ImGui.SetCursorScreenPos(rowStart + new Vector2(40, 0));
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextPrimary));
        ImGui.TextUnformatted(module.Descriptor.DisplayName);
        ImGui.PopStyleColor();
        ImGui.SetCursorScreenPos(rowStart + new Vector2(40, 18));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + descriptionWrapWidth);
        ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Color(theme.Tokens.TextSecondary));
        ImGui.TextUnformatted(module.Descriptor.Description);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(rowStart + new Vector2(width - 360, 8));
        UiKit.StatusBadge(theme, module.IsEnabled ? "Ready" : "Disabled", module.IsEnabled ? ToastLevel.Success : ToastLevel.Information);
        // Stacked below the Ready/Disabled badge (same column, not SameLine) so the two pills never compete for
        // width against the fixed-width Enabled/Actions columns to their right at a narrow window size (§35).
        if (module.Descriptor.UnderDevelopment)
        {
            ImGui.SetCursorScreenPos(rowStart + new Vector2(width - 360, 30));
            UiKit.StatusBadge(theme, "Under Development", ToastLevel.Warning);
        }

        ImGui.SetCursorScreenPos(rowStart + new Vector2(width - 250, 6));
        var enabled = module.IsEnabled;
        if (UiKit.Toggle(theme, "##enabled", ref enabled))
        {
            var wasEnabled = module.IsEnabled;
            module.IsEnabled = enabled;
            globalSettings.SetModuleEnabled(module.Descriptor.Id, enabled);
            if (enabled && !wasEnabled) ActivateNewlyEnabledModule(module);
        }

        ImGui.SetCursorScreenPos(rowStart + new Vector2(width - 120, 6));
        if (UiKit.GhostButton(theme, "Configure", new Vector2(100, 0))) configuringModuleId = module.Descriptor.Id;

        var rowHeight = MathF.Max(module.Descriptor.UnderDevelopment ? 64 : 46, 18 + descriptionHeight + 10);
        ImGui.SetCursorScreenPos(rowStart + new Vector2(0, rowHeight));
    }

    /// <summary>MACRO LIVE QA FIX — release-blocking persistence root cause: a module's own <c>OnVenueChangedAsync</c>
    /// (which every current local module uses to resolve the active venue id and load its per-venue config — see
    /// <c>MacroService.Load</c>/<c>GiveawayService.Load</c>/<c>BlockLettersService.Load</c>) is invoked by
    /// <c>ModuleHost</c> from exactly two places: <c>VenueProfileService.InitializeAsync</c>'s one startup pass, and
    /// <c>SwitchAsync</c> on every venue switch — both gated by <c>ModuleHost.IsolateAsync</c>'s
    /// <c>if (!module.IsEnabled) return;</c> check (NEW_MODULE_GUIDE.md §23). A module that ships
    /// <c>UnderDevelopment: true</c>/disabled-by-default (§22a — every current local module) therefore never
    /// receives that first call at plugin startup. Before this fix, toggling it on right here only ever flipped
    /// <see cref="IVenueModule.IsEnabled"/> with no further effect: the module's own service never learned the
    /// active venue id, so its private venue-id field stayed at its default value and every config it saved before
    /// the NEXT full plugin restart was written under the WRONG per-venue key — live-verified as total data loss
    /// for Macro (macros "worked" for the rest of that session because <c>Draw()</c> reads the same in-memory
    /// <c>Settings</c> object being mutated, but reloading the plugin loaded the REAL venue id's — empty — config).
    /// Giveaways happened not to exhibit this in its own live QA pass purely because it was already enabled (and
    /// therefore already correctly loaded) from a PRIOR session by the time its presets were authored — the
    /// underlying gap is identical in every module using this same, otherwise-correct pattern; this fix closes it
    /// centrally, once, without touching any individual module's own file. Re-invoking <c>OnVenueChangedAsync</c> is
    /// safe by construction — it is specifically designed to be safely re-called on every ordinary venue switch
    /// already (reset in-memory state, reload config for the given venue id) — so this is a pure bug fix, not new
    /// module-specific behavior, and every module's own file is untouched.</summary>
    private void ActivateNewlyEnabledModule(IVenueModule module)
    {
        try
        {
            var current = venues.Current;
            module.OnVenueChangedAsync(new VenueContext(current.Id, current.DisplayName, current.Theme), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            diagnostics.RecordFailure($"Activation failed in {module.Descriptor.Id}: {ex.Message}");
        }
    }

    private void DrawConfigureDetail(VenueTheme theme, IVenueModule module)
    {
        AppFrame.Draw(theme, module.Descriptor.Icon, $"Modules · {module.Descriptor.DisplayName}", module.Descriptor.Description, () =>
        {
            if (UiKit.GhostButton(theme, "← Back to Modules")) configuringModuleId = null;
            ImGui.Spacing(); ImGui.Spacing();
            if (module.Descriptor.UnderDevelopment)
            {
                UiKit.InfoBanner(theme, "Under Development", "This module is not finished and is not covered by live QA. It ships disabled by default — enabling it is at your own risk.", ToastLevel.Warning);
                ImGui.Spacing(); ImGui.Spacing();
            }
            // No extra wrapping card here: a module's DrawSettings() draws its own titled section card(s)
            // (UiKit.BeginSectionCard), exactly like the operational Draw() path never wraps module.Draw() in one
            // either. Wrapping it in a second card used to nest one BeginSectionCard's draw-list channel split
            // inside another's — the two calls share the same window's non-reentrant splitter, which corrupted
            // rendering into one large empty card (see BeginSectionCard's doc comment in UiKit.cs).
            UiKit.SafeDraw(theme, diagnostics, module.Descriptor.Id, module.DrawSettings);
        });
    }
}
