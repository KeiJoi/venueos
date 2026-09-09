# Adding a VenueOS module

> **See `NEW_MODULE_GUIDE.md` for the authoritative, comprehensive guide.** It covers everything this short note
> doesn't — Home integration, embedded and detached rendering, the shared `ModuleWindowHeader`, Settings → Modules
> contribution, per-venue vs. global configuration, the Auto Pop-Out preference, theming, the UI component kit,
> diagnostics, backend-module conventions, a worked example module, and a Definition of Done checklist. This file
> predates that architecture and is kept only as a short historical quick-reference for the registration mechanics
> below, which are still accurate. Where the two disagree, `NEW_MODULE_GUIDE.md` is correct — in particular,
> `VenueHttpClientFactory` (mentioned below) currently has no callers anywhere in the codebase; backend modules
> construct `HttpClient` directly instead.

Do not modify `VenueOS.Core` to add module settings. Implement `IVenueModule`, register it in the plugin composition root, and give it a unique stable ID such as `core.greeter` or `games.trivia`. IDs are persistence contracts and must not be renamed casually.

```csharp
public sealed class ExampleModule(VenueProfileService profiles) : IVenueModule {
  public ModuleDescriptor Descriptor { get; } = new("example.status", "Example", "Example module", "*");
  public bool IsEnabled { get; set; } = true;
  public Task InitializeAsync(ModuleContext c, CancellationToken ct) => Task.CompletedTask;
  public Task OnVenueChangedAsync(VenueContext c, CancellationToken ct) {
    var config = profiles.GetModuleConfig(c.VenueId, Descriptor.Id, 1, () => new ExampleSettings());
    return Task.CompletedTask;
  }
  public void Tick(DateTimeOffset now) { } public void Draw() { } public void DrawSettings() { }
  public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

The module owns its DTO, defaulting, validation and schema migration. Use `PresenceService`, `ChatCommandService`, and `SchedulerService` rather than creating competing queues/scanners (`NotificationService` still exists but has no renderer — see `NEW_MODULE_GUIDE.md` §21/§36 for operator-visible feedback instead). Backend modules should add typed protocol clients next to their module, never a generic backend abstraction — construct `HttpClient` directly, matching every current backend module; `VenueHttpClientFactory` has no callers anywhere in the codebase (see the note above).
