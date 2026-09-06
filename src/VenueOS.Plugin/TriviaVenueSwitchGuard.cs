using VenueOS.Modules.Operations.Trivia;
using VenueOS.Plugin.Shell;

namespace VenueOS.Plugin;

/// <summary>Decision: leaving a venue with an active Trivia game must end that Game (never silently), but must never
/// end the Series it belongs to — the Series stays resumable until the host explicitly chooses End Series. Lives in
/// VenueOS.Plugin (not VenueOS.Modules.Operations) because <see cref="IVenueSwitchGuard"/> is shell/UI-layer.</summary>
internal sealed class TriviaVenueSwitchGuard(MairsTriviaService trivia) : IVenueSwitchGuard
{
    public string? DescribeRisk() => trivia.CurrentGame is { State: not "finished" } game
        ? $"Mair's Trivia has an active game (\"{game.GameName}\") running in this venue. Switching venues will end this game for all connected players. This will not end the Series it may belong to."
        : null;
    public async Task ResolveAsync() { if (trivia.CurrentGame is { State: not "finished" }) await trivia.EndGameAsync().ConfigureAwait(false); }
}
