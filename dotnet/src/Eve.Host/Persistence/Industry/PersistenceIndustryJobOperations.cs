using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

public sealed partial class PersistenceIndustryJobOperations : Eve.Services.Core.IIndustryJobOperations
{

    private const int IndustryJobTaxReferenceType = 42;
    private const int ManufacturingActivity = 1;
    private const int MaterialResearchActivity = 3;
    private const int TimeResearchActivity = 4;
    private const int CopyingActivity = 5;
    private const int InventionActivity = 8;
    private const long FactoryManagerRole = 1024;
    private const long DirectorRole = 1;

    private readonly Npgsql.NpgsqlDataSource _source;
    private readonly string _schema;
    private readonly Eve.Inventory.PgItemStore _items;
    private readonly Eve.Wallet.WalletTransactions _wallets;
    private readonly Eve.Skills.PgSkillStore _skills;
    private readonly Eve.Sde.SdeReader _sde;
    private readonly Eve.Corporations.CorporationStore _corporations;
    private readonly Eve.Events.EventBus _events;
    private readonly Eve.Universe.StationCatalog _stations;
    private readonly Func<long, long, long, long, CancellationToken, ValueTask<IndustryFacility?>> _resolveFacility;
    private readonly Func<long> _nowUnixMs;

    public PersistenceIndustryJobOperations(
        Npgsql.NpgsqlDataSource source,
        string schema,
        Eve.Inventory.PgItemStore items,
        Eve.Wallet.WalletTransactions wallets,
        Eve.Skills.PgSkillStore skills,
        Eve.Sde.SdeReader sde,
        Eve.Corporations.CorporationStore corporations,
        Eve.Events.EventBus events,
        Eve.Universe.StationCatalog stations,
        Func<long, long, long, long, CancellationToken, ValueTask<IndustryFacility?>> resolveFacility,
        Func<long> nowUnixMs)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("Not a valid industry schema.", nameof(schema));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _schema = schema;
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _wallets = wallets ?? throw new ArgumentNullException(nameof(wallets));
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        _sde = sde ?? throw new ArgumentNullException(nameof(sde));
        _corporations = corporations ?? throw new ArgumentNullException(nameof(corporations));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _stations = stations ?? throw new ArgumentNullException(nameof(stations));
        _resolveFacility = resolveFacility ?? throw new ArgumentNullException(nameof(resolveFacility));
        _nowUnixMs = nowUnixMs ?? throw new ArgumentNullException(nameof(nowUnixMs));
    }

    private async Task<long> CreateOutputsAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        System.Text.Json.Nodes.JsonObject job,
        System.Text.Json.Nodes.JsonArray products,
        List<long> outputIds,
        CancellationToken cancellationToken)
    {
        var activityId = Int(job["activityID"]);
        var ownerId = Long(job["ownerID"]);
        var runs = Int(job["runs"]);
        var successes = 0L;
        var selectedProducts = products.OfType<System.Text.Json.Nodes.JsonObject>().ToArray();
        if (selectedProducts.Length == 0 && activityId == CopyingActivity)
            selectedProducts = [new System.Text.Json.Nodes.JsonObject
            {
                ["typeID"] = Int(job["blueprintTypeID"]),
                ["quantity"] = 1,
                ["probability"] = 1d,
            }];
        var seedText = (job["activitySnapshot"] as System.Text.Json.Nodes.JsonObject)?["rollSeed"]?.ToString();
        var seed = 0;
        if (!string.IsNullOrEmpty(seedText))
        {
            var bytes = Convert.FromBase64String(seedText);
            if (bytes.Length >= sizeof(int))
                seed = BitConverter.ToInt32(bytes, 0);
        }
        var random = new Random(seed);
        foreach (var product in selectedProducts)
        {
            var typeId = Int(product["typeID"]);
            var perRun = Long(product["quantity"], 1);
            var probability = Math.Clamp(Double(product["probability"], 1), 0, 1);
            if (typeId <= 0 || perRun <= 0)
                throw InvalidJob();
            long successful;
            if (activityId == InventionActivity)
            {
                successful = 0;
                for (var i = 0; i < runs; i++)
                    if (random.NextDouble() < probability)
                        successful++;
            }
            else
            {
                successful = runs;
            }
            if (activityId == InventionActivity)
                successes += successful;
            var blueprintProduct = activityId is CopyingActivity or InventionActivity;
            if (blueprintProduct)
            {
                for (long index = 0; index < successful; index++)
                {
                    var id = await _items.CreateAsync(connection, transaction,
                        new Eve.Inventory.NewItem(
                            typeId, ownerId, Long(job["outputLocationID"]), Int(job["outputFlagID"]),
                            1, true, BlueprintCopy: true, BlueprintRuns: Int(job["licensedRuns"]),
                            MaterialEfficiency: Int((job["activitySnapshot"] as System.Text.Json.Nodes.JsonObject)?["blueprintMaterialEfficiency"]),
                            TimeEfficiency: Int((job["activitySnapshot"] as System.Text.Json.Nodes.JsonObject)?["blueprintTimeEfficiency"])),
                        cancellationToken);
                    outputIds.Add(id);
                }
            }
            else
            {
                var quantity = checked(perRun * successful);
                if (quantity <= 0)
                    continue;
                var id = await _items.CreateAsync(connection, transaction,
                    new Eve.Inventory.NewItem(typeId, ownerId, Long(job["outputLocationID"]), Int(job["outputFlagID"]), quantity),
                    cancellationToken);
                outputIds.Add(id);
                successes = Math.Max(successes, successful);
            }
        }
        return activityId == InventionActivity ? successes : runs;
    }

    private async Task<ActivityRecipe> ReadRecipeAsync(
        int blueprintTypeId,
        int activityId,
        CancellationToken cancellationToken)
    {
        var activities = await _sde.ReadByFieldAsync("industryActivity", "typeID", [blueprintTypeId], cancellationToken);
        var activity = activities.FirstOrDefault(row => Int(row.Doc, "activityID") == activityId);
        if (activity is null)
            throw InvalidJob();
        var materials = (await _sde.ReadByFieldAsync(
                "industryActivityMaterials", "typeID", [blueprintTypeId], cancellationToken))
            .Where(row => Int(row.Doc, "activityID") == activityId)
            .Select(row => new Material(Int(row.Doc, "materialTypeID"), Long(row.Doc, "quantity")))
            .Where(row => row.TypeId > 0 && row.Quantity > 0)
            .ToArray();
        var products = (await _sde.ReadByFieldAsync(
                "industryActivityProducts", "typeID", [blueprintTypeId], cancellationToken))
            .Where(row => Int(row.Doc, "activityID") == activityId)
            .Select(row => new Product(Int(row.Doc, "productTypeID"),
                Long(row.Doc, "quantity", 1), Double(row.Doc, "probability", 1)))
            .Where(row => row.TypeId > 0 && row.Quantity > 0)
            .ToArray();
        var skills = (await _sde.ReadByFieldAsync(
                "industryActivitySkills", "typeID", [blueprintTypeId], cancellationToken))
            .Where(row => Int(row.Doc, "activityID") == activityId)
            .Select(row => new SkillRequirement(Int(row.Doc, "skillID"), Int(row.Doc, "level")))
            .Where(row => row.TypeId > 0 && row.Level > 0)
            .ToArray();
        var time = checked((int)Math.Clamp(Long(activity.Doc, "time"), 1, int.MaxValue));
        if (materials.Length == 0 && products.Length == 0 &&
            activityId is not (MaterialResearchActivity or TimeResearchActivity))
            throw InvalidJob();
        return new ActivityRecipe(time, materials, products, skills);
    }

    private async Task<Dictionary<int, decimal>> ReadMarketPricesAsync(
        IReadOnlyCollection<int> typeIds,
        CancellationToken cancellationToken)
    {
        var rows = await _sde.ReadAsync("marketPrices", typeIds.Distinct().Select(id => (long)id), cancellationToken);
        var prices = new Dictionary<int, decimal>();
        foreach (var row in rows)
        {
            var typeId = checked((int)row.Key);
            var price = Decimal(row.Doc, "adjustedPrice");
            if (price <= 0)
                price = Decimal(row.Doc, "averagePrice");
            if (price > 0)
                prices[typeId] = price;
        }
        return prices;
    }

    private async Task<decimal> ReadCostIndexAsync(
        long solarSystemId,
        int activityId,
        CancellationToken cancellationToken)
    {
        var rows = await _sde.ReadAsync("industrySystems", [solarSystemId], cancellationToken);
        if (rows.Count == 0 ||
            !rows[0].Doc.TryGetProperty("costIndices", out var indices) ||
            indices.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw InvalidJob();
        foreach (var index in indices.EnumerateArray())
        {
            if (Int(index, "activityID") != activityId)
                continue;
            var rate = Decimal(index, "costIndex");
            if (rate > 1)
                rate /= 100m;
            return Math.Clamp(rate, 0, 1);
        }
        throw InvalidJob();
    }


    public PyValue ReadMapIndustryCostModifier(
        int activityId,
        IReadOnlyList<Eve.Sde.SdeRow> systems)
    {
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var system in systems)
        {
            if (!system.Doc.TryGetProperty("costIndices", out var costIndices) ||
                costIndices.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;

            foreach (var index in costIndices.EnumerateArray())
            {
                if (Int(index, "activityID") != activityId)
                    continue;
                var rate = Decimal(index, "costIndex");
                if (rate > 1)
                    rate /= 100m;
                entries.Add(new(new PyInt(system.Key), new PyReal((double)Math.Clamp(rate, 0, 1))));
                break;
            }
        }
        return new PyDict(entries);
    }

    public async Task<PyValue> ReadMapIndustryJobsOverLast24HoursAsync(
        int activityId,
        long nowUnixMs,
        CancellationToken cancellationToken)
    {
        const long fileTimeEpochOffset = 116444736000000000L;
        var nowFileTime = checked(fileTimeEpochOffset + nowUnixMs * 10_000L);
        var cutoff = nowFileTime - TimeSpan.TicksPerDay;
        var futureGrace = nowFileTime + TimeSpan.TicksPerMinute;

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand($"""
            SELECT solar_system_id, COUNT(*)::bigint
              FROM {_schema}.jobs
             WHERE solar_system_id > 0
               AND start_date_filetime >= $1
               AND start_date_filetime <= $2
               AND ($3 <= 0 OR activity_id = $3)
             GROUP BY solar_system_id
             ORDER BY solar_system_id
            """, connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = cutoff });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = futureGrace });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = activityId });

        var rows = new List<PyValue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(Py.Tuple(new PyInt(reader.GetInt64(0)), new PyInt(reader.GetInt64(1))));
        return Py.Rowset(["solarSystemID", "noOfJobs"], rows);
    }

    private static IReadOnlyList<Product> SelectProducts(
        Eve.Services.Core.IndustryJobInstallSubmission job,
        ActivityRecipe recipe)
    {
        if (job.ActivityId is MaterialResearchActivity or TimeResearchActivity)
            return [];
        if (job.ProductTypeId is { } selected)
        {
            var products = recipe.Products.Where(product => product.TypeId == selected).ToArray();
            if (products.Length == 0)
                throw InvalidJob();
            return products;
        }
        if (recipe.Products.Count == 1)
            return recipe.Products;
        if (job.ActivityId == CopyingActivity && recipe.Products.Count == 0)
            return [new Product(job.BlueprintTypeId, 1, 1)];
        throw InvalidJob();
    }

    private static IReadOnlyDictionary<int, long> RequiredMaterials(
        Eve.Services.Core.IndustryJobInstallSubmission job,
        Eve.Inventory.Item blueprint,
        ActivityRecipe recipe)
    {
        var required = new SortedDictionary<int, long>();
        foreach (var material in recipe.Materials)
        {
            var amount = checked(material.Quantity * job.Runs);
            if (job.ActivityId == ManufacturingActivity && blueprint.MaterialEfficiency > 0)
                amount = checked((long)Math.Ceiling(amount * Math.Max(0, 100 - blueprint.MaterialEfficiency) / 100d));
            required[material.TypeId] = checked(required.GetValueOrDefault(material.TypeId) + amount);
        }
        if (job.OptionalTypeId is { } optionalTypeId)
            required[optionalTypeId] = checked(required.GetValueOrDefault(optionalTypeId) + 1);
        if (job.OptionalTypeId2 is { } optionalTypeId2)
            required[optionalTypeId2] = checked(required.GetValueOrDefault(optionalTypeId2) + 1);
        return required;
    }

    private static long CalculateCost(
        Eve.Services.Core.IndustryJobInstallSubmission job,
        IReadOnlyList<Product> products,
        IReadOnlyDictionary<int, decimal> prices,
        decimal costIndex,
        decimal facilityTax)
    {
        decimal outputValue = 0;
        foreach (var product in products)
        {
            if (!prices.TryGetValue(product.TypeId, out var price) || price <= 0)
                throw InvalidJob();
            outputValue += price * product.Quantity * job.Runs;
        }
        if (products.Count == 0)
        {
            if (!prices.TryGetValue(job.BlueprintTypeId, out var price) || price <= 0)
                throw InvalidJob();
            outputValue = price * job.Runs;
        }
        return checked((long)decimal.Ceiling(outputValue * Math.Clamp(costIndex + facilityTax, 0, 1)));
    }

    private async Task<int> TimeInSecondsAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long characterId,
        Eve.Services.Core.IndustryJobInstallSubmission job,
        ActivityRecipe recipe,
        IndustryFacility facility,
        CancellationToken cancellationToken)
    {
        var modifier = 1d;
        if (job.ActivityId == ManufacturingActivity)
        {
            var industry = await _skills.LevelAsync(connection, transaction, characterId, 3380, cancellationToken);
            var advancedIndustry = await _skills.LevelAsync(connection, transaction, characterId, 3388, cancellationToken);
            modifier = Math.Max(0.1, 1d - 0.04d * industry - 0.03d * advancedIndustry);
        }
        var duration = Math.Ceiling(recipe.TimeSeconds * (double)job.Runs * modifier * facility.TimeMultiplier);
        if (!double.IsFinite(duration) || duration < 1 || duration > int.MaxValue)
            throw InvalidJob();
        return checked((int)duration);
    }

    private async Task ConsumeMaterialsAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        Eve.Services.Core.IndustryJobInstallSubmission job,
        long blueprintId,
        IReadOnlyDictionary<int, long> requirements,
        CancellationToken cancellationToken)
    {
        var items = await _items.LockInLocationAsync(
            connection, transaction, job.InputLocation.LocationId, job.OwnerId,
            job.InputLocation.FlagId, requirements.Keys.ToArray(), cancellationToken);
        foreach (var requirement in requirements)
        {
            var remaining = requirement.Value;
            foreach (var item in items.Where(item => item.TypeId == requirement.Key && item.Id != blueprintId))
            {
                var available = item.Quantity > 0 ? item.Quantity : item.Singleton ? 1 : 0;
                if (available <= 0)
                    continue;
                var used = Math.Min(remaining, available);
                if (used == available)
                    await _items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
                else
                    await _items.SetQuantityAsync(connection, transaction, item.Id, available - used, cancellationToken);
                remaining -= used;
                if (remaining == 0)
                    break;
            }
            if (remaining != 0)
                throw InvalidJob();
        }
    }

    private async Task ValidateActorAsync(
        Eve.Services.Core.IndustryJobActor actor,
        long ownerId,
        Eve.Services.Core.IndustryJobWallet wallet,
        long stationId,
        CancellationToken cancellationToken)
    {
        if (actor.CharacterId <= 0 || ownerId != actor.CharacterId && ownerId != actor.CorporationId ||
            wallet.OwnerId != ownerId && wallet.OwnerId != actor.CharacterId ||
            wallet.AccountKey is < 1000 or > 1006)
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");
        if (wallet.OwnerId == actor.CharacterId && wallet.AccountKey != 1000)
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");
        if (ownerId == actor.CharacterId)
            return;

        var corporation = await _corporations.GetAsync(ownerId, cancellationToken);
        var member = await _corporations.MemberAsync(actor.CharacterId, cancellationToken);
        if (corporation is null || corporation.IsNpc || member is null ||
            member.CorporationId != ownerId || corporation.Id != actor.CorporationId)
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");
        var roles = Eve.Corporations.CorporationRoles.Session(
            corporation, member, stationId > 0 ? stationId : null,
            await _corporations.TitlesAsync(ownerId, cancellationToken)).CorpRole;
        if ((roles & (FactoryManagerRole | DirectorRole)) == 0)
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");
        if (wallet.OwnerId == ownerId &&
            (roles & Eve.Corporations.CorporationRoles.WalletTakeRole(wallet.AccountKey)) == 0)
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");
    }

    private async Task<bool> CanManageExistingJobAsync(
        Eve.Services.Core.IndustryJobActor actor,
        long ownerId,
        long installerId,
        int stationId,
        CancellationToken cancellationToken)
    {
        if (actor.CharacterId <= 0)
            return false;
        if (actor.CharacterId == installerId)
            return true;
        if (ownerId != actor.CorporationId)
            return ownerId == actor.CharacterId && actor.CharacterId == installerId;
        var corporation = await _corporations.GetAsync(ownerId, cancellationToken);
        var member = await _corporations.MemberAsync(actor.CharacterId, cancellationToken);
        if (corporation is null || member is null || member.CorporationId != ownerId)
            return false;
        var roles = Eve.Corporations.CorporationRoles.Session(
            corporation, member, stationId > 0 ? stationId : null,
            await _corporations.TitlesAsync(ownerId, cancellationToken)).CorpRole;
        return (roles & (FactoryManagerRole | DirectorRole)) != 0;
    }

    private JsonObject ActivitySnapshot(
        Eve.Services.Core.IndustryJobInstallSubmission job,
        Eve.Inventory.Item blueprint,
        ActivityRecipe recipe,
        IReadOnlyDictionary<int, long> materials,
        IReadOnlyList<Product> products,
        long cost,
        int duration,
        IndustryFacility facility)
    {
        var materialRows = new System.Text.Json.Nodes.JsonArray();
        foreach (var material in materials)
            materialRows.Add(new System.Text.Json.Nodes.JsonObject { ["typeID"] = material.Key, ["quantity"] = material.Value });
        var productRows = new System.Text.Json.Nodes.JsonArray();
        foreach (var product in products)
            productRows.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["typeID"] = product.TypeId,
                ["quantity"] = product.Quantity,
                ["probability"] = product.Probability,
            });
        return new JsonObject
        {
            ["activityID"] = job.ActivityId,
            ["materials"] = materialRows,
            ["products"] = productRows,
            ["blueprintRuns"] = blueprint.BlueprintRuns,
            ["blueprintMaterialEfficiency"] = blueprint.MaterialEfficiency,
            ["blueprintTimeEfficiency"] = blueprint.TimeEfficiency,
            ["blueprintCopy"] = blueprint.BlueprintCopy,
            ["facilityTaxRate"] = facility.TaxRate,
            ["timeMultiplier"] = facility.TimeMultiplier,
            ["costIsk"] = cost,
            ["timeInSeconds"] = duration,
            ["rollSeed"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            ["requiredSkills"] = new System.Text.Json.Nodes.JsonArray(recipe.Skills.Select(skill =>
                (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
                {
                    ["typeID"] = skill.TypeId,
                    ["level"] = skill.Level,
                }).ToArray()),
        };
    }

    private static string Fingerprint(Eve.Services.Core.IndustryJobInstallSubmission job)
    {
        var canonical = string.Join("|",
            job.ActivityId, job.BlueprintId, job.BlueprintTypeId, job.BlueprintCopy,
            job.BlueprintLocationId, job.BlueprintLocationFlagId, job.FacilityId, job.OwnerId,
            job.SolarSystemId, job.StationId, job.InputLocation.LocationId, job.InputLocation.FlagId,
            job.OutputLocation.LocationId, job.OutputLocation.FlagId, job.Runs, job.LicensedRuns,
            job.ProductTypeId, job.OptionalTypeId, job.OptionalTypeId2,
            job.Wallet.OwnerId, job.Wallet.AccountKey);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private Task PublishChangedAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long jobId,
        long ownerId,
        long blueprintId,
        long installerId,
        int status,
        long successfulRuns,
        string operationId,
        CancellationToken cancellationToken) =>
        _events.PublishAsync(
            connection, transaction,
            Eve.Services.Core.IndustryJobEvents.Changed,
            new Eve.Services.Core.IndustryJobChangedEvent(
                jobId, ownerId, blueprintId, installerId, status, successfulRuns, operationId),
            operationId,
            cancellationToken);

private void Announce(Eve.Wallet.WalletSettlement? settlement)
    {
        if (settlement is null)
            return;
        foreach (var entry in settlement.Entries)
            _wallets.AnnounceCommitted(new Eve.Wallet.WalletBalanceChange(
                entry.OwnerId, entry.AccountKey, entry.Balance));
    }

    private static void Add(Npgsql.NpgsqlCommand command, params object?[] values)
    {
        foreach (var value in values)
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = value ?? DBNull.Value });
    }

    private async Task<(long JobId, string Fingerprint)?> FindInstallReplayAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            $"SELECT job_id, install_request_fingerprint FROM {_schema}.jobs WHERE install_operation_id = $1",
            connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = operationId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private async Task<(long JobId, string Fingerprint)?> ReadInstallReplayAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var command = new Npgsql.NpgsqlCommand(
            $"SELECT job_id, install_request_fingerprint FROM {_schema}.jobs WHERE install_operation_id = $1 FOR UPDATE",
            connection, transaction);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = operationId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private async Task<JsonObject?> ReadJobAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long jobId,
        CancellationToken cancellationToken)
    {
        await using var command = new Npgsql.NpgsqlCommand($"""
            SELECT job_id, activity_id, blueprint_item_id, blueprint_type_id, blueprint_copy,
                   blueprint_location_id, blueprint_location_flag_id, facility_id, owner_id, status,
                   installer_id, completed_character_id, solar_system_id, station_id,
                   start_date_filetime, end_date_filetime, pause_date_filetime, runs, licensed_runs,
                   successful_runs, cost_isk, time_in_seconds, probability, product_type_id,
                   optional_type_id, optional_type_id2, output_location_id, output_flag_id,
                   payment_owner_id, payment_account_key, activity_snapshot, output_item_ids
              FROM {_schema}.jobs WHERE job_id = $1 FOR UPDATE
            """, connection, transaction);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = jobId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return ReadWireJob(reader);
    }

    private static JsonObject ReadWireJob(Npgsql.NpgsqlDataReader reader)
    {
        using var snapshot = reader.GetFieldValue<System.Text.Json.JsonDocument>(30);
        var outputIds = reader.GetFieldValue<long[]>(31);
        var activitySnapshot = JsonNode.Parse(snapshot.RootElement.GetRawText()) as JsonObject ?? new JsonObject();
        return new JsonObject
        {
            ["jobID"] = reader.GetInt64(0),
            ["activityID"] = reader.GetInt32(1),
            ["blueprintID"] = reader.GetInt64(2),
            ["blueprintTypeID"] = reader.GetInt32(3),
            ["blueprintCopy"] = reader.GetBoolean(4),
            ["blueprintLocationID"] = reader.GetInt64(5),
            ["blueprintLocationFlagID"] = reader.GetInt32(6),
            ["facilityID"] = reader.GetInt64(7),
            ["ownerID"] = reader.GetInt64(8),
            ["status"] = reader.GetInt32(9),
            ["installerID"] = reader.GetInt64(10),
            ["completedCharacterID"] = reader.GetInt64(11),
            ["solarSystemID"] = reader.GetInt64(12),
            ["stationID"] = reader.GetInt64(13),
            ["startDate"] = reader.GetInt64(14),
            ["endDate"] = reader.GetInt64(15),
            ["pauseDate"] = reader.IsDBNull(16) ? null : JsonValue.Create(reader.GetInt64(16)),
            ["runs"] = reader.GetInt32(17),
            ["licensedRuns"] = reader.GetInt32(18),
            ["successfulRuns"] = reader.GetInt64(19),
            ["totalCost"] = reader.GetInt64(20),
            ["timeInSeconds"] = reader.GetInt32(21),
            ["probability"] = reader.GetDouble(22),
            ["productTypeID"] = reader.IsDBNull(23) ? null : JsonValue.Create(reader.GetInt32(23)),
            ["optionalTypeID"] = reader.IsDBNull(24) ? null : JsonValue.Create(reader.GetInt32(24)),
            ["optionalTypeID2"] = reader.IsDBNull(25) ? null : JsonValue.Create(reader.GetInt32(25)),
            ["outputLocationID"] = reader.GetInt64(26),
            ["outputFlagID"] = reader.GetInt32(27),
            ["paymentOwnerID"] = reader.GetInt64(28),
            ["paymentAccountKey"] = reader.GetInt32(29),
            ["activitySnapshot"] = activitySnapshot,
            ["outputItemIDs"] = new JsonArray(outputIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        };
    }

    private static long FileTime(long unixMs) => checked(116444736000000000L + unixMs * 10_000L);
    private static string Key(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static Eve.Services.Core.IndustryJobUserErrorException InvalidJob() =>
        new("IndustryInvalidJob");

    private static long Long(JsonNode? value, long fallback = 0) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : fallback;
    private static int Int(JsonNode? value, int fallback = 0) =>
        int.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : fallback;
    private static double Double(JsonNode? value, double fallback = 0) =>
        double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) ? result : fallback;
    private static decimal Decimal(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var value))
            return 0;
        return value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetDecimal(out var result)
            ? result
            : decimal.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result) ? result : 0;
    }
    private static long Long(JsonElement row, string field, long fallback = 0) =>
        row.TryGetProperty(field, out var value) && value.TryGetInt64(out var result) ? result : fallback;
    private static int Int(JsonElement row, string field, int fallback = 0) =>
        row.TryGetProperty(field, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    private static double Double(JsonElement row, string field, double fallback = 0) =>
        row.TryGetProperty(field, out var value) && value.TryGetDouble(out var result) && double.IsFinite(result)
            ? result
            : fallback;
}
