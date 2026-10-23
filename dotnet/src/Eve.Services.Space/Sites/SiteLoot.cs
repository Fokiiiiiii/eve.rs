using Npgsql;

namespace Eve.Services.Space;

/// <summary>
/// (DEVIATIONS "Site loot").
/// </summary>
public static class SiteLoot
{
    public const string GenericDataProfile = "generic_data_loot";
    public const string GenericRelicProfile = "generic_relic_loot";

    /// <summary>One tag of a profile: its candidate type names, and the quantity bounds.</summary>
    public sealed record Tag(string[] Candidates, int Minimum, int Maximum);

    public static IReadOnlyList<Tag> TagsOf(string profile) => profile switch
    {
        GenericDataProfile =>
        [
            new(["Datacore - Electronic Engineering", "Datacore - Mechanical Engineering", "Datacore - Laser Physics", "Datacore - Gallentean Starship Engineering", "Datacore - Minmatar Starship Engineering"], 1, 4),
            new(["Accelerant Decryptor", "Attainment Decryptor", "Optimized Attainment Decryptor", "Process Decryptor", "Symmetry Decryptor"], 1, 1),
            new(["Esoteric Data Interface", "Occult Data Interface", "Incognito Data Interface", "Engagement Plan Data Chip"], 1, 2),
        ],
        GenericRelicProfile =>
        [
            new(["Burned Logic Circuit", "Charred Micro Circuit", "Fried Interface Circuit", "Tripped Power Circuit", "Contaminated Nanite Compound"], 2, 7),
            new(["Alloyed Tritanium Bar", "Armor Plates", "Intact Armor Plates", "Melted Nanoribbons", "Power Circuit"], 1, 3),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "not a site loot profile"),
    };

    /// <summary>The profile of a container: the relic one for a relic container, the data one otherwise.</summary>
    public static string ProfileOf(bool relic) => relic ? GenericRelicProfile : GenericDataProfile;

    /// <summary>The grants of a container: for each tag the candidate the static data has (by the seed) and its quantity, merged by type.</summary>
    public static IReadOnlyList<(int TypeId, long Quantity)> Roll(string profile, long seed, IReadOnlyDictionary<string, int> typeIdByName)
    {
        var grants = new Dictionary<int, long>();
        var tags = TagsOf(profile);
        for (var index = 0; index < tags.Count; index++)
        {
            var tag = tags[index];
            var found = tag.Candidates.Where(typeIdByName.ContainsKey).ToList();
            if (found.Count == 0)
            {
                continue;
            }
            var key = $"{seed}:{profile}:{index}";
            var typeId = typeIdByName[found[(int)(Sites.SiteSeed.Hash(key + ":type") % (uint)found.Count)]];
            var quantity = tag.Minimum + (int)(Sites.SiteSeed.Hash(key + ":quantity") % (uint)(tag.Maximum - tag.Minimum + 1));
            grants[typeId] = grants.GetValueOrDefault(typeId) + quantity;
        }
        return grants.Select(grant => (grant.Key, grant.Value)).ToList();
    }

    /// <summary>The type ids of the names the static data has (its English names), in the caller's transaction.</summary>
    public static async Task<Dictionary<string, int>> TypeIdsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, IEnumerable<string> names, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand("SELECT doc->'name'->>'en', key::int FROM sde.types WHERE doc->'name'->>'en' = ANY($1)", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = names.Distinct(StringComparer.Ordinal).ToArray() });
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.TryAdd(reader.GetString(0), reader.GetInt32(1));
        }
        return result;
    }
}
