using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using VenueOS.Modules.Operations.ShoutRunner;

namespace VenueOS.Plugin.ShoutRunner;

/// <summary>The real <see cref="IShoutRunnerAetheryteCatalog"/>: every teleport-capable Aetheryte's place name from the
/// game's own <c>Aetheryte</c> Excel sheet — the same sheet <see cref="ShoutRunnerAutomationService"/> resolves a
/// configured destination against at teleport time, so anything offered here is something the route engine can
/// actually look up. Only real Aetherytes (<c>IsAetheryte</c>) are listed, not Aethernet shards. Loaded lazily on the
/// first request (the UI thread) and retried, at most every few seconds, if the sheet was unavailable — the same
/// "never permanently give up after one failed load" rule the automation service's own data load follows.</summary>
public sealed class DalamudAetheryteCatalog(IDataManager dataManager, IPluginLog log) : IShoutRunnerAetheryteCatalog
{
    private static readonly long RetryIntervalMs = 5000;

    private IReadOnlyList<string> names = [];
    private long nextAttemptAtMs;

    public IReadOnlyList<string> Names
    {
        get
        {
            if (names.Count > 0) return names;
            var now = Environment.TickCount64;
            if (now < nextAttemptAtMs) return names;
            nextAttemptAtMs = now + RetryIntervalMs;
            names = Load();
            return names;
        }
    }

    private IReadOnlyList<string> Load()
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<Aetheryte>();
            if (sheet == null) return [];
            var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in sheet)
            {
                if (!row.IsAetheryte) continue;
                var name = row.PlaceName.ValueNullable?.Name.ExtractText().Trim();
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
            }

            return [.. result];
        }
        catch (Exception ex)
        {
            log.Debug(ex, "ShoutRunner: failed to load the Aetheryte name list; will retry.");
            return [];
        }
    }
}
