using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Planets;
using Eve.Services;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Services.Planets;

/// <summary>
/// The customs offices of the planets (the client's importExportUI.py and planetInfoSvc): the office is an item of the server's
/// inventory (type 2233, id 1.2e12 + planet id, at the planet's system, owned by an NPC owner) and, in space, an orbital of its planet
/// (the simulation's ball, <see cref="PlaceOffice"/>). Imports take the pilot's stacks out of the office hangar onto the launchpad;
/// exports put the launchpad's commodities into the office hangar. The fees are charged in the same transaction (ref 96 import, 97 export).
/// </summary>
public sealed partial class PlanetMgrService
{
    /// <summary>The owner label of the server's customs offices (an NPC owner, not a player's corporation).</summary>
    private const long CustomsOfficeOwner = 1000125;
    /// <summary>The hangar flag of an office's stacks (the client's flagHangar).</summary>
    private const int HangarFlag = 4;
    private const int RefPlanetaryImportTax = 96;
    private const int RefPlanetaryExportTax = 97;

    public async ValueTask<bool> IsOfficeAsync(long officeId, CancellationToken cancellationToken)
    {
        if (Customs.PlanetOf(officeId) is null) return false;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {inventorySchema}.items WHERE id = $1 AND type_id = $2", connection)
        {
            Parameters = { new() { Value = officeId }, new() { Value = Customs.OfficeTypeId } },
        };
        return (long)(await command.ExecuteScalarAsync(cancellationToken))! > 0;
    }

    /// <summary>The planet's customs office item, created on first use (its id is the planet's office id).</summary>
    private async Task EnsureOfficeAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long planetId, long solarSystemId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {inventorySchema}.items (id, type_id, owner_id, location_id, flag, quantity, singleton, custom_name, blueprint_copy, blueprint_locked,
                blueprint_runs, material_efficiency, time_efficiency, custom_info)
            OVERRIDING SYSTEM VALUE VALUES ($1, $2, $3, $4, 0, 1, true, NULL, false, false, NULL, 0, 0, NULL)
            ON CONFLICT (id) DO UPDATE SET type_id = EXCLUDED.type_id WHERE items.type_id <> EXCLUDED.type_id
            """, connection, transaction)
        {
            Parameters =
            {
                new() { Value = Customs.OfficeId(planetId) }, new() { Value = Customs.OfficeTypeId },
                new() { Value = CustomsOfficeOwner }, new() { Value = solarSystemId },
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Puts a planet's customs office in space: the simulation's ball of it, an orbital of the planet, at <see cref="Customs.OfficePosition"/>. The
    /// simulation keeps a ball that is in space as it is, so this may be called again. Nothing is put without a simulation.
    /// </summary>
    private void PlaceOffice(long planetId, PlanetBody body)
    {
        if (simulation is null)
        {
            Console.Error.WriteLine($"eve-host: the customs office of planet {planetId} is not put in space: no simulation");
            return;
        }
        Console.Error.WriteLine($"eve-host: the customs office of planet {planetId} is put in space in system {body.SolarSystemId}");
        simulation.PlaceStructure(new SimPlaceStructure(Customs.OfficeId(planetId), (int)body.SolarSystemId, "Customs Office", Customs.OfficeTypeId, Customs.OfficeGroupId,
            Customs.OfficePosition(body), Customs.OfficeRadius, CustomsOfficeOwner, 0, 0,
            // no hit points and no resonances of its own (twelve values: four kinds in each of the three layers, all one)
            new SimStructureHealth(0, 0, 0, [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], 0, [0, 0, 0], [0, 0, 0]),
            new SimStructureStatus(StructureState.ShieldVulnerable, UpkeepState.FullPower, null, null, null), planetId));
    }

    /// <summary>
    /// Puts the customs offices in space: all of them at the host's start (a simulation starts with none), and those of a system when a pilot
    /// enters it (the simulation drops a scene that has no pilot, and its balls with it). An office whose planet is not in the catalog is left
    /// out and reported; the others still go up.
    /// </summary>
    public async Task PlaceOfficesAsync(int? solarSystemId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var planets = new List<long>();
            await using (var connection = await source.OpenConnectionAsync(cancellationToken))
            await using (var command = new NpgsqlCommand($"SELECT id FROM {inventorySchema}.items WHERE type_id = $1 AND ($2::bigint IS NULL OR location_id = $2)", connection)
            {
                Parameters =
                {
                    new() { Value = Customs.OfficeTypeId },
                    new() { Value = (object?)solarSystemId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint },
                },
            })
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    if (Customs.PlanetOf(reader.GetInt64(0)) is { } planetId) planets.Add(planetId);
            }
            foreach (var planetId in planets)
            {
                try
                {
                    PlaceOffice(planetId, await BodyAsync(planetId, cancellationToken));
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"eve-host: the customs office of planet {planetId} was not put in space: {error.Message}");
                }
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"eve-host: the customs offices were not put in space: {error.Message}");
        }
    }

    /// <summary>The office rate the character gets at this planet (creating the office if it does not exist yet); null when the planet has none.</summary>
    public async Task<double?> TaxRateAsync(long officeId, long characterId, CancellationToken cancellationToken)
    {
        if (Customs.PlanetOf(officeId) is not { } planetId) return null;
        var body = await BodyAsync(planetId, cancellationToken);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await EnsureOfficeAsync(connection, null, planetId, body.SolarSystemId, cancellationToken);
        var security = await PlanetCatalogLoader.SecurityAsync(source, body.SolarSystemId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var skill = await skills.LevelAsync(connection, transaction, characterId, Customs.CustomsCodeExpertise, cancellationToken);
        return Customs.Rate(security, skill);
    }

    /// <summary>ImportExportWithPlanet: see <see cref="Eve.Services.Inventory.ICustomsOffices"/>.</summary>
    public async ValueTask ImportExportAsync(ClientSession session, long officeId, long launchpadId, IReadOnlyDictionary<long, long> imports,
        IReadOnlyDictionary<int, long> exports, double taxRate, CancellationToken cancellationToken)
    {
        try
        {
            await ImportExportCoreAsync(session, officeId, launchpadId, imports, exports, taxRate, cancellationToken);
        }
        catch (PlanetRefusal refusal)
        {
            throw MachoErrors.ThrowUserError(refusal.Key);
        }
    }

    private async Task ImportExportCoreAsync(ClientSession session, long officeId, long launchpadId, IReadOnlyDictionary<long, long> imports,
        IReadOnlyDictionary<int, long> exports, double taxRate, CancellationToken cancellationToken)
    {
        var owner = session.CharacterId ?? throw MachoErrors.ThrowUserError("CustomsOfficeAccessDenied");
        var planetId = Customs.PlanetOf(officeId) ?? throw MachoErrors.ThrowUserError("CustomsOfficeNotFound");
        var now = Ticks();
        var body = await BodyAsync(planetId, cancellationToken);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var office = await items.LockAndGetAsync(connection, transaction, officeId, cancellationToken)
            ?? throw MachoErrors.ThrowUserError("CustomsOfficeNotFound");
        if (office.TypeId != Customs.OfficeTypeId || office.LocationId != body.SolarSystemId)
            throw MachoErrors.ThrowUserError("CustomsOfficeNotFound");
        var doc = await store.LockAsync(connection, transaction, owner, planetId, cancellationToken)
            ?? throw MachoErrors.ThrowUserError("CannotManagePlanetWithoutCommandCenter");
        var colony = doc.ToColony(catalog, field);
        colony.RunTo(now);

        // the rate the client saw must be the rate now (the client asks again on TaxChanged)
        var security = await PlanetCatalogLoader.SecurityAsync(source, body.SolarSystemId, cancellationToken);
        var rate = Customs.Rate(security, await skills.LevelAsync(connection, transaction, owner, Customs.CustomsCodeExpertise, cancellationToken));
        if (Math.Abs(rate - taxRate) > 1e-9) throw MachoErrors.ThrowUserError("TaxChanged");

        var pad = colony.Pins.GetValueOrDefault(launchpadId) ?? throw MachoErrors.ThrowUserError("CannotImportEndpointNotFound");
        var imported = new Dictionary<int, long>();
        foreach (var (itemId, quantity) in imports)
        {
            var item = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken)
                ?? throw MachoErrors.ThrowUserError("CannotImportEndpointNotFound");
            if (item.LocationId != officeId || item.OwnerId != owner || item.Flag != HangarFlag)
                throw MachoErrors.ThrowUserError("CannotImportEndpointNotFound");
            if (quantity < 1 || quantity > item.Quantity)
                throw MachoErrors.ThrowUserError("RouteFailedValidationExpeditedSourceLacksCommodityQty");
            colony.ImportToLaunchpad(launchpadId, item.TypeId, quantity);
            imported[item.TypeId] = imported.GetValueOrDefault(item.TypeId) + quantity;
            if (quantity == item.Quantity) await items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
            else await items.SetQuantityAsync(connection, transaction, item.Id, item.Quantity - quantity, cancellationToken);
        }
        foreach (var (type, quantity) in exports)
        {
            colony.ExportFromLaunchpad(launchpadId, type, quantity);
            await AddToOfficeAsync(connection, transaction, officeId, owner, type, quantity, cancellationToken);
        }

        var (importTax, exportTax) = Customs.Taxes(catalog, pad.TypeId, imported, exports, rate);
        await ChargeAsync(connection, transaction, owner, importTax, RefPlanetaryImportTax, now, cancellationToken);
        await ChargeAsync(connection, transaction, owner, exportTax, RefPlanetaryExportTax, now, cancellationToken);
        await store.SaveAsync(connection, transaction, owner, planetId, ColonyDocument.From(colony), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await notify(owner, new PyInt(planetId));
    }

    /// <summary>Adds a commodity to the pilot's stack in the office hangar (a new stack when there is none).</summary>
    private async Task AddToOfficeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long officeId, long owner, int typeId, long quantity, CancellationToken cancellationToken)
    {
        await using var find = new NpgsqlCommand($"""
            SELECT id, quantity FROM {inventorySchema}.items
            WHERE location_id = $1 AND owner_id = $2 AND type_id = $3 AND flag = $4 AND NOT singleton ORDER BY id LIMIT 1 FOR UPDATE
            """, connection, transaction)
        {
            Parameters = { new() { Value = officeId }, new() { Value = owner }, new() { Value = typeId }, new() { Value = HangarFlag } },
        };
        await using var reader = await find.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var have = reader.GetInt64(1);
            await reader.CloseAsync();
            await items.SetQuantityAsync(connection, transaction, id, have + quantity, cancellationToken);
            return;
        }
        await reader.CloseAsync();
        await items.CreateAsync(connection, transaction, new NewItem(typeId, owner, officeId, HangarFlag, quantity), cancellationToken);
    }

    /// <summary>A fee, charged to the wallet in the transaction (whole cents; refused when the balance would be negative).</summary>
    private async Task ChargeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long owner, double fee, int reference, long now, CancellationToken cancellationToken)
    {
        var cents = Math.Round((decimal)fee, 2);
        if (cents <= 0) return;
        var balance = await wallets.AdjustAsync(connection, transaction, owner, -cents, 0, reference, "planetary customs fee", now,
            $"pi-customs:{owner}:{reference}:{now}:{Guid.NewGuid():N}", cancellationToken);
        if (balance < 0) throw MachoErrors.ThrowUserError("CannotBuildNotEnoughCash");
    }
}
