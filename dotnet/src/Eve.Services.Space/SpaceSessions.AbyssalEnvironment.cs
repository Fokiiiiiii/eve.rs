using System.Collections.Concurrent;
using Eve.Content;
using Eve.Dogma;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    private sealed record AbyssalEnvironmentRuntime(SimEnsureAbyssalEnvironmentObject Request, AbyssalEnvironmentalTypeDefinition? Mechanics);
    private readonly ConcurrentDictionary<long, AbyssalEnvironmentRuntime> _abyssalEnvironmentObjects = new();
    private readonly ConcurrentDictionary<(SimSceneKey Scene, long Source, long Ship), ulong> _abyssalFieldMemberships = new();
    private readonly ConcurrentDictionary<(SimSceneKey Scene, long Ship), SemaphoreSlim> _abyssalFieldRefreshGates = new();
    private readonly ConcurrentDictionary<long, SimSpawn> _abyssalNpcBaselines = new();

    public async Task<string?> EnsureAbyssalEnvironmentObjectAsync(
        string contentId, SimSceneKey scene, long itemId, int typeId, long ownerId,
        IReadOnlyList<double> position, IReadOnlyList<double> direction,
        AbyssalEnvironmentalTypeDefinition? mechanics = null,
        CancellationToken cancellationToken = default,
        double? presentationRadiusMeters = null)
    {
        if (!Guid.TryParse(contentId, out var content) || !scene.IsValid || scene.InstanceId <= 0 ||
            itemId <= 0 || typeId <= 0 || ownerId <= 0 || !IsVector(position) || !IsVector(direction) ||
            direction.All(value => value == 0)) return "BAD_ABYSSAL_ENVIRONMENT_OBJECT";
        if (!_abyssalEnvironmentByScene.ContainsKey(scene)) return "ABYSSAL_ENVIRONMENT_SCENE_NOT_ENSURED";
        if (mechanics is not null && (mechanics.TypeId != typeId || !double.IsFinite(mechanics.RadiusMeters) || mechanics.RadiusMeters <= 0))
            return "ABYSSAL_ENVIRONMENT_PROFILE_MISMATCH";
        if (mechanics is not null && mechanics.Buffs.Any(buff => !_statics.BuffCollections.ContainsKey(buff.CollectionId)))
            return "ABYSSAL_ENVIRONMENT_BUFF_COLLECTION_MISSING";
        await _statics.EnsureTypesAsync([typeId], cancellationToken).ConfigureAwait(false);
        var type = _statics.Type(typeId);
        if (type is null || type.GroupId <= 0 || type.CategoryId <= 0) return "ABYSSAL_ENVIRONMENT_SDE_TYPE_MISSING";
        var radius = presentationRadiusMeters ?? type.Attributes.GetValueOrDefault(162);
        var length = Math.Sqrt(direction.Sum(value => value * value));
        if (!double.IsFinite(radius) || radius < 0 || !double.IsFinite(length) || length <= 0)
            return "BAD_ABYSSAL_ENVIRONMENT_OBJECT";
        var body = new SimAbyssalSceneObject(scene, itemId, typeId, type.GroupId, type.CategoryId,
            ownerId, radius, type.GraphicId, type.Name, position.ToArray(), direction.Select(value => value / length).ToArray(), false);
        var pulse = mechanics?.PointDefense is not { } defense ? null : new SimAbyssalPointDefense(
            defense.TickIntervalMs, defense.Damage, defense.Targets.TypeListId,
            defense.Targets.IncludedCategories, defense.Targets.IncludedGroups, defense.Targets.IncludedTypes,
            defense.Targets.ExcludedCategories, defense.Targets.ExcludedGroups, defense.Targets.ExcludedTypes, defense.EffectName);
        var request = new SimEnsureAbyssalEnvironmentObject(NextSpawnRequest(), content.ToString("D"),
            new SimAbyssalEnvironmentObject(body, mechanics?.RadiusMeters ?? 0, pulse));
        var runtime = new AbyssalEnvironmentRuntime(request, mechanics);
        var stored = _abyssalEnvironmentObjects.GetOrAdd(itemId, runtime);
        if (!SimCodec.Encode(stored.Request with { RequestId = 1 }).AsSpan().SequenceEqual(SimCodec.Encode(request with { RequestId = 1 })) ||
            !(stored.Mechanics?.Buffs.Select(buff => (buff.CollectionId, buff.Value, buff.Operation, buff.AggregateMode)) ?? [])
                .SequenceEqual(mechanics?.Buffs.Select(buff => (buff.CollectionId, buff.Value, buff.Operation, buff.AggregateMode)) ?? []))
            return "ABYSSAL_ENVIRONMENT_IDENTITY_CONFLICT";
        try
        {
            return await _sim.EnsureAbyssalEnvironmentObjectAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return error.Message; }
    }

    private IReadOnlyList<EffectDef> LocalAbyssalEffects(SimSceneKey scene, long shipId)
    {
        var buffs = _abyssalFieldMemberships.Keys.Where(key => key.Scene == scene && key.Ship == shipId)
            .SelectMany(key => _abyssalEnvironmentObjects.TryGetValue(key.Source, out var source)
                && source.Request.Object.Body.Scene == scene ? source.Mechanics?.Buffs ?? [] : []);
        return buffs.GroupBy(buff => buff.CollectionId).Select(group =>
        {
            var collection = _statics.BuffCollections[group.Key];
            var value = collection.Weakest ? group.Min(buff => buff.Value) : group.Max(buff => buff.Value);
            return collection.EffectFor(value);
        }).ToArray();
    }

    internal async Task ApplyAbyssalFieldMembershipAsync(SimAbyssalFieldMembership membership)
    {
        if (!_abyssalEnvironmentObjects.TryGetValue(membership.SourceItemId, out var source) ||
            source.Request.Object.Body.Scene != membership.Scene) return;
        var gate = _abyssalFieldRefreshGates.GetOrAdd((membership.Scene, membership.ShipId), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A queued notice must not resurrect source state after a confirmed scene teardown.
            if (!_abyssalEnvironmentObjects.TryGetValue(membership.SourceItemId, out var current) || !ReferenceEquals(current, source)) return;
            var key = (membership.Scene, membership.SourceItemId, membership.ShipId);
            if (membership.Inside) _abyssalFieldMemberships[key] = membership.SessionId;
            else _abyssalFieldMemberships.TryRemove(key, out _);
            if (_pilots.TryGetValue(membership.SessionId, out var pilot) && pilot.ShipId == membership.ShipId && pilot.SimulationScene == membership.Scene)
            {
                await BuffedAsync(pilot.Session, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            if (_abyssalOfflineResidents.TryGetValue(membership.ShipId, out var offline) && offline.Scene == membership.Scene &&
                await _ships.LoadAsync(offline.CharacterId, CancellationToken.None).ConfigureAwait(false) is { } snapshot)
            {
                await SyncBuffsAsync(snapshot.World, membership.ShipId, CancellationToken.None).ConfigureAwait(false);
                _sim.ShipChanged(offline.SessionId, SimShips.Of(snapshot, _statics));
                return;
            }
            if (_abyssalNpcBaselines.TryGetValue(membership.ShipId, out var baseline) && baseline.Scene == membership.Scene)
            {
                var effective = ApplyAbyssalEnvironment(baseline, baseline.AbyssalContentId!);
                var error = await _sim.RefreshAbyssalNpcAsync(new SimRefreshAbyssalNpc(
                    NextSpawnRequest(), membership.Scene, membership.SessionId, effective.Ship, effective.Identity), CancellationToken.None).ConfigureAwait(false);
                if (error is not null && error != "ABYSSAL_NPC_ALREADY_DESTROYED")
                    throw new InvalidOperationException($"Abyssal NPC field refresh failed: {error}");
            }
        }
        finally { gate.Release(); }
    }

    private void ForgetAbyssalEnvironmentObjects(SimSceneKey scene)
    {
        foreach (var source in _abyssalEnvironmentObjects.Where(pair => pair.Value.Request.Object.Body.Scene == scene))
            _abyssalEnvironmentObjects.TryRemove(source.Key, out _);
        foreach (var key in _abyssalFieldMemberships.Keys.Where(key => key.Scene == scene))
            _abyssalFieldMemberships.TryRemove(key, out _);
        foreach (var key in _abyssalFieldRefreshGates.Keys.Where(key => key.Scene == scene))
            _abyssalFieldRefreshGates.TryRemove(key, out _);
        foreach (var npc in _abyssalNpcBaselines.Where(pair => pair.Value.Scene == scene))
            _abyssalNpcBaselines.TryRemove(npc.Key, out _);
    }
}
