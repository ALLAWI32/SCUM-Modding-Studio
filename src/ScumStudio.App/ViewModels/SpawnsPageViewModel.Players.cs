using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Localization;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Players: where they spawn. No <c>ServerSettings.ini</c> key chooses where a NEW character first appears, and a mod pak
/// cannot carry "the first time at place X, then as usual" (that is server logic). What the server does let an admin set are
/// the respawn rules, shown here for the keys its file has (with or without the <c>scum.</c> prefix).
/// </summary>
public sealed partial class SpawnsPageViewModel
{
    private static readonly string[] RespawnKinds = ["Random", "Sector", "Shelter", "Squad"];

    /// <summary>The first-spawn answer and the respawn rules.</summary>
    [ObservableProperty]
    private IReadOnlyList<SpawnCard> _playerCards = [];

    private IReadOnlyList<SpawnCard> BuildPlayerCards()
    {
        var cards = new List<SpawnCard>
        {
            new(Loc.T("Spawns.Players.FirstSpawn.Title"), string.Empty, "Icon.Pin", [new SpawnNoteViewModel(Loc.T("Spawns.Players.FirstSpawn.Body"), isWarning: false)]),
        };
        if (_server is not { } server)
        {
            return cards;
        }

        var rows = new List<object?>
        {
            ServerRowIfPresent("AllowSectorRespawn", "Spawns.S.AllowSector", toggle: true),
            ServerRowIfPresent("AllowShelterRespawn", "Spawns.S.AllowShelter", toggle: true),
            ServerRowIfPresent("AllowSquadmateRespawn", "Spawns.S.AllowSquad", toggle: true),
        };
        foreach (var (suffix, label, max, step, times) in new[] { ("Price", "Price", 1000.0, 1.0, false), ("InitialTime", "Initial", 3600.0, 1.0, false), ("CooldownTime", "Cooldown", 3600.0, 1.0, false), ("CooldownResetMultiplier", "Reset", 5.0, 0.05, true) })
        {
            foreach (var kind in RespawnKinds)
            {
                // RandomLocationPrice, SectorLocationCooldownTime...: the first key of the file that names the kind and the value.
                var key = server.Values.Keys.FirstOrDefault(k => (k.StartsWith("scum.", StringComparison.OrdinalIgnoreCase) ? k[5..] : k) is var name
                    && name.StartsWith(kind, StringComparison.OrdinalIgnoreCase) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && (suffix != "CooldownTime" || !name.EndsWith("ResetMultiplier", StringComparison.OrdinalIgnoreCase)));
                if (key is not null)
                {
                    var integer = !times && !server.Values[key].Contains('.', StringComparison.Ordinal);
                    rows.Add(ServerRow(key, $"Spawns.S.{kind}{label}", integer: integer, max: max, step: step, unit: times ? Loc.T("Spawns.Unit.Times") : string.Empty));
                }
            }
        }

        var shown = rows.OfType<object>().ToList();
        if (shown.Count > 0)
        {
            cards.Add(new SpawnCard(Loc.T("Spawns.Players.Respawn"), Loc.T("Spawns.Players.Respawn.Caption"), "Icon.History", shown));
        }

        return cards;
    }
}
