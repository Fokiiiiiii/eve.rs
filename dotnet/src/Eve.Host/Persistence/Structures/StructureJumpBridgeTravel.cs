using Eve.Dogma;
using Eve.Events;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Structures;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using System.Runtime.CompilerServices;

namespace Eve.Host;

/// <summary>Executes the V24 Ansiblex RPC against the persisted structures, inventory and wallet.</summary>
internal sealed class StructureJumpBridgeTravel(
    NpgsqlDataSource database,
    StructureStore structures,
    PgItemStore items,
    DogmaStatic dogma,
    PersistenceStructureDirectoryReadModel directory,
    SpaceSessions? space,
    SolarSystemCatalog systems,
    WalletTransactions wallets,
    EventBus events,
    Func<long> now)
{
    private sealed record AcceptedJump(long SourceStructureId, int SourceSystemId, PyValue Result);

    private sealed class SessionJumpState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public AcceptedJump? Accepted { get; set; }
    }

    private readonly ConditionalWeakTable<ClientSession, SessionJumpState> _sessionJumps = new();

    private const int AnsiblexTypeId = 35_841;
    private const int JumpBridgeServiceId = StructureServiceIds.JumpBridge;
    private const int JumpBridgeServiceSettingId = 34;
    private const int LiquidOzoneTypeId = 16_273;
    private const int StructureFuelBayFlag = 172;
    private const int JournalRefType = 140;
    private const double JumpRangeMeters = 2_500;
    private const double MaximumRangeLy = 5;
    private const double MaximumShipMassKg = 1_480_000_000;
    private const decimal MaximumIskPerLiquidOzone = 2_000;
    private const string FeeReason = "Ansiblex jump bridge activation";

    public async ValueTask<PyValue> TravelAsync(ServiceCall call)
    {
        var structureId = call.Arg(0) switch
        {
            PyInt value when value.Value > 0 => value.Value,
            PyLong value when value.Value > 0 => value.Value,
            _ => 0,
        };
        if (structureId <= 0)
            throw Notify("That jump bridge is no longer available.");

        // A lost RPC reply may be retried while the simulation is still completing the accepted jump. Replay
        // the successful session-change result for this session/source instead of charging again or exposing
        // the simulation's generic "jump in progress" error to the client.
        var sessionJump = _sessionJumps.GetValue(call.Session, static _ => new SessionJumpState());
        await sessionJump.Gate.WaitAsync(call.Cancellation).ConfigureAwait(false);
        try
        {
            if (sessionJump.Accepted is { } accepted)
            {
                if (space?.SolarSystemOf(call.Session) == accepted.SourceSystemId)
                {
                    if (accepted.SourceStructureId == structureId)
                        return accepted.Result;
                    throw Notify("Another jump bridge activation is still pending.");
                }

                // Arrival changes the pilot's authoritative system. At that point the prior operation has
                // completed and this receipt must not mask a later, intentional use of a bridge.
                sessionJump.Accepted = null;
            }

            return await TravelCoreAsync(call, structureId, sessionJump).ConfigureAwait(false);
        }
        finally
        {
            sessionJump.Gate.Release();
        }
    }

    private async ValueTask<PyValue> TravelCoreAsync(ServiceCall call, long structureId, SessionJumpState sessionJump)
    {
        if (space is null || call.Session.CharacterId is not { } characterId ||
            space.ShipIdOf(call.Session) is not { } shipId || !space.IsInSpace(call.Session))
            throw Notify("You need an active ship to use a jump bridge.");

        var travel = await directory.GetJumpBridgeTravelStateAsync(call, structureId, call.Cancellation);
        if (travel is null)
            throw Notify("That jump bridge is not linked to a reciprocal destination.");
        var currentSystem = space.SolarSystemOf(call.Session);
        if (currentSystem != travel.SolarSystemId)
            throw Notify("You are not in the same solar system as that jump bridge.");
        await using var connection = await database.OpenConnectionAsync(call.Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation);
        if (!travel.SourceServiceOnline)
            throw Notify("That jump bridge service is offline.");
        var locked = new Dictionary<long, StructureRecord>();
        foreach (var id in new[] { structureId, travel.DestinationStructureId }.Where(id => id > 0).Order())
        {
            var record = await structures.GetAsync(connection, transaction, id, true, call.Cancellation);
            if (record is null || record.Snapshot.DestroyedMs > 0 || record.TypeId != AnsiblexTypeId)
                throw Notify("That jump bridge is no longer available.");
            locked.Add(id, record);
        }

        var sourceRecord = locked[structureId];
        if (sourceRecord.SolarSystemId != travel.SolarSystemId ||
            !await StructureServiceOnlineAsync(connection, transaction, structureId, call.Cancellation))
            throw Notify("That jump bridge service is offline.");
        if (await HasProximityConflictAsync(connection, transaction, sourceRecord, call.Cancellation))
            throw Notify("That jump bridge service is offline.");
        if (!travel.CharacterHasAccess)
            throw MachoErrors.ThrowUserError("ActivateJumpBridgeDenied");
        if (travel.DestinationStructureId <= 0)
            throw Notify("That jump bridge is not linked to a reciprocal destination.");

        var destination = locked[travel.DestinationStructureId];
        if (destination.SolarSystemId != travel.DestinationSolarSystemId || !travel.DestinationServiceOnline ||
            !await StructureServiceOnlineAsync(connection, transaction, destination.Snapshot.StructureId, call.Cancellation))
            throw Notify("The destination jump bridge service is offline.");
        if (await HasProximityConflictAsync(connection, transaction, destination, call.Cancellation))
            throw Notify("The destination jump bridge service is offline.");

        var sourcePosition = await space.PositionOfAsync(call.Session, sourceRecord.Snapshot.StructureId, call.Cancellation);
        var shipPosition = await space.PositionOfAsync(call.Session, shipId, call.Cancellation);
        if (sourcePosition is not { } sourceAt || shipPosition is not { } shipAt ||
            sourceAt.SolarSystemId != currentSystem || shipAt.SolarSystemId != currentSystem)
            throw Notify("That jump bridge is not reachable from your current location.");
        var surfaceDistance = Distance(sourceAt.X, sourceAt.Y, sourceAt.Z,
            shipAt.X, shipAt.Y, shipAt.Z) - Math.Max(0, sourceAt.Radius ?? 0) - Math.Max(0, shipAt.Radius ?? 0);
        if (Math.Max(0, surfaceDistance) > JumpRangeMeters)
            throw Notify("You are too far away from the jump bridge.");
        if (await space.IsWarpScrambledAsync(call.Session, call.Cancellation))
            throw Notify("You cannot use an Ansiblex while warp disrupted.");

        var distanceLy = systems.LightYearsBetween(sourceRecord.SolarSystemId, destination.SolarSystemId);
        if (distanceLy is > MaximumRangeLy + 0.000001)
            throw Notify("That jump bridge connection is beyond the maximum Ansiblex range.");
        var shipMass = await space.ActiveShipMassAsync(call.Session, call.Cancellation);
        if (shipMass is not { } mass || !double.IsFinite(mass) || mass <= 0)
            throw Notify("You need an active ship to use a jump bridge.");
        if (mass > MaximumShipMassKg)
            throw Notify("Your ship is too massive to use an Ansiblex jump bridge.");

        var ozoneRequired = Math.Max(50L, checked((long)Math.Ceiling(mass * (distanceLy ?? 0) * 0.000003 + 50)));
        var fuel = (await items.LockContainerAsync(connection, transaction, structureId, call.Cancellation))
            .Where(item => item.TypeId == LiquidOzoneTypeId && item.Flag == StructureFuelBayFlag &&
                (item.Singleton ? 1 : item.Quantity) > 0)
            .OrderBy(item => item.Id)
            .ToArray();
        var availableOzone = fuel.Sum(item => item.Singleton ? 1L : item.Quantity);
        if (availableOzone < ozoneRequired)
            throw Notify("That jump bridge does not have enough Liquid Ozone.");

        var rate = Math.Clamp(travel.IskPerLiquidOzone, 0m, MaximumIskPerLiquidOzone);
        var fee = decimal.Round(ozoneRequired * rate, 2, MidpointRounding.AwayFromZero);
        IReadOnlyDictionary<WalletAccount, decimal> balances = new Dictionary<WalletAccount, decimal>();
        var characterWallet = new WalletAccount(characterId);
        // EVEJS credits a corporation wallet division (CORPORATION_WALLET_KEY_START), not the
        // corporation's master wallet account.
        var corporationWallet = new WalletAccount(travel.OwnerCorporationId, 1_001);
        if (fee > 0)
        {
            balances = await wallets.LockBalancesAsync(connection, transaction, [characterWallet, corporationWallet], call.Cancellation);
            if (!balances.TryGetValue(characterWallet, out var balance) || balance < fee)
                throw MachoErrors.ThrowUserError("NotEnoughMoney",
                    ("balance", new PyReal((double)(balances.GetValueOrDefault(characterWallet)))),
                    ("amount", new PyReal((double)fee)));
        }

        async ValueTask Settle(CancellationToken cancellationToken)
        {
            var remaining = ozoneRequired;
            var changes = new List<ItemChanged>();
            var remainingByType = new Dictionary<int, long>();
            foreach (var item in fuel)
            {
                var quantity = item.Singleton ? 1 : item.Quantity;
                var taken = Math.Min(remaining, quantity);
                if (taken > 0)
                {
                    Item after;
                    if (taken == quantity)
                    {
                        await items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
                        after = item with { LocationId = CharacterShips.JunkLocation };
                    }
                    else
                    {
                        after = item with { Quantity = item.Quantity - taken };
                        await items.SetQuantityAsync(connection, transaction, item.Id, after.Quantity, cancellationToken);
                    }
                    changes.Add(new ItemChanged(after, structureId, item.Flag, item.Quantity));
                    remaining -= taken;
                }
                var left = quantity - taken;
                if (left > 0)
                    remainingByType[item.TypeId] = remainingByType.GetValueOrDefault(item.TypeId) + left;
            }
            if (remaining != 0)
                throw new InvalidOperationException("locked Ansiblex Liquid Ozone stacks became short");
            if (changes.Count > 0)
                await events.PublishInTransactionAsync(connection, transaction, StructureFuelBayNotifications.Topic,
                    new StructureFuelBayItemsChanged(structureId, changes,
                        remainingByType.Select(pair => new StructureFuelRemaining(pair.Key, pair.Value)).ToArray()),
                    $"structure:{structureId}", cancellationToken);

            WalletSettlement? settlement = null;
            if (fee > 0)
            {
                settlement = await wallets.TransferAsync(connection, transaction, characterWallet, corporationWallet, fee,
                    JournalRefType, FeeReason, now(), $"ansiblex:{Guid.NewGuid():N}", $"structure:{structureId}", cancellationToken,
                    referenceId: structureId);
            }
            await transaction.CommitAsync(cancellationToken);
            if (settlement is not null)
                foreach (var entry in settlement.Entries)
                    wallets.AnnounceCommitted(new WalletBalanceChange(entry.OwnerId, entry.AccountKey, entry.Balance));
            events.Wake();
        }

        var result = await space.StructureJumpAsync(call.Session, structureId, destination.SolarSystemId,
            destination.Snapshot.StructureId, null, call.Cancellation, Settle);
        sessionJump.Accepted = new AcceptedJump(structureId, sourceRecord.SolarSystemId, result);
        return result;
    }

    private async Task<bool> StructureServiceOnlineAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken)
    {
        var services = await structures.ServicesAsync(connection, transaction, structureId, cancellationToken);
        return services.GetValueOrDefault(JumpBridgeServiceId) == ServiceState.Online;
    }

    private async Task<bool> HasProximityConflictAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, StructureRecord structure, CancellationToken cancellationToken)
    {
        var records = await structures.ListInSystemAsync(connection, transaction, structure.SolarSystemId, cancellationToken);
        await dogma.EnsureTypesAsync(records.Select(record => record.TypeId), cancellationToken);
        var ownType = dogma.Type(structure.TypeId);
        if (ownType is null || ownType.CategoryId != 65)
            return false;
        var ownRadius = StructureAnchorAuthority.Radius(ownType);
        var ownDistance = StructureAnchorAuthority.DeploymentDistance(ownType);
        foreach (var candidate in records)
        {
            if (candidate.Snapshot.StructureId == structure.Snapshot.StructureId || candidate.Snapshot.DestroyedMs > 0 ||
                dogma.Type(candidate.TypeId) is not { CategoryId: 65 } candidateType)
                continue;
            var candidateDistance = StructureAnchorAuthority.DeploymentDistance(candidateType);
            var minimumDistance = Math.Min(ownDistance, candidateDistance);
            if (minimumDistance <= 0)
                continue;
            var threshold = minimumDistance + ownRadius + StructureAnchorAuthority.Radius(candidateType);
            if (Distance(structure.X, structure.Y, structure.Z, candidate.X, candidate.Y, candidate.Z) < threshold)
                return true;
        }
        return false;
    }

    private static double Distance(double ax, double ay, double az, double bx, double by, double bz)
    {
        var dx = ax - bx;
        var dy = ay - by;
        var dz = az - bz;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static MachoWrappedException Notify(string text) =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
}
