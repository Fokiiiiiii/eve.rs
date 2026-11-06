using System.Text.Json;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services.Space;
using Eve.Space;
using Npgsql;

namespace Eve.Host;

/// <summary>Source-backed placement policy over the existing SDE type-list matcher and same-link Sim snapshot.</summary>
public sealed class AbyssalDeploymentEligibility(JsonElement proximityTypes, ItemTypeCatalog itemTypes)
{
    public const int ProximityTypeListId = 50;
    public const double MinimumCenterDistanceMeters = 1_000_000;

    public static async Task<AbyssalDeploymentEligibility> LoadAsync(SdeReader sde, ItemTypeCatalog types, CancellationToken ct = default)
    {
        var row = (await sde.ReadAsync("typeLists", [ProximityTypeListId], ct).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new InvalidDataException("The SDE Abyssal filament proximity type list is missing.");
        return new AbyssalDeploymentEligibility(row.Doc.Clone(), types);
    }

    public static void ValidateProbe(AbyssalRunAdmissionRequest request, AbyssalParticipantPoseResult pose)
    {
        if (!pose.Found || pose.ShipId != request.ShipId || pose.Scene != SimSceneKey.SolarSystem(request.SolarSystemId) ||
            pose.Position.Count != 3 || pose.Direction.Count != 3 ||
            pose.Position.Concat(pose.Direction).Any(value => !double.IsFinite(value)) ||
            pose.Direction.All(value => value == 0) || pose.Deployment is null)
            throw AbyssalErrorAdapter.ClientError(22);
    }

    public static void ValidateSessionState(AbyssalParticipantPoseResult pose)
    {
        if (pose.SessionChanging) throw AbyssalErrorAdapter.ClientError(11);
        if (pose.SelfDestructPending) throw AbyssalErrorAdapter.ClientError(18);
    }

    public static void ValidateShipEffects(AbyssalParticipantPoseResult pose)
    {
        var state = pose.Deployment ?? throw new InvalidDataException("An authoritative deployment snapshot is required.");
        if (state.Cloaked) throw AbyssalErrorAdapter.ClientError(15);
        if (state.UndockInvulnerable) throw AbyssalErrorAdapter.ClientError(17);
        if (pose.Tethered) throw AbyssalErrorAdapter.ClientError(19);
        if (state.Warping) throw AbyssalErrorAdapter.ClientError(14);
        if (state.WarpScrambled) throw AbyssalErrorAdapter.ClientError(20);
        if (state.CynoActive) throw AbyssalErrorAdapter.ClientError(22);
    }

    public async Task ValidateNearbyAsync(long ownShipId, SimAbyssalDeploymentSnapshot snapshot, CancellationToken ct = default)
    {
        var candidates = snapshot.NearbyObjects.Where(value => value.ItemId != ownShipId).ToArray();
        if (candidates.Any(value => value.TypeId <= 0 || !double.IsFinite(value.CenterDistanceMeters) || value.CenterDistanceMeters < 0))
            throw new InvalidDataException("The deployment snapshot contains an invalid proximity object.");
        var types = await itemTypes.GetAsync(candidates.Select(value => value.TypeId), ct).ConfigureAwait(false);
        var seen = new HashSet<long>();
        foreach (var candidate in candidates)
        {
            if (candidate.ItemId > 0 && !seen.Add(candidate.ItemId)) continue;
            if (!types.TryGetValue(candidate.TypeId, out var type))
                throw new InvalidDataException($"Deployment proximity type {candidate.TypeId} is missing from SDE.");
            if (candidate.CenterDistanceMeters <= MinimumCenterDistanceMeters && EncodedItemsRpcService.IsInTypeList(proximityTypes, type))
                throw AbyssalErrorAdapter.ClientError(8, new PyInt(candidate.TypeId));
        }
    }

    public async Task ValidatePersistedTracesAsync(PgItemStore items, NpgsqlConnection connection, NpgsqlTransaction transaction,
        AbyssalRunAdmissionRequest request, CancellationToken ct = default)
    {
        var type = await itemTypes.GetAsync(AbyssalTraceFactory.TraceTypeId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The SDE trace type is missing.");
        if (!EncodedItemsRpcService.IsInTypeList(proximityTypes, type)) return;
        var traces = await items.LockInLocationAsync(connection, transaction, request.SolarSystemId,
            AbyssalTraceFactory.TraceOwnerId, Eve.Ships.CharacterShips.InSpaceFlag, [type.TypeId], ct).ConfigureAwait(false);
        foreach (var trace in traces)
        {
            var pose = JsonSerializer.Deserialize<AbyssalTracePoseSnapshot>(trace.CustomInfo ?? "", Json)
                ?? throw new InvalidDataException("A persisted public trace has no authoritative pose.");
            if (pose.Position.Count != 3 || pose.Position.Any(value => !double.IsFinite(value)))
                throw new InvalidDataException("A persisted public trace has an invalid pose.");
            var dx = request.SourceShipPosition.X - pose.Position[0];
            var dy = request.SourceShipPosition.Y - pose.Position[1];
            var dz = request.SourceShipPosition.Z - pose.Position[2];
            if (dx * dx + dy * dy + dz * dz <= MinimumCenterDistanceMeters * MinimumCenterDistanceMeters)
                throw AbyssalErrorAdapter.ClientError(8, new PyInt(type.TypeId));
        }
    }

    public static double NormalizeSecurityBand(double security) => Math.Clamp(Math.Floor(security * 10 + 0.5) / 10, 0.5, 1);

    public static bool RequiresSuspect(int tier, double security)
    {
        var band = NormalizeSecurityBand(security);
        var maxTier = band >= .9 ? 0 : band >= .8 ? 3 : band >= .7 ? 4 : band >= .6 ? 5 : 6;
        return band < .9 && tier > maxTier;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
