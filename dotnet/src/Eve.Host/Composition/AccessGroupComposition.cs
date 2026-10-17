using Eve.AccessGroups;
using Eve.Bookmarks;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private AccessGroupStore accessGroups = default!;

    /// <summary>The owner groups (schema <c>access_groups</c>, or <c>EVE_ACCESS_GROUPS_SCHEMA</c>).</summary>
    private async Task AccessGroupStores()
    {
        var schema = Env("EVE_ACCESS_GROUPS_SCHEMA", AccessGroupStore.DefaultSchema);
        await AccessGroupStore.MigrateAsync(source, schema);
        accessGroups = new AccessGroupStore(source, schema, now);
    }

    /// <summary>The corporation and alliance (0 for none) each character is in now, from the character store.</summary>
    private async Task<IReadOnlyDictionary<long, (long CorporationId, long AllianceId)>> AffiliationsOf(IReadOnlyList<long> characterIds, CancellationToken token)
    {
        var found = new Dictionary<long, (long CorporationId, long AllianceId)>();
        foreach (var character in await characters.GetManyAsync(characterIds, token))
            found[character.Id] = (character.CorporationId, character.AllianceId ?? 0);
        return found;
    }

    /// <summary>The access lists the character holds a role in, for the shared bookmark folders.</summary>
    private async ValueTask<AccessLists> MemberLists(Affiliation who, CancellationToken token) =>
        new(await accessGroups.MemberRolesAsync(who, token), who.CorporationId);

    /// <summary>
    /// What a member id of an access group is: everyone (id 0), a corporation, an alliance or a character. Null when the id is none of them.
    /// </summary>
    private async ValueTask<MemberKind?> OwnerKind(long id)
    {
        if (id == 0) return MemberKind.Everyone;
        if (id < 0) return null;
        if (await corporations.GetAsync(id) is { Deleted: false }) return MemberKind.Corporation;
        if (await corporations.AllianceAsync(id) is { Deleted: false }) return MemberKind.Alliance;
        if (await characters.GetAsync(id) is not null) return MemberKind.Character;
        return null;
    }
}
