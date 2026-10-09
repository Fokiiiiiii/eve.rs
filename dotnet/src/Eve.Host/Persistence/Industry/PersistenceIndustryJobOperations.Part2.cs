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
    public sealed record IndustryFacility(
        long FacilityId, long SolarSystemId, long OwnerId, decimal TaxRate, double TimeMultiplier, bool IsOnline);

    private sealed record Material(int TypeId, long Quantity);

    private sealed record Product(int TypeId, long Quantity, double Probability);

    private sealed record SkillRequirement(int TypeId, int Level);

    private sealed record ActivityRecipe(
        int TimeSeconds,
        IReadOnlyList<Material> Materials,
        IReadOnlyList<Product> Products,
        IReadOnlyList<SkillRequirement> Skills);

    private sealed record PreparedInstall(
        ActivityRecipe Recipe,
        IReadOnlyDictionary<int, long> Materials,
        IReadOnlyList<Product> Products,
        IndustryFacility Facility,
        long CostIsk,
        int TimeInSeconds,
        JsonObject Snapshot,
        string Fingerprint);

    private sealed record WalletNotice(long OwnerId, int AccountKey, decimal Balance);

    public async ValueTask<System.Text.Json.Nodes.JsonObject?> ReadJobsAsync(CancellationToken cancellationToken)
    {
        var jobs = new System.Text.Json.Nodes.JsonObject();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand($"""
            SELECT job_id, activity_id, blueprint_item_id, blueprint_type_id, blueprint_copy,
                   blueprint_location_id, blueprint_location_flag_id, facility_id, owner_id, status,
                   installer_id, completed_character_id, solar_system_id, station_id,
                   start_date_filetime, end_date_filetime, pause_date_filetime, runs, licensed_runs,
                   successful_runs, cost_isk, time_in_seconds, probability, product_type_id,
                   optional_type_id, optional_type_id2, output_location_id, output_flag_id,
                   payment_owner_id, payment_account_key, activity_snapshot, output_item_ids
              FROM {_schema}.jobs
             ORDER BY job_id
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            jobs[Key(reader.GetInt64(0))] = ReadWireJob(reader);
        return new System.Text.Json.Nodes.JsonObject { ["jobs"] = jobs };
    }

    public async ValueTask<long> InstallJobAsync(
        Eve.Services.Core.IndustryJobInstallRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Context.OperationId))
            throw InvalidJob();
        var fingerprint = Fingerprint(request.Job);
        try
        {
            return await InstallOnceAsync(request, fingerprint, cancellationToken);
        }
        catch (Npgsql.PostgresException error) when (error.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            var replay = await FindInstallReplayAsync(request.Context.OperationId, cancellationToken);
            if (replay is null)
                throw;
            if (!StringComparer.Ordinal.Equals(replay.Value.Fingerprint, fingerprint))
                throw InvalidJob();
            return replay.Value.JobId;
        }
    }

    private async ValueTask<long> InstallOnceAsync(
        Eve.Services.Core.IndustryJobInstallRequest request,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var job = request.Job;
        await ValidateActorAsync(request.Context.Actor, job.OwnerId, job.Wallet, job.StationId, cancellationToken);
        var facility = await _resolveFacility(
            job.FacilityId, job.StationId, job.SolarSystemId, request.Context.Actor.CharacterId, cancellationToken);
        if (facility is null || !facility.IsOnline || facility.FacilityId != job.FacilityId ||
            facility.SolarSystemId != job.SolarSystemId || facility.OwnerId <= 0 ||
            !double.IsFinite(facility.TimeMultiplier) || facility.TimeMultiplier <= 0)
            throw InvalidJob();

        var recipe = await ReadRecipeAsync(job.BlueprintTypeId, job.ActivityId, cancellationToken);
        var products = SelectProducts(job, recipe);

        var priceTypes = products.Count > 0
            ? products.Select(product => product.TypeId).Distinct().ToArray()
            : [job.BlueprintTypeId];
        var prices = await ReadMarketPricesAsync(priceTypes, cancellationToken);
        var costIndex = await ReadCostIndexAsync(job.SolarSystemId, job.ActivityId, cancellationToken);

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var replay = await ReadInstallReplayAsync(connection, transaction, request.Context.OperationId, cancellationToken);
        if (replay is not null)
        {
            if (!StringComparer.Ordinal.Equals(replay.Value.Fingerprint, fingerprint))
                throw InvalidJob();
            await transaction.CommitAsync(cancellationToken);
            return replay.Value.JobId;
        }

        var blueprint = await _items.LockAndGetAsync(connection, transaction, job.BlueprintId, cancellationToken);
        if (blueprint is null || blueprint.OwnerId != job.OwnerId || blueprint.TypeId != job.BlueprintTypeId ||
            blueprint.LocationId != job.BlueprintLocationId || blueprint.Flag != job.BlueprintLocationFlagId ||
            !blueprint.Singleton || blueprint.BlueprintCopy != job.BlueprintCopy || blueprint.BlueprintLocked)
            throw InvalidJob();
        if (blueprint.BlueprintCopy &&
            (blueprint.BlueprintRuns is not { } remainingRuns || remainingRuns < job.Runs))
            throw InvalidJob();
        if (job.ActivityId == CopyingActivity && job.LicensedRuns <= 0)
            throw InvalidJob();
        var requiredMaterials = RequiredMaterials(job, blueprint, recipe);

        foreach (var requirement in recipe.Skills)
        {
            var level = await _skills.LevelAsync(
                connection, transaction, request.Context.Actor.CharacterId, requirement.TypeId, cancellationToken);
            if (level < requirement.Level)
                throw InvalidJob();
        }

        var duration = await TimeInSecondsAsync(
            connection, transaction, request.Context.Actor.CharacterId, job, recipe, facility, cancellationToken);
        var cost = CalculateCost(job, products, prices, costIndex, facility.TaxRate);
        await ConsumeMaterialsAsync(
            connection, transaction, job, blueprint.Id, requiredMaterials, cancellationToken);
        await _items.SetJobBlueprintLockedAsync(
            connection, transaction, job.OwnerId, blueprint.Id, blueprint.TypeId,
            blueprint.LocationId, blueprint.Flag, blueprint.BlueprintCopy, true, cancellationToken);

        var now = _nowUnixMs();
        var startFileTime = FileTime(now);
        var endFileTime = FileTime(checked(now + (long)duration * 1000));
        var snapshot = ActivitySnapshot(job, blueprint, recipe, requiredMaterials, products, cost, duration, facility);
        var snapshotJson = snapshot.ToJsonString();
        Eve.Wallet.WalletSettlement? settlement = null;
        if (cost > 0)
        {
            settlement = await _wallets.AdjustAsync(
                connection, transaction,
                new Eve.Wallet.WalletAccount(job.Wallet.OwnerId, job.Wallet.AccountKey),
                -cost, facility.OwnerId, IndustryJobTaxReferenceType,
                "Industry job installation", now, request.Context.OperationId,
                "industry-job-install", cancellationToken,
                blueprint.Id, blueprint.TypeId);
        }

        var insert = new Npgsql.NpgsqlCommand($"""
            INSERT INTO {_schema}.jobs (
                install_operation_id, install_request_fingerprint, activity_id, blueprint_item_id,
                blueprint_type_id, blueprint_copy, blueprint_location_id, blueprint_location_flag_id,
                facility_id, owner_id, installer_id, completed_character_id, solar_system_id, station_id,
                input_location_id, input_flag_id, output_location_id, output_flag_id, status,
                start_date_filetime, end_date_filetime, runs, licensed_runs, successful_runs, cost_isk,
                time_in_seconds, probability, product_type_id, optional_type_id, optional_type_id2,
                payment_owner_id, payment_account_key, activity_snapshot
            )
            VALUES (
                $1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,0,$12,$13,$14,$15,$16,$17,1,
                $18,$19,$20,$21,0,$22,$23,$24,$25,$26,$27,$28,$29::jsonb
            )
            RETURNING job_id
            """, connection, transaction);
        Add(insert,
            request.Context.OperationId, fingerprint, job.ActivityId, blueprint.Id, blueprint.TypeId,
            blueprint.BlueprintCopy, blueprint.LocationId, blueprint.Flag, job.FacilityId, job.OwnerId,
            request.Context.Actor.CharacterId, job.SolarSystemId, job.StationId,
            job.InputLocation.LocationId, job.InputLocation.FlagId,
            job.OutputLocation.LocationId, job.OutputLocation.FlagId,
            startFileTime, endFileTime, job.Runs, job.LicensedRuns, cost, duration,
            products.Count == 0 ? 1d : products.Max(product => product.Probability),
            (object?)job.ProductTypeId ?? DBNull.Value,
            (object?)job.OptionalTypeId ?? DBNull.Value,
            (object?)job.OptionalTypeId2 ?? DBNull.Value,
            job.Wallet.OwnerId, job.Wallet.AccountKey, snapshotJson);
        var jobId = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
        await PublishChangedAsync(connection, transaction, jobId, job.OwnerId, blueprint.Id,
            request.Context.Actor.CharacterId, 1, 0, request.Context.OperationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Announce(settlement);
        return jobId;
    }

    public async ValueTask CompleteJobAsync(
        Eve.Services.Core.IndustryJobAction request,
        CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await CompleteInTransactionAsync(connection, transaction, request, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask CompleteManyJobsAsync(
        Eve.Services.Core.IndustryJobBatch request,
        CancellationToken cancellationToken)
    {
        if (request.Jobs.Count == 0)
            return;
        if (request.Jobs.Select(job => job.JobId).Distinct().Count() != request.Jobs.Count)
            throw InvalidJob();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var job in request.Jobs)
            await CompleteInTransactionAsync(connection, transaction, job, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask CancelJobAsync(
        Eve.Services.Core.IndustryJobAction request,
        CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await ReadJobAsync(connection, transaction, request.JobId, cancellationToken);
        if (row is null)
            throw InvalidJob();
        var status = Int(row["status"]);
        if (status == 101)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (status >= 100 || Long(row["solarSystemID"]) != request.SolarSystemId)
            throw InvalidJob();
        var ownerId = Long(row["ownerID"]);
        var installerId = Long(row["installerID"]);
        if (!await CanManageExistingJobAsync(request.Context.Actor, ownerId, installerId,
                Int(row["stationID"]), cancellationToken))
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");

        var blueprintId = Long(row["blueprintID"]);
        var blueprint = await _items.LockAndGetAsync(connection, transaction, blueprintId, cancellationToken);
        if (blueprint is null || !blueprint.BlueprintLocked)
            throw InvalidJob();
        await _items.SetJobBlueprintLockedAsync(
            connection, transaction, ownerId, blueprint.Id, blueprint.TypeId, blueprint.LocationId,
            blueprint.Flag, blueprint.BlueprintCopy, false, cancellationToken);
        await using var update = new Npgsql.NpgsqlCommand($"""
            UPDATE {_schema}.jobs
               SET status = 101, completed_character_id = $2, completed_at = clock_timestamp()
             WHERE job_id = $1 AND status < 100
            """, connection, transaction);
        Add(update, request.JobId, request.Context.Actor.CharacterId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw InvalidJob();
        await PublishChangedAsync(connection, transaction, request.JobId, ownerId, blueprintId,
            request.Context.Actor.CharacterId, 101, Long(row["successfulRuns"]),
            request.Context.OperationId + ":" + request.JobId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task CompleteInTransactionAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        Eve.Services.Core.IndustryJobAction request,
        CancellationToken cancellationToken)
    {
        var row = await ReadJobAsync(connection, transaction, request.JobId, cancellationToken);
        if (row is null)
            throw InvalidJob();
        var status = Int(row["status"]);
        if (status == 100)
            return;
        if (status >= 100 || Long(row["solarSystemID"]) != request.SolarSystemId ||
            Long(row["endDate"]) > FileTime(_nowUnixMs()))
            throw InvalidJob();

        var ownerId = Long(row["ownerID"]);
        var installerId = Long(row["installerID"]);
        var activityId = Int(row["activityID"]);
        if (!await CanManageExistingJobAsync(request.Context.Actor, ownerId, installerId,
                Int(row["stationID"]), cancellationToken))
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");
        var facility = await _resolveFacility(
            Long(row["facilityID"]), Long(row["stationID"]), request.SolarSystemId,
            request.Context.Actor.CharacterId, cancellationToken);
        if (facility is null || !facility.IsOnline || facility.SolarSystemId != request.SolarSystemId)
            throw InvalidJob();

        var blueprintId = Long(row["blueprintID"]);
        var blueprint = await _items.LockAndGetAsync(connection, transaction, blueprintId, cancellationToken);
        if (blueprint is null || !blueprint.BlueprintLocked)
            throw InvalidJob();
        await _items.SetJobBlueprintLockedAsync(
            connection, transaction, ownerId, blueprint.Id, blueprint.TypeId, blueprint.LocationId,
            blueprint.Flag, blueprint.BlueprintCopy, false, cancellationToken);

        var runs = Int(row["runs"]);
        var licensedRuns = Int(row["licensedRuns"]);
        var productRows = (row["activitySnapshot"] as System.Text.Json.Nodes.JsonObject)?["products"] as System.Text.Json.Nodes.JsonArray ?? [];
        var outputIds = new List<long>();
        long successfulRuns = runs;
        var nextMaterialEfficiency = blueprint.MaterialEfficiency;
        var nextTimeEfficiency = blueprint.TimeEfficiency;

        if (activityId == MaterialResearchActivity)
            nextMaterialEfficiency = Math.Min(10, checked(nextMaterialEfficiency + runs));
        else if (activityId == TimeResearchActivity)
            nextTimeEfficiency = Math.Min(20, checked(nextTimeEfficiency + runs));
        else
        {
            if (blueprint.BlueprintCopy && activityId != CopyingActivity)
            {
                var remaining = (blueprint.BlueprintRuns ?? 0) - runs;
                if (remaining <= 0)
                    await _items.DeleteAsync(connection, transaction, blueprint.Id, cancellationToken);
                else
                    await _items.SetIndustryBlueprintStateAsync(
                        connection, transaction, ownerId, blueprint.Id, blueprint.TypeId, true,
                        remaining, blueprint.MaterialEfficiency, blueprint.TimeEfficiency, cancellationToken);
            }

            successfulRuns = await CreateOutputsAsync(
                connection, transaction, row, productRows, outputIds, cancellationToken);
        }

        if (blueprint.BlueprintCopy && activityId is MaterialResearchActivity or TimeResearchActivity)
        {
            var remaining = (blueprint.BlueprintRuns ?? 0) - runs;
            if (remaining <= 0)
                await _items.DeleteAsync(connection, transaction, blueprint.Id, cancellationToken);
            else
                await _items.SetIndustryBlueprintStateAsync(
                    connection, transaction, ownerId, blueprint.Id, blueprint.TypeId, true,
                    remaining, nextMaterialEfficiency, nextTimeEfficiency, cancellationToken);
        }
        else if (!blueprint.BlueprintCopy && activityId is MaterialResearchActivity or TimeResearchActivity)
        {
            await _items.SetIndustryBlueprintStateAsync(
                connection, transaction, ownerId, blueprint.Id, blueprint.TypeId, false,
                blueprint.BlueprintRuns, nextMaterialEfficiency, nextTimeEfficiency, cancellationToken);
        }

        var outputArray = new Npgsql.NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint,
            Value = outputIds.ToArray(),
        };
        await using var update = new Npgsql.NpgsqlCommand($"""
            UPDATE {_schema}.jobs
               SET status = 100, completed_character_id = $2, successful_runs = $3,
                   output_item_ids = $4, result_snapshot = $5::jsonb, completed_at = clock_timestamp()
             WHERE job_id = $1 AND status < 100
            """, connection, transaction);
        update.Parameters.Add(new Npgsql.NpgsqlParameter { Value = request.JobId });
        update.Parameters.Add(new Npgsql.NpgsqlParameter { Value = request.Context.Actor.CharacterId });
        update.Parameters.Add(new Npgsql.NpgsqlParameter { Value = successfulRuns });
        update.Parameters.Add(outputArray);
        update.Parameters.Add(new Npgsql.NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
            Value = new System.Text.Json.Nodes.JsonObject
            {
                ["blueprintMaterialEfficiency"] = nextMaterialEfficiency,
                ["blueprintTimeEfficiency"] = nextTimeEfficiency,
                ["outputItemIDs"] = new System.Text.Json.Nodes.JsonArray(outputIds.Select(id => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(id)).ToArray()),
            }.ToJsonString(),
        });
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw InvalidJob();

        await PublishChangedAsync(connection, transaction, request.JobId, ownerId, blueprintId,
            request.Context.Actor.CharacterId, 100, successfulRuns,
            request.Context.OperationId + ":" + request.JobId, cancellationToken);
    }
}
