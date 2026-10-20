using System.Text.Json;
using Eve.Data;
using Eve.Dogma;
using Eve.Services.Space.Effects;
using Eve.Sde;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The superweapon graphics (<see cref="SuperWeaponFx"/>): the titan doomsday guids are the SDE's effect rows, and each lance, reaper and disruptive lance graphic
/// catalog (superWeaponCatalog.js).
/// </summary>
public class SuperWeaponFxTests
{
    private static readonly (int Effect, string Name, string Guid)[] Doomsdays =
    [
        (4489, "superWeaponAmarr", "effects.SuperWeaponAmarr"),
        (4490, "superWeaponCaldari", "effects.SuperWeaponCaldari"),
        (4491, "superWeaponGallente", "effects.SuperWeaponGallente"),
        (4492, "superWeaponMinmatar", "effects.SuperWeaponMinmatar"),
    ];

    /// <summary>Titan lances (40631, 41439-41441), reapers (40632, 41442-41444) and disruptive lances (77398-77401), with the raceID the static data gives each.</summary>
    private static readonly (int Type, int Race)[] Lances = [(40631, 4), (41439, 1), (41440, 8), (41441, 2), (77398, 2), (77399, 4), (77400, 1), (77401, 8)];
    private static readonly (int Type, int Race)[] Reapers = [(40632, 4), (41442, 1), (41443, 8), (41444, 2)];

    [PgFact]
    public async Task The_titan_doomsday_graphics_are_the_guids_of_the_sde_effect_rows()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var rows = await new SdeReader(source).ReadAsync("dogmaEffects", Doomsdays.Select(d => (long)d.Effect));

        foreach (var (effect, name, guid) in Doomsdays)
        {
            var row = rows.Single(r => r.Key == effect);
            Assert.Equal(name, row.Doc.GetProperty("name").GetString());
            Assert.Equal(guid, row.Doc.GetProperty("guid").GetString());
            Assert.Equal(guid, SuperWeaponFx.Doomsday(name));
        }
        Assert.Null(SuperWeaponFx.Doomsday("doomsdayConeDOT"));
    }

    [PgFact]
    public async Task Each_lance_disruptive_lance_and_reaper_takes_the_graphic_of_its_race()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        var types = await sde.ReadAsync("types", Lances.Concat(Reapers).Select(t => (long)t.Type));

        foreach (var (type, race) in Lances.Concat(Reapers))
        {
            var row = types.Single(t => t.Key == type);
            Assert.Equal(race, row.Doc.GetProperty("raceID").GetInt32());
        }

        Assert.Equal("effects.SuperWeaponLanceAmarr", SuperWeaponFx.Lance(4));
        Assert.Equal("effects.SuperWeaponLanceCaldari", SuperWeaponFx.Lance(1));
        Assert.Equal("effects.SuperWeaponLanceGallente", SuperWeaponFx.Lance(8));
        Assert.Equal("effects.SuperWeaponLanceMinmatar", SuperWeaponFx.Lance(2));
        foreach (var (_, race) in Lances)
        {
            Assert.Equal(SuperWeaponFx.Lance(race), SuperWeaponFx.DisruptiveLance(race));
        }

        Assert.Equal("effects.SWSlashBeamAmarr", SuperWeaponFx.Reaper(4));
        Assert.Equal("effects.SWSlashBeamCaldari", SuperWeaponFx.Reaper(1));
        Assert.Equal("effects.SWSlashBeamGallente", SuperWeaponFx.Reaper(8));
        Assert.Equal("effects.SWSlashBeamMinmatar", SuperWeaponFx.Reaper(2));
        Assert.Null(SuperWeaponFx.Lance(0));
    }
}
