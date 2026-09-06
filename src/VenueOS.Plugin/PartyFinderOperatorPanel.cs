using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueOS.Modules.Operations.PartyFinder;
using VenueOS.Plugin.Shell;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.Plugin;

/// <summary>Party Finder's operational (<see cref="Draw"/>) and persistent-configuration (<see cref="DrawSettings"/>)
/// UI. Rebuilds the donor's full recruitment-criteria workflow (venuepartyfinder, UI/MainWindow.cs) using VenueOS's
/// component library, with two deliberate differences from the donor: no Block Letter Generator (extracted from
/// Party Finder's scope — a future dedicated module), and a new End Party Finder action alongside Create/Refresh/Abort.</summary>
internal sealed class PartyFinderOperatorPanel(PartyFinderService service, PartyFinderDutyCatalog duties, VenueProfileService venues)
{
    private string dutySearch = string.Empty;

    public void Draw()
    {
        var theme = venues.Current.Theme;
        var preset = service.Settings.Preset;

        DrawActionArea(theme);
        ImGui.Spacing();

        UiKit.BeginSectionCard("partyfinder-details", theme, "Details");
        DrawDetailsSection(theme, preset);
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("partyfinder-search-area", theme, "Search Area");
        DrawSearchAreaSection(theme, preset);
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("partyfinder-conditions", theme, "Conditions");
        DrawConditionsSection(theme, preset);
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("partyfinder-df-settings", theme, "Duty Finder Settings");
        DrawDutyFinderSettingsSection(theme, preset);
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("partyfinder-loot-rule", theme, "Loot Rule");
        DrawLootRuleSection(theme, preset);
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("partyfinder-languages", theme, "Languages");
        DrawLanguagesSection(theme, preset);
        UiKit.EndSectionCard();

        ImGui.Spacing();
        UiKit.BeginSectionCard("partyfinder-roles", theme, "Roles");
        DrawRolesSection(theme, preset);
        UiKit.EndSectionCard();
    }

    public void DrawSettings()
    {
        var theme = venues.Current.Theme;
        UiKit.BeginSectionCard("partyfinder-automation-settings", theme, "Automation");

        var autoRefresh = service.Settings.AutoRefreshEnabled;
        if (UiKit.Toggle(theme, "Auto Refresh on native 5 minute warning", ref autoRefresh))
        {
            service.SetAutoRefreshEnabled(autoRefresh);
        }

        var warningOverride = service.Settings.WarningMessageOverride;
        if (Forms.TextField(theme, "Warning Message Override", ref warningOverride, 256))
        {
            service.SetWarningMessageOverride(warningOverride);
        }

        UiKit.EndSectionCard();
    }

    private void DrawActionArea(VenueTheme theme)
    {
        UiKit.BeginSectionCard("partyfinder-status", theme, "Party Finder");

        UiKit.ConnectionBadge(theme, service.Status, service.HasOwnListing);
        ImGui.Spacing();
        UiKit.StatusBadge(theme, service.HasOwnListing ? "Listing active" : "No listing", service.HasOwnListing ? ToastLevel.Success : ToastLevel.Information);
        ImGui.SameLine();
        UiKit.StatusBadge(theme, service.IsCompatibilityVerified ? "Compatibility verified" : "Compatibility pending", service.IsCompatibilityVerified ? ToastLevel.Success : ToastLevel.Warning);
        if (service.IsEnding)
        {
            ImGui.SameLine();
            UiKit.StatusBadge(theme, "Ending…", ToastLevel.Warning);
        }

        ImGui.Spacing();

        var actionsDisabled = service.IsEnding;
        ImGui.BeginDisabled(actionsDisabled);
        if (UiKit.PrimaryButton(theme, service.HasOwnListing ? "Edit / Apply Changes" : "Recruit Members")) service.CreateOrUpdate("operator panel");
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Refresh Active Listing")) service.Refresh("operator panel");
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (UiKit.GhostButton(theme, "Abort")) service.Abort();

        ImGui.SameLine();
        // End Party Finder is a deliberate shutdown, not a routine control — visually distinguished as the danger
        // action, but still built from VenueOS's semantic theme tokens like every other button here.
        ImGui.BeginDisabled(service.IsEnding || (!service.HasOwnListing && !service.Settings.AutoRefreshEnabled));
        if (UiKit.DangerButton(theme, "End Party Finder")) service.EndPartyFinder("operator panel");
        ImGui.EndDisabled();
        UiKit.Tooltip("Disables Auto Refresh, withdraws the active listing, and leaves Auto Refresh off until you explicitly re-enable it in Settings.");

        UiKit.EndSectionCard();
    }

    private void DrawDetailsSection(VenueTheme theme, PartyFinderPreset preset)
    {
        DrawCategorySelector(theme, preset);
        DrawDutySelector(theme, preset);
        DrawObjectiveSelector(theme, preset);

        var beginner = preset.BeginnerFriendly;
        if (UiKit.Toggle(theme, "Beginner friendly", ref beginner))
        {
            service.UpdatePreset(preset with { BeginnerFriendly = beginner });
        }

        var comment = preset.Comment;
        if (Forms.MultilineField(theme, "Comment", ref comment, 512, 120))
        {
            service.UpdatePreset(preset with { Comment = comment });
        }

        ImGui.TextDisabled($"{PartyFinderPreset.CommentByteCount(preset.Comment)}/192 bytes");
    }

    private void DrawSearchAreaSection(VenueTheme theme, PartyFinderPreset preset)
    {
        var worldOnly = preset.LimitRecruitingToWorld;
        if (UiKit.Toggle(theme, "Limit recruiting to world server", ref worldOnly))
        {
            service.UpdatePreset(preset with { LimitRecruitingToWorld = worldOnly });
        }

        var privateParty = preset.PrivateParty;
        if (UiKit.Toggle(theme, "Form a private party", ref privateParty))
        {
            service.UpdatePreset(preset with { PrivateParty = privateParty, Password = privateParty ? preset.Password : (ushort)0 });
        }

        ImGui.BeginDisabled(!preset.PrivateParty);
        var password = (int)preset.Password;
        if (Forms.NumericField(theme, "Party password", ref password, 1, 0, 9999))
        {
            service.UpdatePreset(preset with { Password = PartyFinderPreset.ClampPassword(password) });
        }
        ImGui.EndDisabled();
    }

    private void DrawConditionsSection(VenueTheme theme, PartyFinderPreset preset)
    {
        var completionEnabled = preset.CompletionStatus != PartyFinderCompletionStatus.None;
        if (UiKit.Toggle(theme, "Completion status", ref completionEnabled))
        {
            service.UpdatePreset(preset with { CompletionStatus = completionEnabled ? PartyFinderCompletionStatus.DutyComplete : PartyFinderCompletionStatus.None });
        }

        ImGui.BeginDisabled(!completionEnabled);
        var statusOptions = new[] { PartyFinderCompletionStatus.DutyComplete, PartyFinderCompletionStatus.DutyIncomplete, PartyFinderCompletionStatus.DutyCompleteWeeklyUnclaimed };
        var statusNames = statusOptions.Select(PartyFinderPreset.GetCompletionStatusName).ToArray();
        var currentStatus = preset.CompletionStatus == PartyFinderCompletionStatus.None ? PartyFinderCompletionStatus.DutyComplete : preset.CompletionStatus;
        var statusIndex = Array.IndexOf(statusOptions, currentStatus);
        if (statusIndex < 0) statusIndex = 0;
        if (Forms.ComboField(theme, "Completion status value", statusNames, ref statusIndex))
        {
            service.UpdatePreset(preset with { CompletionStatus = statusOptions[statusIndex] });
        }
        ImGui.EndDisabled();

        var avgIlEnabled = preset.AverageItemLevelEnabled;
        if (UiKit.Toggle(theme, "Avg. item level", ref avgIlEnabled))
        {
            service.UpdatePreset(preset with { AverageItemLevelEnabled = avgIlEnabled, AverageItemLevel = avgIlEnabled && preset.AverageItemLevel == 0 ? (ushort)1 : preset.AverageItemLevel });
        }

        ImGui.BeginDisabled(!preset.AverageItemLevelEnabled);
        var avgIl = (int)preset.AverageItemLevel;
        if (Forms.NumericField(theme, "Avg. item level value", ref avgIl, 1, 1, 999))
        {
            service.UpdatePreset(preset with { AverageItemLevel = PartyFinderPreset.ClampAverageItemLevel(avgIl) });
        }
        ImGui.EndDisabled();
    }

    private void DrawDutyFinderSettingsSection(VenueTheme theme, PartyFinderPreset preset)
    {
        DrawFlagToggle(theme, "Unrestricted party", PartyFinderDutyFinderSetting.UnrestrictedParty, preset);
        DrawFlagToggle(theme, "Minimum item level", PartyFinderDutyFinderSetting.MinimumIL, preset);
        DrawFlagToggle(theme, "Silence echo", PartyFinderDutyFinderSetting.SilenceEcho, preset);
    }

    private void DrawFlagToggle(VenueTheme theme, string label, PartyFinderDutyFinderSetting flag, PartyFinderPreset preset)
    {
        var enabled = (preset.DutyFinderSettings & flag) == flag;
        if (UiKit.Toggle(theme, label, ref enabled))
        {
            var next = enabled ? preset.DutyFinderSettings | flag : preset.DutyFinderSettings & ~flag;
            service.UpdatePreset(preset with { DutyFinderSettings = next });
        }
    }

    private void DrawLootRuleSection(VenueTheme theme, PartyFinderPreset preset)
    {
        var options = new[] { PartyFinderLootRule.Normal, PartyFinderLootRule.GreedOnly, PartyFinderLootRule.Lootmaster };
        var names = options.Select(PartyFinderPreset.GetLootRuleName).ToArray();
        var index = Array.IndexOf(options, preset.LootRule);
        if (index < 0) index = 0;
        if (Forms.ComboField(theme, "Loot rule", names, ref index))
        {
            service.UpdatePreset(preset with { LootRule = options[index] });
        }
    }

    private void DrawLanguagesSection(VenueTheme theme, PartyFinderPreset preset)
    {
        DrawLanguageToggle(theme, "JP", PartyFinderLanguage.Japanese, preset);
        ImGui.SameLine();
        DrawLanguageToggle(theme, "EN", PartyFinderLanguage.English, preset);
        ImGui.SameLine();
        DrawLanguageToggle(theme, "DE", PartyFinderLanguage.German, preset);
        ImGui.SameLine();
        DrawLanguageToggle(theme, "FR", PartyFinderLanguage.French, preset);
    }

    private void DrawLanguageToggle(VenueTheme theme, string label, PartyFinderLanguage flag, PartyFinderPreset preset)
    {
        var enabled = (preset.Languages & flag) == flag;
        if (UiKit.Toggle(theme, label, ref enabled))
        {
            var next = enabled ? preset.Languages | flag : preset.Languages & ~flag;
            service.UpdatePreset(preset with { Languages = next });
        }
    }

    private void DrawRolesSection(VenueTheme theme, PartyFinderPreset preset)
    {
        Forms.PushFieldStyle(theme);
        var mainSlots = (int)preset.NumberOfSlotsInMainParty;
        Forms.FieldLabel(theme, "Slots in main party");
        if (ImGui.SliderInt("##pf-slots", ref mainSlots, 1, 8))
        {
            service.UpdatePreset(preset with { NumberOfSlotsInMainParty = (byte)mainSlots });
        }

        var groups = (int)preset.NumberOfGroups;
        Forms.FieldLabel(theme, "Number of groups");
        if (ImGui.SliderInt("##pf-groups", ref groups, 1, 6))
        {
            service.UpdatePreset(preset with { NumberOfGroups = (byte)groups });
        }
        Forms.PopFieldStyle();

        var onePerJob = preset.OnePlayerPerJob;
        if (UiKit.Toggle(theme, "One player per job", ref onePerJob))
        {
            service.UpdatePreset(preset with { OnePlayerPerJob = onePerJob });
        }

        ImGui.TextWrapped("The native screen stores role restrictions as per-slot job masks. Expand a slot below to match the role icons you want.");
        ImGui.TextWrapped($"Effective slots: {preset.EffectiveSlotCount}");

        if (UiKit.GhostButton(theme, "Set all slots to any job"))
        {
            service.UpdatePreset(preset.WithAllSlots(PartyFinderSlot.AnyJob));
        }

        for (var i = 0; i < preset.EffectiveSlotCount; i++)
        {
            var slot = preset.GetSlot(i);
            if (!ImGui.TreeNode($"Slot {i + 1}: {PartyFinderSlot.Describe(slot)}###pf-slot-{i}"))
            {
                continue;
            }

            DrawSlotQuickMasks(theme, preset, i);
            UiKit.Divider(theme);

            foreach (var job in PartyFinderJobCatalog.Jobs)
            {
                var enabled = slot.Allows(job.Job);
                if (UiKit.Toggle(theme, $"{job.Abbreviation} - {job.Name}###pf-slot-{i}-{job.Abbreviation}", ref enabled))
                {
                    var jobs = slot.Jobs.ToList();
                    if (enabled) { if (!jobs.Contains(job.Job)) jobs.Add(job.Job); }
                    else jobs.Remove(job.Job);
                    service.UpdatePreset(preset.WithSlot(i, new PartyFinderSlot(jobs)));
                }
            }

            ImGui.TreePop();
        }
    }

    private void DrawSlotQuickMasks(VenueTheme theme, PartyFinderPreset preset, int index)
    {
        var buttonSize = new Vector2(96, 0);
        if (UiKit.GhostButton(theme, $"All###pf-all-{index}", buttonSize)) service.UpdatePreset(preset.WithSlot(index, PartyFinderSlot.AnyJob));
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, $"Tank###pf-tank-{index}", buttonSize)) service.UpdatePreset(preset.WithSlot(index, new PartyFinderSlot(PartyFinderJobCatalog.JobsInRole(PartyFinderRole.Tank))));
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, $"Heal###pf-heal-{index}", buttonSize)) service.UpdatePreset(preset.WithSlot(index, new PartyFinderSlot(PartyFinderJobCatalog.JobsInRole(PartyFinderRole.Healer))));
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, $"Melee###pf-melee-{index}", buttonSize)) service.UpdatePreset(preset.WithSlot(index, new PartyFinderSlot(PartyFinderJobCatalog.JobsInRole(PartyFinderRole.Melee))));
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, $"Phys Ranged###pf-pranged-{index}", buttonSize)) service.UpdatePreset(preset.WithSlot(index, new PartyFinderSlot(PartyFinderJobCatalog.JobsInRole(PartyFinderRole.PhysicalRanged))));
        ImGui.SameLine();
        if (UiKit.GhostButton(theme, $"Caster###pf-caster-{index}", buttonSize)) service.UpdatePreset(preset.WithSlot(index, new PartyFinderSlot(PartyFinderJobCatalog.JobsInRole(PartyFinderRole.MagicalRanged))));
    }

    private void DrawCategorySelector(VenueTheme theme, PartyFinderPreset preset)
    {
        var options = Enum.GetValues<PartyFinderCategory>();
        var names = options.Select(PartyFinderPreset.GetCategoryName).ToArray();
        var index = Array.IndexOf(options, preset.Category);
        if (index < 0) index = 0;
        if (Forms.ComboField(theme, "Category", names, ref index))
        {
            service.UpdatePreset(preset with { Category = options[index] });
        }
    }

    private void DrawObjectiveSelector(VenueTheme theme, PartyFinderPreset preset)
    {
        var options = new[] { PartyFinderObjective.None, PartyFinderObjective.DutyCompletion, PartyFinderObjective.Practice, PartyFinderObjective.Loot };
        var names = options.Select(PartyFinderPreset.GetObjectiveName).ToArray();
        var index = Array.IndexOf(options, preset.Objective);
        if (index < 0) index = 0;
        if (Forms.ComboField(theme, "Objective", names, ref index))
        {
            service.UpdatePreset(preset with { Objective = options[index] });
        }
    }

    private void DrawDutySelector(VenueTheme theme, PartyFinderPreset preset)
    {
        Forms.SearchBox(theme, "pf-duty-search", ref dutySearch, "Filter duties");

        var filtered = duties.Filter(dutySearch).ToArray();
        var names = filtered.Select(d => d.Name).ToArray();
        var index = Array.FindIndex(filtered, d => d.Id == preset.DutyId);
        var preview = duties.GetName(preset.DutyId);

        Forms.FieldLabel(theme, "Duty");
        ImGui.SetNextItemWidth(-1);
        Forms.PushFieldStyle(theme);
        var open = ImGui.BeginCombo("##pf-duty", preview);
        Forms.PopFieldStyle();
        if (!open) return;

        for (var i = 0; i < filtered.Length; i++)
        {
            var duty = filtered[i];
            var selected = i == index;
            if (ImGui.Selectable($"{duty.Name}##pf-duty-{duty.Id}", selected))
            {
                service.UpdatePreset(preset with { DutyId = duty.Id });
            }

            if (selected) ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }
}
