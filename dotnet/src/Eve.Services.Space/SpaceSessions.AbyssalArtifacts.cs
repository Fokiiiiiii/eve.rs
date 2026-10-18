using System.Collections.Concurrent;
using Eve.Characters;
using Eve.Inventory;
using Eve.Space;

namespace Eve.Services.Space;

public sealed record AbyssalRewardCacheDestroyedNotice(
    string ContentId, long InstanceId, int SpaceLocationId, int RoomOrdinal,
    long CacheItemId, int TypeId, long KillerShipId,
    IReadOnlyList<double> Position, IReadOnlyList<double> Direction);

public sealed partial class SpaceSessions
{
    private readonly ConcurrentDictionary<long, SimEnsureAbyssalRewardCache> _abyssalRewardCaches = new();

    /// <summary>The Host durably records source destruction before materializing any reward; repeated notices are idempotent.</summary>
    public Func<AbyssalRewardCacheDestroyedNotice, CancellationToken, ValueTask>? AbyssalRewardCacheDestroyedAsync { get; set; }

    public async Task<string?> EnsureAbyssalRewardCacheAsync(
        string contentId, SimSceneKey scene, long cacheItemId, int typeId, long ownerId,
        IReadOnlyList<double> position, IReadOnlyList<double> direction,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(contentId, out var content) || !scene.IsValid || scene.InstanceId <= 0 ||
            cacheItemId <= 0 || ownerId <= 0 || typeId is not (47_951 or 49_689 or 49_661 or 49_662) ||
            !IsVector(position) || !IsVector(direction) || direction.All(value => value == 0))
            return "BAD_ABYSSAL_REWARD_CACHE";
        if (!_abyssalEnvironmentByScene.ContainsKey(scene)) return "ABYSSAL_CACHE_SCENE_NOT_ENSURED";
        await _statics.EnsureTypesAsync([typeId], cancellationToken).ConfigureAwait(false);
        var type = _statics.Type(typeId);
        if (type is null || type.GroupId != 2_009 || type.CategoryId != 11)
            return "ABYSSAL_CACHE_SDE_TYPE_MISMATCH";
        var entity = SimShips.OfType(cacheItemId, type) with { OwnerId = ownerId, MaxVelocity = 0 };
        if (entity.Vitals is not { StructureHp: > 0 }) return "ABYSSAL_CACHE_SDE_HEALTH_MISSING";
        var length = Math.Sqrt(direction.Sum(value => value * value));
        if (!double.IsFinite(length) || length <= 0) return "BAD_ABYSSAL_REWARD_CACHE";
        var request = new SimEnsureAbyssalRewardCache(NextSpawnRequest(), content.ToString("D"), scene, entity,
            position.ToArray(), direction.Select(value => value / length).ToArray());
        var stored = _abyssalRewardCaches.GetOrAdd(cacheItemId, request);
        if (stored.ContentId != request.ContentId || stored.Scene != scene || stored.Entity.TypeId != typeId ||
            stored.Entity.OwnerId != ownerId || !stored.Position.SequenceEqual(request.Position) ||
            !stored.Direction.SequenceEqual(request.Direction))
            return "ABYSSAL_CACHE_IDENTITY_CONFLICT";
        try
        {
            return await _sim.EnsureAbyssalRewardCacheAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return error.Message; }
    }

    private async Task FinishAbyssalRewardCacheDestroyedAsync(SimEnsureAbyssalRewardCache cache, SimDestroyed destroyed)
    {
        if (destroyed.ShipId != cache.Entity.ShipId || destroyed.ShipTypeId != cache.Entity.TypeId ||
            !IsVector(destroyed.Position) || !IsVector(destroyed.Direction))
            throw new InvalidDataException("ABYSSAL_CACHE_DESTRUCTION_IDENTITY_MISMATCH");
        ForgetLockedTarget(destroyed.ShipId);
        var handler = AbyssalRewardCacheDestroyedAsync
            ?? throw new InvalidOperationException("The Abyssal reward-cache destruction authority is not connected.");
        await handler(new AbyssalRewardCacheDestroyedNotice(
            cache.ContentId, cache.Scene.InstanceId, cache.Scene.LocationId, cache.Scene.RoomIndex,
            destroyed.ShipId, destroyed.ShipTypeId, destroyed.KillerShipId,
            destroyed.Position.ToArray(), destroyed.Direction.ToArray()), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Confirms a durable Host loot item in its exact private scene; retries preserve its live state.</summary>
    public async Task<string?> EnsureAbyssalLootContainerAsync(
        string contentId, SimPlaceWreck container, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(contentId, out var content) || !container.Scene.IsValid || container.Scene.InstanceId <= 0 ||
            !_abyssalEnvironmentByScene.ContainsKey(container.Scene)) return "BAD_ABYSSAL_LOOT_CONTAINER";
        try
        {
            return await _sim.EnsureAbyssalLootContainerAsync(
                new SimEnsureAbyssalLootContainer(NextSpawnRequest(), content.ToString("D"), container),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return error.Message; }
    }

    internal RunSpaceItemScope? RunSpaceScopeOf(ClientSession session)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot) || pilot.SimulationScene.InstanceId == 0)
            return null;
        if (!pilot.SimulationScene.IsValid || pilot.CharacterId != session.CharacterId ||
            pilot.ShipId != session.GetInt("shipid") || !Guid.TryParse(pilot.AbyssalContentId, out var contentId) || contentId == Guid.Empty)
            throw new InvalidDataException("An Abyssal item requires the active participant's authoritative content, ship and room identity.");
        return new RunSpaceItemScope(contentId, pilot.SimulationScene.InstanceId, pilot.SimulationScene.RoomIndex,
            pilot.SimulationScene.LocationId, pilot.CharacterId);
    }

    /// <summary>Detaches a persisted participant before an administrative origin return, including after both processes restart.</summary>
    public async Task<AbyssalParticipantSettlementResult> DetachAbyssalParticipantAsync(
        string contentId, SimSceneKey source, Character persistedCharacter, Item persistedShip,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistedCharacter);
        ArgumentNullException.ThrowIfNull(persistedShip);
        if (!Guid.TryParse(contentId, out var content) || !source.IsValid || source.InstanceId <= 0 ||
            persistedCharacter.Id <= 0 || persistedCharacter.ShipId != persistedShip.Id || persistedShip.Id <= 0 ||
            persistedShip.OwnerId != persistedCharacter.Id || !persistedShip.Singleton || persistedShip.Quantity != 1 ||
            persistedCharacter.SolarSystemId != source.LocationId || persistedShip.Flag != 0 ||
            persistedShip.LocationId != source.LocationId)
            return new(false, false, false, "BAD_ABYSSAL_DETACH_SNAPSHOT");
        // Character.StationId retains the last station during ordinary undock; the active ship's persisted
        // space location/flag and character ship ownership identify the resident being detached.
        var candidates = _pilots.Values.Where(pilot => pilot.ShipId == persistedShip.Id).ToArray();
        if (candidates.Length > 1) return new(false, false, false, "ABYSSAL_DETACH_DUPLICATE_RESIDENT");
        var pilot = candidates.SingleOrDefault();
        var hasOffline = _abyssalOfflineResidents.TryGetValue(persistedShip.Id, out var offline);
        bool Matches(string? residentContent, long characterId, SimSceneKey residentScene) =>
            Guid.TryParse(residentContent, out var id) && id == content && characterId == persistedCharacter.Id && residentScene == source;
        if (pilot is not null && !Matches(pilot.AbyssalContentId, pilot.CharacterId, pilot.SimulationScene) ||
            hasOffline && !Matches(offline!.ContentId, offline.CharacterId, offline.Scene) || pilot is not null && hasOffline)
            return new(false, false, false, "ABYSSAL_DETACH_RESIDENT_MISMATCH");
        SimRemoveAbyssalParticipantReply removed;
        try
        {
            removed = await _sim.RemoveAbyssalParticipantAsync(
                new SimRemoveAbyssalParticipant(NextSpawnRequest(), source, persistedShip.Id), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return new(false, false, false, error.Message); }
        if (removed.Error is not null) return new(false, false, false, removed.Error);
        if (pilot is not null)
        {
            pilot.End();
            await _charges.CloseAsync((ulong)pilot.Session.SessionId).ConfigureAwait(false);
            await CancelRepairsAsync(pilot, CancellationToken.None).ConfigureAwait(false);
            ReleaseBoundObjects(pilot.Session);
            _pilots.TryRemove((ulong)pilot.Session.SessionId, out _);
        }
        _abyssalOfflineResidents.TryRemove(persistedShip.Id, out _);
        return new(!removed.AlreadyAbsent, removed.AlreadyAbsent, false, null);
    }
}
