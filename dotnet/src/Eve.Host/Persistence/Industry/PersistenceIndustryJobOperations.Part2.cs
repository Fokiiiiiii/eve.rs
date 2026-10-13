using System.Net;
using IR = Eve.Services.Core.IndustryRules;
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
        long FacilityId, long SolarSystemId, long OwnerId, decimal TaxRate, double TimeMultiplier, bool IsOnline,
        double? ResearchTimeMultiplier = null, IReadOnlySet<int>? StationServices = null, IReadOnlySet<int>? Activities = null)
    {
        /// <summary>The time factor of the science activities (research, copying, invention); the job factor otherwise.</summary>
        public double ResearchFactor => ResearchTimeMultiplier ?? TimeMultiplier;
    }

    private sealed record Material(int TypeId, long Quantity);

    private sealed record Product(int TypeId, long Quantity, double Probability);

    private sealed record SkillRequirement(int TypeId, int Level);

    private sealed record ActivityRecipe(
        int TimeSeconds,
        IReadOnlyList<Material> Materials,
        IReadOnlyList<Product> Products,
        IReadOnlyList<SkillRequirement> Skills,
        int MaxProductionLimit);

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
        request = request with { Job = await WithDecryptorAsync(request.Job, cancellationToken) };
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

    /// <summary>
    /// The client chooses a decryptor in the materials dictionary, not as an optional type: an invention takes the first
    /// listed item that is a decryptor (it carries a decryptor attribute) and is not a material of the activity.
    /// </summary>
    private async Task<Eve.Services.Core.IndustryJobInstallSubmission> WithDecryptorAsync(
        Eve.Services.Core.IndustryJobInstallSubmission job,
        CancellationToken cancellationToken)
    {
        if (job.ActivityId != InventionActivity || job.OptionalTypeId is not null || job.MaterialTypeIds is not { Count: > 0 } listed)
            return job;
        var recipe = await ReadRecipeAsync(job.BlueprintTypeId, job.ActivityId, cancellationToken);
        foreach (var typeId in listed)
        {
            if (recipe.Materials.Any(material => material.TypeId == typeId))
                continue;
            if (await ReadTypeAttributeAsync(typeId, 1112, cancellationToken) is not null ||
                await ReadTypeAttributeAsync(typeId, 1113, cancellationToken) is not null ||
                await ReadTypeAttributeAsync(typeId, 1114, cancellationToken) is not null ||
                await ReadTypeAttributeAsync(typeId, 1124, cancellationToken) is not null)
                return job with { OptionalTypeId = typeId };
        }
        return job;
    }

    private async ValueTask<long> InstallOnceAsync(
        Eve.Services.Core.IndustryJobInstallRequest request,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var job = request.Job;
        var actor = request.Context.Actor;
        await ValidateActorAsync(actor, job.OwnerId, job.Wallet, job.StationId, job.ActivityId, cancellationToken);
        // A corporation's outputs go to the facility's deliveries or a corporation hangar division (the client's output list).
        if (job.OwnerId != actor.CharacterId &&
            (job.OutputLocation.LocationId != job.FacilityId || !IR.IsCorporationOutputFlag(job.OutputLocation.FlagId)))
            throw InvalidJob();
        var facility = await _resolveFacility(
            job.FacilityId, job.StationId, job.SolarSystemId, actor.CharacterId, cancellationToken);
        if (facility is null || !facility.IsOnline || facility.FacilityId != job.FacilityId ||
            facility.SolarSystemId != job.SolarSystemId || facility.OwnerId <= 0 ||
            !double.IsFinite(facility.TimeMultiplier) || facility.TimeMultiplier <= 0 ||
            !double.IsFinite(facility.ResearchFactor) || facility.ResearchFactor <= 0)
            throw InvalidJob();

        var recipe = await ReadRecipeAsync(job.BlueprintTypeId, job.ActivityId, cancellationToken);
        if (facility.StationServices is { } services && !StationRunsActivity(services, job.ActivityId))
            throw InvalidJob();
        if (facility.Activities is { } supported && !supported.Contains(job.ActivityId))
            throw InvalidJob();
        var products = SelectProducts(job, recipe);
        if (job.ActivityId == InventionActivity && products.Count != 1)
            throw InvalidJob();
        var costIndex = await ReadCostIndexAsync(job.SolarSystemId, job.ActivityId, cancellationToken);
        var highSecurity = await IsHighSecurityAsync(job.SolarSystemId, cancellationToken);

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
        // The blueprint's own location and copy flag are authoritative; a client value of zero means the client did not send it.
        if (blueprint is null || blueprint.OwnerId != job.OwnerId || blueprint.TypeId != job.BlueprintTypeId ||
            job.BlueprintLocationId > 0 && blueprint.LocationId != job.BlueprintLocationId ||
            job.BlueprintLocationFlagId > 0 && blueprint.Flag != job.BlueprintLocationFlagId ||
            !blueprint.Singleton || blueprint.BlueprintLocked)
            throw InvalidJob();
        if (blueprint.BlueprintCopy &&
            (blueprint.BlueprintRuns is not { } remainingRuns || remainingRuns < job.Runs))
            throw InvalidJob();
        // The client's activity compatibility: invention needs a copy; copying, research and reactions need an original.
        if (job.ActivityId == InventionActivity && !blueprint.BlueprintCopy)
            throw InvalidJob();
        if (job.ActivityId is CopyingActivity or TimeResearchActivity or MaterialResearchActivity or ReactionActivity &&
            blueprint.BlueprintCopy)
            throw InvalidJob();
        if (job.ActivityId == CopyingActivity && (job.LicensedRuns <= 0 || job.LicensedRuns > recipe.MaxProductionLimit))
            throw InvalidJob();
        if (job.ActivityId is TimeResearchActivity or MaterialResearchActivity)
        {
            var left = job.ActivityId == MaterialResearchActivity
                ? IR.ResearchRunsLeft(blueprint.MaterialEfficiency, IR.MaxMaterialEfficiency, 1)
                : IR.ResearchRunsLeft(blueprint.TimeEfficiency, IR.MaxTimeEfficiency, IR.StepTimeEfficiency);
            if (job.Runs > left)
                throw InvalidJob();
        }
        var requiredMaterials = RequiredMaterials(job, blueprint, recipe);

        var skillLevels = new Dictionary<int, int>();
        foreach (var requirement in recipe.Skills)
        {
            var level = await _skills.LevelAsync(
                connection, transaction, actor.CharacterId, requirement.TypeId, cancellationToken);
            if (level < requirement.Level)
                throw InvalidJob();
            skillLevels[requirement.TypeId] = level;
        }
        // The facility's jumps from the installer against the distance skill of the activity (the client's FACILITY_DISTANCE).
        var installerSystemId = await _installerSystem(actor.CharacterId, cancellationToken) ?? throw InvalidJob();
        var jumps = await JumpsAsync(installerSystemId, job.SolarSystemId, cancellationToken);
        var distanceLevel = await _skills.LevelAsync(connection, transaction, actor.CharacterId, DistanceSkill(job.ActivityId), cancellationToken);
        if (jumps is null || jumps > IR.MaxDistanceJumps(distanceLevel))
            throw InvalidJob();
        await CheckSlotsAsync(connection, transaction, actor.CharacterId, job.ActivityId, cancellationToken);

        var duration = await TimeInSecondsAsync(
            connection, transaction, actor.CharacterId, job, recipe, facility, blueprint, cancellationToken);
        var baseCost = await ReadBaseCostAsync(job, blueprint, products, recipe, cancellationToken);
        var cost = IR.Total(IR.CostParts(baseCost, (double)costIndex, (double)facility.TaxRate, highSecurity, job.ActivityId));

        var probability = 1d;
        JsonObject? invention = null;
        if (job.ActivityId == InventionActivity)
        {
            var decryptor = await ReadDecryptorAsync(job.OptionalTypeId, job.OptionalTypeId2, cancellationToken);
            var lowerSkills = await LowerInventionSkillsAsync(cancellationToken);
            probability = IR.InventionProbability(
                recipe.Products.Select(product => product.Probability).ToArray(),
                IR.InventionSkillBonus(recipe.Skills.Select(skill => (skillLevels[skill.TypeId], lowerSkills.Contains(skill.TypeId)))),
                decryptor.ProbabilityMultiplier);
            invention = new JsonObject
            {
                ["runs"] = (int)Math.Round(products[0].Quantity + decryptor.Runs, MidpointRounding.AwayFromZero),
                ["me"] = IR.InventionMaterialEfficiency + (int)Math.Truncate(decryptor.MaterialEfficiency),
                ["te"] = IR.InventionTimeEfficiency + (int)Math.Truncate(decryptor.TimeEfficiency),
            };
        }

        await ConsumeMaterialsAsync(
            connection, transaction, job, blueprint.Id, requiredMaterials, cancellationToken);
        await _items.SetJobBlueprintLockedAsync(
            connection, transaction, job.OwnerId, blueprint.Id, blueprint.TypeId,
            blueprint.LocationId, blueprint.Flag, blueprint.BlueprintCopy, true, cancellationToken);

        var now = _nowUnixMs();
        var startFileTime = FileTime(now);
        var endFileTime = FileTime(checked(now + (long)duration * 1000));
        var snapshot = ActivitySnapshot(job, blueprint, recipe, requiredMaterials, products, cost, duration, facility, invention);
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
                $18,$19,$20,$21,0,$22,$23,$24,$25,$26,$27,$28,$29,$30::jsonb
            )
            RETURNING job_id
            """, connection, transaction);
        Add(insert,
            request.Context.OperationId, fingerprint, job.ActivityId, blueprint.Id, blueprint.TypeId,
            blueprint.BlueprintCopy, blueprint.LocationId, blueprint.Flag, job.FacilityId, job.OwnerId,
            actor.CharacterId, job.SolarSystemId, job.StationId,
            job.InputLocation.LocationId, job.InputLocation.FlagId,
            job.OutputLocation.LocationId, job.OutputLocation.FlagId,
            startFileTime, endFileTime, job.Runs, job.LicensedRuns, cost, duration,
            probability,
            (object?)job.ProductTypeId ?? DBNull.Value,
            (object?)job.OptionalTypeId ?? DBNull.Value,
            (object?)job.OptionalTypeId2 ?? DBNull.Value,
            job.Wallet.OwnerId, job.Wallet.AccountKey, snapshotJson);
        var jobId = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
        await _readyJobs.ScheduleAsync(connection, transaction, jobId, endFileTime, cancellationToken);
        await PublishChangedAsync(connection, transaction, jobId, job.OwnerId, blueprint.Id,
            actor.CharacterId, 1, 0, request.Context.OperationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Announce(settlement);
        return jobId;
    }

    /// <summary>
    /// The client's job base cost (Activity.job_cost): the estimated item value of the materials per run, scaled
    /// by the activity (copying and research by 2 percent of the blueprint's value, invention by the product's).
    /// </summary>
    private async Task<double> ReadBaseCostAsync(
        Eve.Services.Core.IndustryJobInstallSubmission job,
        Eve.Inventory.Item blueprint,
        IReadOnlyList<Product> products,
        ActivityRecipe recipe,
        CancellationToken cancellationToken)
    {
        async Task<double> Value(int typeId, int activityId) =>
            await ReadEstimatedValueAsync(typeId, activityId, new HashSet<int>(), cancellationToken);
        switch (job.ActivityId)
        {
            case ManufacturingActivity:
                return await Value(job.BlueprintTypeId, ManufacturingActivity) * job.Runs;
            case TimeResearchActivity:
            case MaterialResearchActivity:
                var level = job.ActivityId == MaterialResearchActivity
                    ? blueprint.MaterialEfficiency
                    : blueprint.TimeEfficiency / IR.StepTimeEfficiency;
                var ratio = IR.ResearchJobSeconds(recipe.TimeSeconds, level, job.Runs) / recipe.TimeSeconds;
                return await Value(job.BlueprintTypeId, ManufacturingActivity) * IR.CostPercentage * ratio;
            case CopyingActivity:
                return await Value(job.BlueprintTypeId, ManufacturingActivity) * IR.CostPercentage * job.LicensedRuns * job.Runs;
            case InventionActivity:
                return await Value(products[0].TypeId, ManufacturingActivity) * IR.CostPercentage * job.Runs;
            case ReactionActivity:
                return await Value(job.BlueprintTypeId, ReactionActivity) * job.Runs;
            default:
                throw InvalidJob();
        }
    }

    /// <summary>
    /// The decryptor of an invention: its probability multiplier (dogma 1112), the extra runs (1124) and the ME
    /// (1113) and TE (1114) of the output. A selected item without those attributes is not a decryptor.
    /// </summary>
    private async Task<(double ProbabilityMultiplier, double Runs, double MaterialEfficiency, double TimeEfficiency)> ReadDecryptorAsync(
        int? first,
        int? second,
        CancellationToken cancellationToken)
    {
        double probability = 1, runs = 0, materialEfficiency = 0, timeEfficiency = 0;
        foreach (var typeId in new[] { first, second })
        {
            if (typeId is not { } id)
                continue;
            var multiplier = await ReadTypeAttributeAsync(id, 1112, cancellationToken);
            var me = await ReadTypeAttributeAsync(id, 1113, cancellationToken);
            var te = await ReadTypeAttributeAsync(id, 1114, cancellationToken);
            var extraRuns = await ReadTypeAttributeAsync(id, 1124, cancellationToken);
            if (multiplier is null && me is null && te is null && extraRuns is null)
                throw InvalidJob();
            probability *= multiplier ?? 1;
            runs += extraRuns ?? 0;
            materialEfficiency += me ?? 0;
            timeEfficiency += te ?? 0;
        }
        return (probability, runs, materialEfficiency, timeEfficiency);
    }

    /// <summary>
    /// The job slots of an installer: manufacturing has one base slot plus Mass Production and Advanced Mass
    /// Production; the science activities share one base slot plus Laboratory Operation (one slot per level each).
    /// </summary>
    private async Task CheckSlotsAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long characterId,
        int activityId,
        CancellationToken cancellationToken)
    {
        int[] activities;
        long slots;
        if (activityId == ManufacturingActivity)
        {
            activities = [ManufacturingActivity];
            slots = 1 + await SlotBonusAsync(connection, transaction, characterId, MassProductionSkillId, 450, cancellationToken)
                      + await SlotBonusAsync(connection, transaction, characterId, AdvancedMassProductionSkillId, 450, cancellationToken);
        }
        else if (activityId is TimeResearchActivity or MaterialResearchActivity or CopyingActivity or InventionActivity)
        {
            activities = [TimeResearchActivity, MaterialResearchActivity, CopyingActivity, InventionActivity];
            slots = 1 + await SlotBonusAsync(connection, transaction, characterId, LaboratoryOperationSkillId, 471, cancellationToken)
                      + await SlotBonusAsync(connection, transaction, characterId, AdvancedLaboratoryOperationSkillId, 471, cancellationToken);
        }
        else
        {
            return;
        }
        // One character's installs are serialised so two requests cannot both pass the count.
        await using (var locked = new Npgsql.NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        {
            locked.Parameters.Add(new Npgsql.NpgsqlParameter
            {
                Value = "industry-slots:" + characterId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            await locked.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var count = new Npgsql.NpgsqlCommand($"""
            SELECT COUNT(*) FROM {_schema}.jobs
             WHERE installer_id = $1 AND status < 100 AND activity_id = ANY($2)
            """, connection, transaction);
        count.Parameters.Add(new Npgsql.NpgsqlParameter { Value = characterId });
        count.Parameters.Add(new Npgsql.NpgsqlParameter { Value = activities });
        var used = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        if (used >= slots)
            throw InvalidJob();
    }

    /// <summary>The slot bonus of a skill: its SDE attribute per level times the character's level (0 when it has none).</summary>
    private async Task<long> SlotBonusAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long characterId,
        int skillTypeId,
        int attributeId,
        CancellationToken cancellationToken)
    {
        var perLevel = await ReadTypeAttributeAsync(skillTypeId, attributeId, cancellationToken) ?? 0;
        return (long)perLevel * await _skills.LevelAsync(connection, transaction, characterId, skillTypeId, cancellationToken);
    }

    /// <summary>The skill set that gives the jump allowance of an activity (the client's DISTANCE_SKILL_FOR_ACTIVITY).</summary>
    private static int DistanceSkill(int activityId) => activityId switch
    {
        ManufacturingActivity => ScmSkillId,
        ReactionActivity => RemoteReactionsSkillId,
        _ => ScientificNetworkingSkillId,
    };

    /// <summary>The skill types on the lower invention probability list (the SDE type list the client reads).</summary>
    private async Task<IReadOnlySet<int>> LowerInventionSkillsAsync(CancellationToken cancellationToken)
    {
        var skills = new HashSet<int>();
        var rows = await _sde.ReadAsync("typeLists", [(long)LowerInventionSkillTypeListId], cancellationToken);
        if (rows.Count > 0 && rows[0].Doc.TryGetProperty("includedTypeIDs", out var ids) && ids.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var id in ids.EnumerateArray())
                if (id.ValueKind == System.Text.Json.JsonValueKind.Number)
                    skills.Add(id.GetInt32());
        return skills;
    }

    /// <summary>The jumps between two solar systems over the stargate graph (the SDE neighbours), or null when none within the allowance.</summary>
    private async Task<int?> JumpsAsync(long from, long to, CancellationToken cancellationToken)
    {
        if (from == to)
            return 0;
        _jumpGraph ??= LoadJumpGraphAsync();
        var graph = await _jumpGraph;
        var distance = new Dictionary<long, int> { [from] = 0 };
        var queue = new Queue<long>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var system = queue.Dequeue();
            var jumps = distance[system];
            if (jumps >= IR.MaxIndustryDistanceJumps)
                continue;
            foreach (var neighbour in graph.TryGetValue(system, out var neighbours) ? neighbours : [])
            {
                if (distance.ContainsKey(neighbour))
                    continue;
                if (neighbour == to)
                    return jumps + 1;
                distance[neighbour] = jumps + 1;
                queue.Enqueue(neighbour);
            }
        }
        return null;
    }

    /// <summary>The stargate graph: each gate joins its system to its destination (the pinned SDE carries no neighbour lists).</summary>
    private async Task<IReadOnlyDictionary<long, long[]>> LoadJumpGraphAsync()
    {
        var rows = await _sde.ReadAsync("mapStargates", cancellationToken: CancellationToken.None);
        var graph = new Dictionary<long, HashSet<long>>();
        foreach (var row in rows)
        {
            if (!row.Doc.TryGetProperty("solarSystemID", out var from) || from.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !row.Doc.TryGetProperty("destination", out var destination) ||
                !destination.TryGetProperty("solarSystemID", out var to) || to.ValueKind != System.Text.Json.JsonValueKind.Number)
                continue;
            var origin = from.GetInt64();
            var target = to.GetInt64();
            if (!graph.TryGetValue(origin, out var origins)) graph[origin] = origins = [];
            origins.Add(target);
        }
        return graph.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    /// <summary>
    /// The NPC station services that run an activity (the SDE station operation's service list): the Factory
    /// (14) runs manufacturing, the Laboratory (15) the science activities. No NPC station service runs reactions.
    /// </summary>
    private static bool StationRunsActivity(IReadOnlySet<int> services, int activityId) => activityId switch
    {
        ManufacturingActivity => services.Contains(FactoryServiceId),
        ReactionActivity => false,
        _ => services.Contains(LaboratoryServiceId),
    };

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
        if (status == 102)
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
               SET status = 102, completed_character_id = $2, completed_at = clock_timestamp()
             WHERE job_id = $1 AND status < 100
            """, connection, transaction);
        Add(update, request.JobId, request.Context.Actor.CharacterId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw InvalidJob();
        await PublishChangedAsync(connection, transaction, request.JobId, ownerId, blueprintId,
            request.Context.Actor.CharacterId, 102, Long(row["successfulRuns"]),
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
            nextMaterialEfficiency = Math.Min(IR.MaxMaterialEfficiency, checked(nextMaterialEfficiency + runs));
        else if (activityId == TimeResearchActivity)
            nextTimeEfficiency = Math.Min(IR.MaxTimeEfficiency, checked(nextTimeEfficiency + IR.StepTimeEfficiency * runs));
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
                null, nextMaterialEfficiency, nextTimeEfficiency, cancellationToken);
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
