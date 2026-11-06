namespace Eve.FactionWarfare;

/// <summary>
/// The factions, militia corporations and combat relations of factional warfare. Ids are the SDE factions table (build 3396210) and the
/// militia corporations are its <c>militiaCorporationID</c> values (client extract <c>compat/factions.jsonl.gz</c>). The enemy pairs are the
/// V24.01 client's <c>eve/common/script/util/facwarCommon.py</c> (<c>_OCCUPATION_ENEMY_BY_FACTION_ID</c>, <c>_INSURGENCY_ENEMIES_BY_FACTION_ID</c>).
/// </summary>
public static class FwFactions
{
    public const long CaldariState = 500001;
    public const long MinmatarRepublic = 500002;
    public const long AmarrEmpire = 500003;
    public const long GallenteFederation = 500004;
    public const long GuristasPirates = 500010;
    public const long AngelCartel = 500011;

    /// <summary>The four occupier factions, in the SDE order.</summary>
    public static IReadOnlyList<long> Occupiers { get; } = [CaldariState, MinmatarRepublic, AmarrEmpire, GallenteFederation];

    /// <summary>The two sides' fighting pairs: the occupation pairs (Amarr-Minmatar, Caldari-Gallente) and the insurgency pairs (Angel and Guristas against the occupiers).</summary>
    private static readonly (long A, long B)[] EnemyPairs =
    [
        (AmarrEmpire, MinmatarRepublic),
        (CaldariState, GallenteFederation),
        (AngelCartel, AmarrEmpire),
        (AngelCartel, MinmatarRepublic),
        (GuristasPirates, CaldariState),
        (GuristasPirates, GallenteFederation),
    ];

    /// <summary>The militia corporation of a faction (SDE <c>militiaCorporationID</c>), or 0 for a faction with none.</summary>
    public static long MilitiaCorporationOf(long factionId) => factionId switch
    {
        CaldariState => 1000180,
        MinmatarRepublic => 1000182,
        AmarrEmpire => 1000179,
        GallenteFederation => 1000181,
        GuristasPirates => 1000437,
        AngelCartel => 1000436,
        _ => 0,
    };

    public static bool IsOccupier(long factionId) => Occupiers.Contains(factionId);

    public static bool IsPirate(long factionId) => factionId is GuristasPirates or AngelCartel;

    public static bool IsWarfareFaction(long factionId) => IsOccupier(factionId) || IsPirate(factionId);

    /// <summary>The faction that fights this occupier over its systems (Amarr and Minmatar, Caldari and Gallente), or 0.</summary>
    public static long OccupationEnemyOf(long factionId) => factionId switch
    {
        AmarrEmpire => MinmatarRepublic,
        MinmatarRepublic => AmarrEmpire,
        CaldariState => GallenteFederation,
        GallenteFederation => CaldariState,
        _ => 0,
    };

    /// <summary>Whether two factions fight in combat (the client's <c>IsCombatEnemyFaction</c>). The relation is symmetric.</summary>
    public static bool IsCombatEnemy(long a, long b) =>
        a != 0 && b != 0 && a != b && EnemyPairs.Any(pair => (pair.A == a && pair.B == b) || (pair.A == b && pair.B == a));
}
