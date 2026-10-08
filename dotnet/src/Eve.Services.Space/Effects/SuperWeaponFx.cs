namespace Eve.Services.Space.Effects;

/// <summary>
/// The graphics the superweapon families play, by race. The race is the static data's <c>raceID</c> of the module (1 Caldari, 2 Minmatar, 4 Amarr, 8 Gallente),
/// which is the key the EveJS disruptive lance table uses too. The titan doomsday graphics are the guids of the SDE's effect rows
/// (<c>superWeapon{Race}</c>, checked against <c>sde.dogmaEffects</c> by SuperWeaponFxTests). The lance names are the client's own
/// (<c>effectData</c> in <c>superWeapon.py</c>, V24.01). The reaper names are EveJS's catalog (<c>superweaponCatalog.js</c>) and are EveJS's catalog (not read from the client's effect data).
/// The Bosonic Field Generator (40633) has no graphic in the static data, the client table or EveJS (its cone FX guid is empty there), so none is given.
/// Nothing reads this table yet: the bodies (docs/project/TODO.md section 12) send these names when they are written.
/// </summary>
internal static class SuperWeaponFx
{
    private static readonly IReadOnlyDictionary<int, string> LanceByRace = new Dictionary<int, string>
    {
        [4] = "effects.SuperWeaponLanceAmarr",
        [1] = "effects.SuperWeaponLanceCaldari",
        [8] = "effects.SuperWeaponLanceGallente",
        [2] = "effects.SuperWeaponLanceMinmatar",
    };

    private static readonly IReadOnlyDictionary<int, string> ReaperByRace = new Dictionary<int, string>
    {
        [4] = "effects.SWSlashBeamAmarr",
        [1] = "effects.SWSlashBeamCaldari",
        [8] = "effects.SWSlashBeamGallente",
        [2] = "effects.SWSlashBeamMinmatar",
    };

    private static readonly IReadOnlyDictionary<string, string> DoomsdayByEffect = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["superWeaponAmarr"] = "effects.SuperWeaponAmarr",
        ["superWeaponCaldari"] = "effects.SuperWeaponCaldari",
        ["superWeaponGallente"] = "effects.SuperWeaponGallente",
        ["superWeaponMinmatar"] = "effects.SuperWeaponMinmatar",
    };

    /// <summary>The graphic of a titan lance (<c>doomsdayBeamDOT</c>) of this race, or null for a race without one.</summary>
    public static string? Lance(int raceId) => LanceByRace.GetValueOrDefault(raceId);

    /// <summary>The graphic of a disruptive lance (<c>debuffLance</c>): the racial titan lance (EveJS DISRUPTIVE_LANCE_FX_GUID_BY_RACE_ID).</summary>
    public static string? DisruptiveLance(int raceId) => LanceByRace.GetValueOrDefault(raceId);

    /// <summary>The graphic of a reaper (<c>doomsdaySlash</c>) of this race, or null.</summary>
    public static string? Reaper(int raceId) => ReaperByRace.GetValueOrDefault(raceId);

    /// <summary>The graphic of a titan doomsday, by its effect name (<c>superWeapon{Race}</c>), or null for another effect.</summary>
    public static string? Doomsday(string effectName) => DoomsdayByEffect.GetValueOrDefault(effectName);
}
