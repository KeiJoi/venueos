using VenueOS.Core;

namespace VenueOS.Core.Tests;

public sealed class ModuleHostTests
{
    [Fact]
    public async Task Orders_dependencies_and_isolates_tick_failure()
    {
        var log = new List<string>(); var host = new ModuleHost(); var baseModule = new TestModule("base", log); var dependent = new TestModule("dependent", log, ["base"]); var broken = new TestModule("broken", log) { ThrowOnTick = true };
        host.Register(dependent); host.Register(broken); host.Register(baseModule); await host.InitializeAsync(); host.Tick(DateTimeOffset.UtcNow);
        Assert.True(log.IndexOf("init:base") < log.IndexOf("init:dependent")); Assert.Contains("tick:base", log); Assert.Contains("tick:dependent", log);
    }
    [Fact]
    public async Task Disabled_module_does_not_receive_lifecycle()
    {
        var log = new List<string>(); var host = new ModuleHost(); var module = new TestModule("off", log) { IsEnabled = false }; host.Register(module); await host.InitializeAsync(); Assert.Empty(log);
    }
    [Fact]
    public void Modules_are_ordered_by_id_regardless_of_registration_order()
    {
        // Home and Settings → Modules both iterate ModuleHost.Modules directly (never ModuleHost's separate
        // dependency-ordered Ordered()), so this single ordering must be deterministic and independent of the order
        // modules.Register(...) happened to be called in Plugin.cs, or the two surfaces could silently diverge.
        var log = new List<string>(); var host = new ModuleHost();
        host.Register(new TestModule("games.tournament", log)); host.Register(new TestModule("core.attendance", log)); host.Register(new TestModule("games.bingo", log));
        Assert.Equal(["core.attendance", "games.bingo", "games.tournament"], host.Modules.Select(x => x.Descriptor.Id));
    }
    [Fact]
    public void DisplayOrder_wins_over_alphabetical_id_when_set()
    {
        // ModuleDescriptor.DisplayOrder is the one authoritative place to change display order later (see its doc
        // comment) — proves a lower DisplayOrder sorts first even against an alphabetically-earlier Id, and that
        // leaving it at the default 0 for every module (today's actual state) falls back to alphabetical-by-Id.
        var log = new List<string>(); var host = new ModuleHost();
        host.Register(new TestModule("zeta", log, displayOrder: 1)); host.Register(new TestModule("alpha", log, displayOrder: 2)); host.Register(new TestModule("mid", log));
        Assert.Equal(["mid", "zeta", "alpha"], host.Modules.Select(x => x.Descriptor.Id));
    }
    internal sealed class TestModule(string id, List<string> log, IReadOnlyList<string>? dependencies = null, int displayOrder = 0) : IVenueModule
    {
        public ModuleDescriptor Descriptor { get; } = new(id, id, "test", "*", dependencies, DisplayOrder: displayOrder); public bool IsEnabled { get; set; } = true; public bool ThrowOnTick { get; init; }
        public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken) { log.Add($"init:{Descriptor.Id}"); return Task.CompletedTask; }
        public Task OnVenueChangedAsync(VenueContext context, CancellationToken cancellationToken) { log.Add($"venue:{Descriptor.Id}:{context.DisplayName}"); return Task.CompletedTask; }
        public void Tick(DateTimeOffset now) { log.Add($"tick:{Descriptor.Id}"); if (ThrowOnTick) throw new InvalidOperationException(); } public void Draw() { } public void DrawSettings() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
