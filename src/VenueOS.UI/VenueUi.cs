using VenueOS.Core;
using VenueOS.Services;
using VenueOS.Venues;

namespace VenueOS.UI;

/// <summary>Semantic styling contract. The plugin renderer maps these tokens to ImGui; modules never own brand colors.</summary>
public sealed class VenueUi(VenueProfileService venues, NotificationService notifications)
{
    public VenueTheme Theme => venues.Current.Theme;
    public string ActiveVenueLabel => venues.Current.DisplayName;
    public UiStyle Button(bool active = false) => new(active ? Theme.Tokens.Selected : Theme.Tokens.Primary, Theme.Tokens.TextPrimary, Theme.Metrics.Rounding, Theme.Metrics.Padding);
    public UiStyle Card() => new(Theme.Tokens.Surface, Theme.Tokens.TextPrimary, Theme.Metrics.Rounding, Theme.Metrics.Padding);
    public UiStyle StatusBadge(ToastLevel level) => new(level switch { ToastLevel.Success => Theme.Tokens.Success, ToastLevel.Warning => Theme.Tokens.Warning, ToastLevel.Error => Theme.Tokens.Error, _ => Theme.Tokens.Accent }, Theme.Tokens.TextPrimary, Theme.Metrics.Rounding, Theme.Metrics.Padding / 2);
    public UiStyle NavItem(bool selected) => new(selected ? Theme.Tokens.Selected : Theme.Tokens.RaisedSurface, Theme.Tokens.TextPrimary, Theme.Metrics.Rounding, Theme.Metrics.Padding);
    public void Toast(string message, ToastLevel level = ToastLevel.Information) => notifications.Push(message, level);
}
public sealed record UiStyle(string Background, string Foreground, float Rounding, float Padding);
public sealed class VenueShell(ModuleHost modules, VenueProfileService venues)
{
    private const string SettingsId = "__settings__";
    public string? SelectedModuleId { get; private set; }
    public IReadOnlyList<IVenueModule> Navigation => modules.Modules;
    public string Header => $"VenueOS · {venues.Current.DisplayName}";
    public bool IsHome => SelectedModuleId is null;
    public bool IsSettingsSelected => SelectedModuleId == SettingsId;
    public void SelectModule(string id) => SelectedModuleId = modules.Modules.Any(x => x.Descriptor.Id == id) ? id : null;
    public void SelectSettings() => SelectedModuleId = SettingsId;
    public void SelectHome() => SelectedModuleId = null;
    public void DrawSelected() { var module = modules.Modules.SingleOrDefault(x => x.Descriptor.Id == SelectedModuleId); if (module is null) return; if (!module.IsEnabled) return; module.Draw(); }
}
