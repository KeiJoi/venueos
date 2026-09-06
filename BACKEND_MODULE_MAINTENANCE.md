# Backend module maintenance

The standalone plugin is the canonical reference implementation. VenueOS is a compatible client, never the backend authority.

```text
Backend changes → Update standalone plugin FIRST → Test standalone plugin → Record new known-good version/commit → Compare VenueOS module to updated standalone client → Update VenueOS typed protocol client → Run compatibility tests → Test VenueOS
```

| Module ID | Standalone repository / pin | Protocol client | Credentials/settings | Contract notes |
|---|---|---|---|---|
| `games.raffle` | `C:\FFXIVplugs\ffxivraffle4all` @ `111b3a9` | `src/VenueOS.Modules.Operations/Raffle/VenueRaffleClient.cs` | `VenueRaffleSettings` | `VENUE_RAFFLE_3A.md` |
| `games.trivia` | `C:\FFXIVplugs\mairstrivia` @ `6ba4855` | `src/VenueOS.Modules.Operations/Trivia/MairsTriviaClient.cs` | `MairsTriviaSettings` | `MAIRS_TRIVIA_3B.md` |
| `games.tournament` | `C:\FFXIVplugs\tournamentcontrol` @ `c29e984` / 0.1.6 | `src/VenueOS.Modules.Operations/Tournament/TournamentControlClient.cs` | `TournamentModuleSettings` | `TOURNAMENT_CONTROL_3C.md` |
| `games.bingo` | `C:\FFXIVplugs\ffxivbingo4all` @ `015d5d6` / 1.0.0.7 | `src/VenueOS.Modules.Operations/Bingo/VenueBingoClient.cs` | `VenueBingoSettings` | `VENUE_BINGO_3D.md` |

All settings are venue-scoped through `VenueProfileService`. Treat tokens, keys, and tokenized browser URLs as secrets; redact them from logs and diagnostics.
