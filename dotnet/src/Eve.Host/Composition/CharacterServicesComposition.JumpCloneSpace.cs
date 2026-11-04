using Eve.Services.Core;
using Eve.Structures;

namespace Eve.Host;

/// <summary>

/// the jump clone service to the space side. The link is set once the space sessions and the structures are composed.
/// </summary>
internal sealed partial class CharacterServicesComposition
{

    private const int CloningBayTaxSettingId = 23;


    private const decimal MaxStructureCloneBayFee = 100000000m;

    private readonly JumpCloneSpaceLink jumpCloneSpace = new();

    /// <summary>The clone installs read the space side on every server; without a space scene no ship is in space (its distance is none).</summary>
    private void LinkJumpCloneSpace() =>
        jumpCloneSpace.Space = new HostJumpCloneSpace(spaceSessions, ships, dogma, characterSessions, StructureCloneBayAsync);

    /// <summary>
    /// A structure's clone bay for a character: its Medical service online, or its Jump Clone service online and the character allowed to dock

    /// </summary>
    private async ValueTask<StructureCloneBay?> StructureCloneBayAsync(long characterId, long structureId, CancellationToken token)
    {
        StructureRecord? record;
        int medical, jumpClone;
        await using (var connection = await source.OpenConnectionAsync(token))
        {
            record = await structureStore.GetAsync(connection, null, structureId, false, token);
            if (record is null || record.Snapshot.DestroyedMs > 0)
                return null;
            var services = await structureStore.ServicesAsync(connection, null, structureId, token);
            medical = services.GetValueOrDefault(Eve.Structures.StructureServiceIds.Medical);
            jumpClone = services.GetValueOrDefault(Eve.Structures.StructureServiceIds.JumpClone);
        }

        var hasMedicalAccess = medical == Eve.Structures.ServiceState.Online
            && await structureDirectoryReadModel.CharacterHasServiceAsync(
                characterId, structureId, Eve.Structures.StructureServiceIds.Medical, token);
        if (!hasMedicalAccess
            && (jumpClone != Eve.Structures.ServiceState.Online || !await StructureDockingAllowedAsync(characterId, structureId, token)))
            return null;

        // A structure with no profile uses the default clone-bay fee.
        var fee = 900000m;
        if (record.ProfileId > 0
            && (await structureProfilesRpcBackend.GetProfileSettingsAsync(record.OwnerCorporationId, record.ProfileId, token))
                .TryGetValue(CloningBayTaxSettingId, out var groups)
            && groups.Count > 0)
        {
            fee = Math.Clamp(groups[0].Value switch
            {
                Eve.Marshal.PyReal real => (decimal)real.Value,
                Eve.Marshal.PyInt integer => integer.Value,
                Eve.Marshal.PyLong longValue => longValue.Value,
                _ => fee,
            }, 0m, MaxStructureCloneBayFee);
        }
        return new StructureCloneBay(structureId, record.OwnerCorporationId, fee);
    }
}
