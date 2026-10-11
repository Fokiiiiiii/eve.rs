namespace Eve.Services.Space.Sites;

/// <summary>A type of the container groups (306 Spawn, 314, 1207 Scatter) as the static data names it.</summary>
public readonly record struct SiteContainerType(string Name, int TypeId, int GroupId, bool Published);

/// <summary>
/// The container types by name. The static data gives several types one name (for example unpublished containers of one group):
/// a name resolves to one type, deterministically: the published type first, then the lowest type id. Each shared name is reported once
/// through <c>warn</c>, naming the type kept and the types skipped; nothing is thrown.
/// </summary>
public static class SiteContainerTypes
{
    public static Dictionary<string, (int TypeId, int GroupId)> ByName(IEnumerable<SiteContainerType> types, Action<string>? warn = null)
    {
        var byName = new Dictionary<string, (int TypeId, int GroupId)>(StringComparer.Ordinal);
        foreach (var group in types.GroupBy(type => type.Name, StringComparer.Ordinal))
        {
            var ordered = group.OrderByDescending(type => type.Published).ThenBy(type => type.TypeId).ToList();
            var kept = ordered[0];
            byName[group.Key] = (kept.TypeId, kept.GroupId);
            if (ordered.Count > 1)
            {
                warn?.Invoke($"container type name '{group.Key}' is shared by types {string.Join(", ", ordered.Select(type => type.TypeId))}; " +
                    $"the containers use type {kept.TypeId} and skip the others");
            }
        }
        return byName;
    }
}
