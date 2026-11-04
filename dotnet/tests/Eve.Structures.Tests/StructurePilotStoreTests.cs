using Eve.Data;

namespace Eve.Structures.Tests;

public sealed class StructurePilotStoreTests
{
    private static StructurePilotRequest Request(
        long structureId,
        long characterId,
        long sessionId,
        long shipId,
        long? locationId,
        bool fromSpace) =>
        new(structureId, characterId, sessionId, shipId, locationId, fromSpace);

    private static Task<StructurePilotAcquireResult> Acquire(
        Rig rig,
        StructurePilotStore store,
        StructurePilotRequest request,
        bool takeoverAuthorized = true) =>
        rig.Commit((connection, transaction) => store.AcquireAsync(connection, transaction, request, takeoverAuthorized));

    private static Task<StructurePilotRecord?> GetByStructure(Rig rig, StructurePilotStore store, long structureId) =>
        rig.Commit((connection, transaction) => store.GetByStructureAsync(connection, transaction, structureId));

    private static Task<StructurePilotRecord?> GetBySession(Rig rig, StructurePilotStore store, long sessionId) =>
        rig.Commit((connection, transaction) => store.GetBySessionAsync(connection, transaction, sessionId));

    private static Task<bool> Release(Rig rig, StructurePilotStore store, StructurePilotRecord expectedOwner) =>
        rig.Commit((connection, transaction) => store.ReleaseAsync(connection, transaction, expectedOwner));

    private static Task<StructurePilotRecord?> Restore(
        Rig rig,
        StructurePilotStore store,
        StructurePilotRecord expectedOwner,
        StructurePilotRecord displaced) =>
        rig.Commit((connection, transaction) => store.RestoreAsync(connection, transaction, expectedOwner, displaced));

    [PgFact]
    public async Task Same_session_retry_preserves_return_state_and_authorized_takeover_returns_displaced_pilot()
    {
        await using var rig = await Rig.Create();
        const long structureId = 8101;
        await rig.Anchor(structureId);
        var store = new StructurePilotStore(rig.Store);
        var original = await Acquire(rig, store, Request(structureId, 9101, 10101, 11101, 12101, false), false);

        Assert.Equal(StructurePilotAcquireStatus.Acquired, original.Status);
        Assert.NotNull(original.Current);

        var retry = await Acquire(rig, store, Request(structureId, 9101, 10101, 11999, null, true), false);
        Assert.Equal(StructurePilotAcquireStatus.AlreadyOwned, retry.Status);
        Assert.Equal(original.Current, retry.Current);
        Assert.Null(retry.Displaced);

        var denied = await Acquire(rig, store, Request(structureId, 9102, 10102, 11102, null, true), false);
        Assert.Equal(StructurePilotAcquireStatus.TakeoverDenied, denied.Status);
        Assert.Equal(original.Current, await GetByStructure(rig, store, structureId));

        var takeover = await Acquire(rig, store, Request(structureId, 9102, 10102, 11102, null, true));
        Assert.Equal(StructurePilotAcquireStatus.Acquired, takeover.Status);
        Assert.Equal(original.Current, takeover.Displaced);
        Assert.Equal(2L, takeover.Current!.Version);
        Assert.Null(await GetBySession(rig, store, 10101));
        Assert.Equal(takeover.Current, await GetBySession(rig, store, 10102));
    }

    [PgFact]
    public async Task Missing_destroyed_and_invalid_structure_requests_are_rejected()
    {
        await using var rig = await Rig.Create();
        var store = new StructurePilotStore(rig.Store);

        var missing = await Acquire(rig, store, Request(8201, 9201, 10201, 11201, null, false));
        Assert.Equal(StructurePilotAcquireStatus.StructureNotFound, missing.Status);

        const long destroyedId = 8202;
        await rig.Anchor(destroyedId);
        await rig.Commit((connection, transaction) => Sql.ExecuteAsync(
            connection,
            transaction,
            $"UPDATE {rig.StructuresSchema}.structures SET destroyed_ms = 1 WHERE structure_id = $1",
            CancellationToken.None,
            destroyedId));
        var destroyed = await Acquire(rig, store, Request(destroyedId, 9202, 10202, 11202, null, false));
        Assert.Equal(StructurePilotAcquireStatus.StructureDestroyed, destroyed.Status);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Acquire(rig, store, Request(0, 9203, 10203, 11203, null, false)));
    }

    [PgFact]
    public async Task Character_and_session_ids_cannot_control_different_structures()
    {
        await using var rig = await Rig.Create();
        const long firstStructure = 8301;
        const long secondStructure = 8302;
        await rig.Anchor(firstStructure);
        await rig.Anchor(secondStructure);
        var store = new StructurePilotStore(rig.Store);
        var first = await Acquire(rig, store, Request(firstStructure, 9301, 10301, 11301, 12301, false));

        var characterConflict = await Acquire(rig, store, Request(secondStructure, 9301, 10302, 11302, null, true));
        var sessionConflict = await Acquire(rig, store, Request(secondStructure, 9302, 10301, 11303, null, true));

        Assert.Equal(StructurePilotAcquireStatus.IdentityInUse, characterConflict.Status);
        Assert.Equal(StructurePilotAcquireStatus.IdentityInUse, sessionConflict.Status);
        Assert.Null(await GetByStructure(rig, store, secondStructure));
        Assert.Equal(first.Current, await GetByStructure(rig, store, firstStructure));
    }

    [PgFact]
    public async Task Concurrent_authorized_acquisitions_serialize_on_the_structure_row()
    {
        await using var rig = await Rig.Create();
        const long structureId = 8401;
        await rig.Anchor(structureId);
        var store = new StructurePilotStore(rig.Store);

        var results = await Task.WhenAll(
            Acquire(rig, store, Request(structureId, 9401, 10401, 11401, 12401, false)),
            Acquire(rig, store, Request(structureId, 9402, 10402, 11402, null, true)));

        Assert.All(results, result => Assert.Equal(StructurePilotAcquireStatus.Acquired, result.Status));
        var initial = Assert.Single(results, result => result.Displaced is null);
        var takeover = Assert.Single(results, result => result.Displaced is not null);
        var current = (await GetByStructure(rig, store, structureId))!;
        Assert.Equal(1L, initial.Current!.Version);
        Assert.Equal(2L, current.Version);
        Assert.Equal(takeover.Current, current);
        Assert.Contains(current.CharacterId, new long[] { 9401, 9402 });
        Assert.Equal(current, await GetBySession(rig, store, current.SessionId));
        Assert.Null(await GetBySession(rig, store, takeover.Displaced!.SessionId));
    }

    [PgFact]
    public async Task Stale_disconnect_and_binding_rollback_cannot_remove_a_newer_owner()
    {
        await using var rig = await Rig.Create();
        const long structureId = 8501;
        await rig.Anchor(structureId);
        var store = new StructurePilotStore(rig.Store);
        var first = await Acquire(rig, store, Request(structureId, 9501, 10501, 11501, 12501, false));
        var second = await Acquire(rig, store, Request(structureId, 9502, 10502, 11502, null, true));
        var third = await Acquire(rig, store, Request(structureId, 9503, 10503, 11503, null, true));

        Assert.False(await Release(rig, store, first.Current!));
        Assert.False(await Release(rig, store, second.Current!));
        Assert.Null(await Restore(rig, store, second.Current!, second.Displaced!));
        var mismatched = second.Displaced! with { StructureId = structureId + 1 };
        Assert.Null(await Restore(rig, store, third.Current!, mismatched));
        Assert.Equal(third.Current, await GetByStructure(rig, store, structureId));
        Assert.True(await Release(rig, store, third.Current!));
        Assert.False(await Release(rig, store, third.Current!));
    }

    [PgFact]
    public async Task Reacquiring_after_release_never_reuses_a_stale_owner_version()
    {
        await using var rig = await Rig.Create();
        await rig.Anchor(1001);
        var store = new StructurePilotStore(rig.Store);
        var request = Request(1001, 9101, 10101, 11101, 12101, false);
        var first = (await Acquire(rig, store, request)).Current!;
        Assert.True(await Release(rig, store, first));
        var second = (await Acquire(rig, store, request)).Current!;
        Assert.True(second.Version > first.Version);
        Assert.False(await Release(rig, store, first));
        Assert.Equal(second, await GetByStructure(rig, store, 1001));
    }

    [PgFact]
    public async Task Failed_binding_can_restore_the_displaced_pilot_with_a_new_version()
    {
        await using var rig = await Rig.Create();
        const long structureId = 8601;
        await rig.Anchor(structureId);
        var store = new StructurePilotStore(rig.Store);
        var previous = await Acquire(rig, store, Request(structureId, 9601, 10601, 11601, 12601, false));
        var takeover = await Acquire(rig, store, Request(structureId, 9602, 10602, 11602, null, true));

        var restored = await Restore(rig, store, takeover.Current!, takeover.Displaced!);

        Assert.Equal(previous.Current!.CharacterId, restored!.CharacterId);
        Assert.Equal(previous.Current!.SessionId, restored!.SessionId);
        Assert.Equal(takeover.Current!.Version + 1, restored!.Version);
        Assert.False(await Release(rig, store, previous.Current!));
        Assert.True(await Release(rig, store, restored!));
    }

    [PgFact]
    public async Task Restart_clear_and_structure_removal_remove_pilot_rows()
    {
        await using var rig = await Rig.Create();
        const long structureId = 8701;
        await rig.Anchor(structureId);
        var store = new StructurePilotStore(rig.Store);
        var pilot = await Acquire(rig, store, Request(structureId, 9701, 10701, 11701, null, true));

        var cleared = await rig.Commit((connection, transaction) => store.ClearOnRestartAsync(connection, transaction));
        Assert.Equal(1, cleared);
        Assert.Null(await GetBySession(rig, store, 10701));

        var afterRestart = await Acquire(rig, store, Request(structureId, 9701, 10701, 11701, null, true));
        Assert.Equal(StructurePilotAcquireStatus.Acquired, afterRestart.Status);
        await rig.Commit((connection, transaction) => Sql.ExecuteAsync(
            connection,
            transaction,
            $"DELETE FROM {rig.StructuresSchema}.structures WHERE structure_id = $1",
            CancellationToken.None,
            structureId));
        Assert.Null(await GetByStructure(rig, store, structureId));
        Assert.Null(await GetBySession(rig, store, 10701));
        Assert.NotNull(pilot.Current);
    }

    [PgFact]
    public async Task Separate_rig_schemas_keep_pilot_authority_isolated()
    {
        await using var firstRig = await Rig.Create();
        await using var secondRig = await Rig.Create();
        const long structureId = 8801;
        await firstRig.Anchor(structureId);
        await secondRig.Anchor(structureId);
        var firstStore = new StructurePilotStore(firstRig.Store);
        var secondStore = new StructurePilotStore(secondRig.Store);

        var acquired = await Acquire(firstRig, firstStore, Request(structureId, 9801, 10801, 11801, 12801, false));

        Assert.NotNull(acquired.Current);
        Assert.Equal(acquired.Current, await GetByStructure(firstRig, firstStore, structureId));
        Assert.Null(await GetByStructure(secondRig, secondStore, structureId));
        Assert.Null(await GetBySession(secondRig, secondStore, 10801));
    }
}
