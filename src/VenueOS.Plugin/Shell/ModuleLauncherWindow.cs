using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin.Shell;

/// <summary>The Module Launcher — a small, always-available hotbar for quick module access, independent of the main
/// tablet (it works whether or not the tablet is open). Drawn unconditionally every frame from <c>Plugin.Draw</c>,
/// gated only on the same central session-presentation gate (<c>sessionGate.CanRenderGeneralUi</c>) every other
/// VenueOS window already uses, and on <see cref="LauncherSettings.Enabled"/> — this <b>is</b> the documented future
/// Global UI Login Gate attachment point; no new gate is built for this pass.
///
/// Live QA follow-up: the launcher is icon-only, always — this is no longer a toggleable mode (the previous
/// icon+name layout consumed too much screen space). The hover tooltip carries the module's full
/// <see cref="ModuleDescriptor.DisplayName"/> in its place; see <see cref="DrawButton"/>.
///
/// Click routing goes exclusively through the shared <see cref="ModuleWindowManager"/>'s public surface — the
/// launcher never touches a module's operational state or config directly, matching the presentation/operational
/// separation the rest of this pass enforces. Layout/eligibility math is the pure, unit-tested
/// <see cref="LauncherEntries"/>/<see cref="LauncherLayout"/> in <c>VenueOS.Core</c>; this class only turns that into
/// ImGui draw calls.</summary>
internal static class ModuleLauncherWindow
{
    private const float StripHeight = 26f;
    private const float ButtonWidth = 44f;
    private const float ButtonHeight = 40f;

    public static void Draw(VenueTheme theme, ModuleHost modules, GlobalSettingsService globalSettings, ModuleWindowManager windowManager)
    {
        var launcher = globalSettings.Launcher;
        if (!launcher.Enabled) return;

        var entries = LauncherEntries.Eligible(
            modules.Modules.Select(m => new LauncherModuleInfo(m.Descriptor.Id, m.IsEnabled)).ToArray(),
            launcher.ShowOnLauncher, launcher.ModuleOrder);

        UiKit.PushWindowTheme(theme);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
        if (launcher.Locked) flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;

        if (launcher.Locked)
        {
            // Forced every frame from the persisted values so it can't drift — same technique used for a Collapsed
            // module window's own fixed size.
            ImGui.SetNextWindowPos(new Vector2(launcher.PositionX, launcher.PositionY), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(launcher.Width, launcher.Height), ImGuiCond.Always);
        }
        else
        {
            ImGui.SetNextWindowPos(new Vector2(launcher.PositionX, launcher.PositionY), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(launcher.Width, launcher.Height), ImGuiCond.FirstUseEver);
        }
        ImGui.SetNextWindowSizeConstraints(new Vector2(160, 64), new Vector2(float.MaxValue, float.MaxValue));

        if (ImGui.Begin("###venueos-launcher", flags))
        {
            // Scale is a separate, secondary density control (icon/font size multiplier) independent of window
            // pixel dimensions — resizing the window never changes Scale, and changing Scale never changes
            // Width/Height. SetWindowFontScale only affects this window's own text/frame sizing and resets
            // automatically next frame, so no explicit pop is needed.
            var scale = Math.Clamp(launcher.Scale, 0.5f, 2.5f);
            ImGui.SetWindowFontScale(scale);

            DrawStrip(theme, globalSettings, launcher);

            if (entries.Count == 0)
            {
                UiKit.EmptyState(theme, "No modules are shown on the launcher.", "Configure Launcher in VenueOS Settings.");
            }
            else
            {
                // Buttons Per Row stays authoritative and independent of pixel size (never auto-responsive) — if
                // the window is narrower than the buttons need, content scrolls horizontally instead of silently
                // changing the column count.
                ImGui.BeginChild("launcher-buttons", new Vector2(0, 0), false, ImGuiWindowFlags.HorizontalScrollbar);
                DrawButtons(theme, modules, windowManager, launcher, entries, scale);
                ImGui.EndChild();
            }

            if (!launcher.Locked)
            {
                var pos = ImGui.GetWindowPos();
                var size = ImGui.GetWindowSize();
                if (pos.X != launcher.PositionX || pos.Y != launcher.PositionY || size.X != launcher.Width || size.Y != launcher.Height)
                    globalSettings.SetLauncher(launcher with { PositionX = pos.X, PositionY = pos.Y, Width = size.X, Height = size.Y });
            }
        }
        ImGui.End();
        UiKit.PopWindowTheme();
    }

    /// <summary>A thin custom top strip: drag handle (the same "no native title bar, custom header is the drag
    /// region" convention as every other VenueOS window) plus a lock/edit icon button. Locked disables both native
    /// move and native resize (see <see cref="Draw"/>) and this handle's own drag; unlocked re-enables both the
    /// handle and ImGui's native resize grips — no new ImGui capability needed, the identical mechanism every other
    /// module window already uses for free resize.</summary>
    private static void DrawStrip(VenueTheme theme, GlobalSettingsService globalSettings, LauncherSettings launcher)
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.InvisibleButton("##launcher-drag", new Vector2(MathF.Max(0, width - StripHeight - 4), StripHeight));
        if (!launcher.Locked && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetMouseDragDelta(ImGuiMouseButton.Left));
            ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
        }
        ImGui.SameLine();
        if (UiKit.IconButton(theme, "launcher-lock", launcher.Locked ? "lock" : "unlock", StripHeight, launcher.Locked ? "Unlock to move/resize" : "Lock in place"))
            globalSettings.SetLauncher(launcher with { Locked = !launcher.Locked });
        ImGui.SetCursorScreenPos(start + new Vector2(0, StripHeight + 4));
    }

    private static void DrawButtons(VenueTheme theme, ModuleHost modules, ModuleWindowManager windowManager, LauncherSettings launcher, IReadOnlyList<string> entries, float scale)
    {
        // Buttons Per Row stays authoritative and independent of pixel size (never auto-responsive) — it only
        // decides when a row wraps (via LauncherLayout.ContinuesRow below), never the fixed icon-only button width.
        var buttonsPerRow = Math.Max(1, launcher.ButtonsPerRow);
        var buttonWidth = ButtonWidth * scale;

        for (var i = 0; i < entries.Count; i++)
        {
            var module = modules.Modules.FirstOrDefault(x => x.Descriptor.Id == entries[i]);
            if (module is null) continue; // stale id — silently skipped, never surfaced (§37/§45).

            if (LauncherLayout.ContinuesRow(i, buttonsPerRow)) ImGui.SameLine();
            DrawButton(theme, windowManager, module, buttonWidth, scale);
        }
    }

    /// <summary>Icon-only, always (Live QA follow-up — no more icon+name mode). Square-ish button, centered icon, no
    /// name text drawn; the full <see cref="ModuleDescriptor.DisplayName"/> is carried entirely by the hover
    /// tooltip below instead.</summary>
    private static void DrawButton(VenueTheme theme, ModuleWindowManager windowManager, IVenueModule module, float width, float scale)
    {
        ImGui.PushID($"launcher-{module.Descriptor.Id}");
        var size = new Vector2(width, ButtonHeight * scale);
        var start = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##launcher-btn", size);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        var state = windowManager.GetState(module.Descriptor.Id);
        var active = state is ModulePresentationState.Expanded or ModulePresentationState.Collapsed;
        var background = active ? theme.Tokens.Selected : hovered ? theme.Tokens.RaisedSurface : theme.Tokens.Surface;
        drawList.AddRectFilled(start, start + size, UiKit.ColorU32(background), theme.Metrics.Rounding);
        if (active || hovered) drawList.AddRect(start, start + size, UiKit.ColorU32(theme.Tokens.Border), theme.Metrics.Rounding, ImDrawFlags.None, 1f);

        var iconCenter = start + size / 2;
        AppIcons.Draw(module.Descriptor.Icon, iconCenter, 13 * scale, UiKit.ColorU32(theme.Tokens.TextPrimary));
        // The module's full display name lives here now — the button itself never draws a label — plus a state
        // hint (whether it's currently expanded/collapsed/hidden isn't otherwise shown here).
        if (hovered) ImGui.SetTooltip(module.Descriptor.DisplayName);
        ImGui.PopID();

        if (clicked) RouteClick(windowManager, module.Descriptor.Id, state);
    }

    /// <summary>Closed → Open (idempotent, applies the remembered preference if present). Hidden → Restore (returns
    /// to whichever of Expanded/Collapsed it was before being hidden). Collapsed/Expanded → RequestFocus only —
    /// deliberately never auto-expands a deliberately collapsed window.</summary>
    private static void RouteClick(ModuleWindowManager windowManager, string moduleId, ModulePresentationState? state)
    {
        switch (state)
        {
            case null: windowManager.Open(moduleId); break;
            case ModulePresentationState.Hidden: windowManager.Restore(moduleId); break;
            default: windowManager.RequestFocus(moduleId); break;
        }
    }
}
