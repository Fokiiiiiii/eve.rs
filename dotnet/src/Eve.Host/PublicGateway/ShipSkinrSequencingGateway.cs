using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Eve.GameStore;
using Eve.Host.Persistence;
using Eve.Inventory;
using Eve.Scheduler;
using Eve.Services;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.PublicGateway;

/// <summary>V24.01 SKINR sequencing, component license and custom license handlers.</summary>
internal sealed class ShipSkinrSequencingGateway(
    NpgsqlDataSource source,
    PgShipSkinrStore store,
    ShipSkinrCatalog catalog,
    PgSkillStore skills,
    IReadOnlyDictionary<string, int> skillTypeIds,
    PgItemStore items,
    string inventorySchema,
    PgWalletStore wallets,
    DurableScheduler scheduler,
    NoticeHub notices,
    CharacterSessions sessions,
    GameStoreCache? cosmetics,
    Func<long>? clock = null)
{
    private const string Prefix = "eve_public.cosmetic.ship.skin.thirdparty.";
    private const string ComponentApi = Prefix + "component.license.api.";
    private const string FiniteApi = Prefix + "component.license.finite.api.";
    private const string InfiniteApi = Prefix + "component.license.infinite.api.";
    private const string ItemApi = Prefix + "component.license.item.api.";
    private const string SequencingApi = Prefix + "sequencing.api.";
    private const string JobApi = Prefix + "sequencing.job.api.";
    private const string LicenseApi = Prefix + "license.api.";
    private const string DataApi = Prefix + "api.";
    private static readonly int[] PlexByTier = [25, 30, 35, 40, 50, 60, 75, 90, 110, 130, 160, 200, 250, 310, 380, 470, 580, 720];
    private static readonly int[] HoursByTier = [3, 7, 12, 19, 28, 40, 56, 75, 97, 121, 150, 184, 224, 271, 325, 386, 455, 532];
    private static readonly int[] BatchRunsByLevel = [0, 4, 9, 19, 29, 49];
    private static readonly int[] MassRunsByLevel = [0, 25, 50, 100, 150, 250];
    private static readonly int[] IndustrialRunsByLevel = [0, 200, 450, 700, 1700, 4700];
    private static readonly int[] ParallelJobsByLevel = [0, 1, 3, 5, 7, 9];
    private static readonly int[] MultiThreadJobsByLevel = [0, 2, 4, 6, 8, 10];
    private static readonly int[] DurationByLevel = [0, 1, 3, 6, 10, 15];
    private readonly Func<long> _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private readonly string _inventorySchema = Regex.IsMatch(inventorySchema, "^[a-z_][a-z0-9_]{0,62}$")
        ? inventorySchema
        : throw new ArgumentException("not a usable inventory schema", nameof(inventorySchema));

    public IReadOnlyDictionary<string, GatewayHandler> Handlers()
    {
        var handlers = new Dictionary<string, GatewayHandler>(StringComparer.Ordinal)
        {
            [SequencingApi + "GetTierPricingRequest"] = (_, _) =>
                Task.FromResult(Success(SequencingApi + "GetTierPricingResponse", TierPricing())),
            [SequencingApi + "GetTierDurationsRequest"] = GetTierDurationsAsync,
            [ComponentApi + "GetOwnedRequest"] = GetOwnedComponentsAsync,
            [FiniteApi + "GetRequest"] = (request, token) => GetComponentLicenseAsync(request, infinite: false, token),
            [InfiniteApi + "GetRequest"] = (request, token) => GetComponentLicenseAsync(request, infinite: true, token),
            [ItemApi + "ConsumeRequest"] = ConsumeComponentItemAsync,
            [JobApi + "InitiateRequest"] = InitiateAsync,
            [JobApi + "GetAllActiveRequest"] = GetAllActiveJobsAsync,
            [JobApi + "GetRequest"] = GetJobAsync,
            [LicenseApi + "GetOwnedRequest"] = GetOwnedSkinsAsync,
            [LicenseApi + "GetRequest"] = GetSkinLicenseAsync,
            [LicenseApi + "ActivateRequest"] = ActivateSkinLicenseAsync,
            [LicenseApi + "ApplyRequest"] = (request, token) => ApplySkinAsync(request, unapply: false, token),
            [LicenseApi + "UnapplyRequest"] = (request, token) => ApplySkinAsync(request, unapply: true, token),
            [DataApi + "GetRequest"] = GetSkinDataAsync,
        };
        return handlers;
    }

    private long NowMs => _clock();

    internal static (long PlexCost, long DurationMs) CalculateQuote(int tier, int quantity, int durationBonusPercent)
    {
        if (tier is < 1 or > 18) throw new ArgumentOutOfRangeException(nameof(tier));
        if (quantity is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(quantity));
        var plex = checked((long)PlexByTier[tier - 1] * quantity);
        var baseDuration = checked((long)HoursByTier[tier - 1] * 3_600_000L);
        var duration = checked(baseDuration * (100 - Math.Clamp(durationBonusPercent, 0, 60)) / 100);
        return (plex, Math.Max(1000, duration));
    }

    private static byte[] TierPricing()
    {
        var response = new ProtoWriter();
        foreach (var (price, index) in PlexByTier.Select((value, index) => (value, index)))
        {
            var tier = new ProtoWriter();
            tier.Uint(1, (ulong)(index + 1));
            var plex = new ProtoWriter();
            plex.Uint(1, checked((ulong)price * 100));
            var row = new ProtoWriter();
            row.Message(1, tier.ToArray());
            row.Message(2, plex.ToArray());
            response.Message(1, row.ToArray());
        }
        return response.ToArray();
    }

    private async Task<GatewayAnswer> GetTierDurationsAsync(
        GatewayRequest request, CancellationToken cancellationToken)
    {
        var responseType = SequencingApi + "GetTierDurationsResponse";
        if (!ValidCharacter(request)) return Error(400, "INVALID_CHARACTER", responseType);
        var discount = await SkillPolicyAsync(request.CharacterId!.Value, cancellationToken, "durationBonusPercent");
        return Success(responseType, TierDurations(discount));
    }

    internal static byte[] TierDurations(int discountPercentageApplied)
    {
        var response = new ProtoWriter();
        response.Uint(2, (ulong)Math.Clamp(discountPercentageApplied, 0, 60));
        foreach (var (hours, index) in HoursByTier.Select((value, index) => (value, index)))
        {
            var tier = new ProtoWriter();
            tier.Uint(1, (ulong)(index + 1));
            var duration = new ProtoWriter();
            duration.Uint(1, checked((ulong)hours * 3600));
            var row = new ProtoWriter();
            row.Message(1, tier.ToArray());
            row.Message(2, duration.ToArray());
            response.Message(1, row.ToArray());
        }
        return response.ToArray();
    }

    private async Task<GatewayAnswer> GetOwnedComponentsAsync(GatewayRequest request, CancellationToken cancellationToken)
    {
        if (!ValidCharacter(request)) return Error(400, "INVALID_CHARACTER", ComponentApi + "GetOwnedResponse");
        await using var command = source.CreateCommand(store.Q("""
            SELECT component_id, finite_uses, infinite, first_acquired_ms
            FROM {schema}.skinr_component_licenses
            WHERE character_id = $1 AND (finite_uses > 0 OR infinite)
            ORDER BY component_id
            """));
        command.Parameters.Add(new NpgsqlParameter { Value = request.CharacterId!.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var response = new ProtoWriter();
        while (await reader.ReadAsync(cancellationToken))
        {
            var component = catalog.ComponentFor(reader.GetInt32(0));
            if (component is null) continue;
            var acquired = reader.GetInt64(3);
            if (reader.GetInt64(1) > 0)
            {
                var license = new ProtoWriter();
                license.Message(1, ComponentIdentifier(component.Id, component.CategoryId));
                license.Uint(2, (ulong)reader.GetInt64(1));
                license.Message(3, Timestamp(acquired));
                response.Message(1, license.ToArray());
            }
            if (reader.GetBoolean(2))
            {
                var license = new ProtoWriter();
                license.Message(1, ComponentIdentifier(component.Id, component.CategoryId));
                license.Message(2, Timestamp(acquired));
                response.Message(2, license.ToArray());
            }
        }
        return Success(ComponentApi + "GetOwnedResponse", response.ToArray());
    }

    private async Task<GatewayAnswer> GetComponentLicenseAsync(
        GatewayRequest request, bool infinite, CancellationToken cancellationToken)
    {
        var responseType = (infinite ? InfiniteApi : FiniteApi) + "GetResponse";
        if (!ValidCharacter(request)) return Error(400, "INVALID_CHARACTER", responseType);
        var (character, componentId, category) = ComponentLicenseId(request.Payload, 1);
        if (character != request.CharacterId || componentId <= 0 ||
            catalog.ComponentFor(componentId) is not { } component || component.CategoryId != category)
            return Error(400, "INVALID_COMPONENT_LICENSE", responseType);
        await using var command = source.CreateCommand(store.Q("""
            SELECT finite_uses, infinite, first_acquired_ms
            FROM {schema}.skinr_component_licenses
            WHERE character_id = $1 AND component_id = $2
            """));
        command.Parameters.Add(new NpgsqlParameter { Value = request.CharacterId!.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = componentId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || (infinite ? !reader.GetBoolean(1) : reader.GetInt64(0) <= 0))
            return Error(404, "COMPONENT_LICENSE_NOT_FOUND", responseType);
        var license = new ProtoWriter();
        if (!infinite) license.Uint(1, (ulong)reader.GetInt64(0));
        license.Message(infinite ? 1 : 2, Timestamp(reader.GetInt64(2)));
        var payload = new ProtoWriter();
        payload.Message(1, license.ToArray());
        return Success(responseType, payload.ToArray());
    }

    private async Task<GatewayAnswer> ConsumeComponentItemAsync(GatewayRequest request, CancellationToken cancellationToken)
    {
        var responseType = ItemApi + "ConsumeResponse";
        if (!ValidCharacter(request)) return Error(400, "INVALID_CHARACTER", responseType);
        var itemId = NestedSequential(request.Payload, 1, 1, 1);
        var quantity = Varint(request.Payload, 2);
        var requestId = RequestId(request);
        if (itemId <= 0 || quantity <= 0 || quantity > 5000 || requestId == Guid.Empty)
            return Error(400, "INVALID_COMPONENT_LICENSE_ITEM", responseType);
        var fingerprint = Fingerprint(request.Payload);
        var character = request.CharacterId!.Value;
        var now = NowMs;
        int componentId;
        int categoryId;
        bool infinite;
        long grantUses;
        Item? consumedItem = null;
        long remainingItemQuantity = 0;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await PgShipSkinrStore.LockCharacterAsync(connection, transaction, character, cancellationToken);
            var duplicate = await ReadReceiptAsync(connection, transaction, character, "component.consume", requestId, fingerprint, cancellationToken);
            if (duplicate == ReceiptResult.Conflict) return Error(409, "REQUEST_CONFLICT", responseType);
            if (duplicate == ReceiptResult.Replay)
            {
                await transaction.CommitAsync(cancellationToken);
                return Success(responseType, []);
            }

            var item = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
            if (item is null || item.OwnerId != character || item.Quantity < quantity)
                return Error(409, "COMPONENT_LICENSE_ITEM_CUSTODY", responseType);
            var resolved = catalog.ComponentForLicenseType(item.TypeId);
            if (resolved is not { } license)
                return Error(400, "INVALID_COMPONENT_LICENSE_ITEM", responseType);
            componentId = license.Component.Id;
            categoryId = license.Component.CategoryId;
            infinite = license.Uses == 0;
            var consume = infinite ? 1L : quantity;
            grantUses = infinite ? 0 : checked((long)license.Uses * consume);
            if (quantity != consume || (!infinite && grantUses <= 0))
                return Error(400, "INVALID_COMPONENT_LICENSE_QUANTITY", responseType);
            consumedItem = item;
            remainingItemQuantity = item.Quantity - consume;
            if (remainingItemQuantity == 0) await items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
            else await items.SetQuantityAsync(connection, transaction, item.Id, remainingItemQuantity, cancellationToken);

            await using (var grant = new NpgsqlCommand(store.Q("""
                INSERT INTO {schema}.skinr_component_licenses
                    (character_id, component_id, finite_uses, infinite, first_acquired_ms, updated_ms)
                VALUES ($1, $2, $3, $4, $5, $5)
                ON CONFLICT (character_id, component_id) DO UPDATE SET
                    finite_uses = skinr_component_licenses.finite_uses + EXCLUDED.finite_uses,
                    infinite = skinr_component_licenses.infinite OR EXCLUDED.infinite,
                    updated_ms = EXCLUDED.updated_ms
                """), connection, transaction))
            {
                grant.Parameters.Add(new NpgsqlParameter { Value = character });
                grant.Parameters.Add(new NpgsqlParameter { Value = componentId });
                grant.Parameters.Add(new NpgsqlParameter { Value = grantUses });
                grant.Parameters.Add(new NpgsqlParameter { Value = infinite });
                grant.Parameters.Add(new NpgsqlParameter { Value = now });
                await grant.ExecuteNonQueryAsync(cancellationToken);
            }
            await InsertReceiptAsync(connection, transaction, character, "component.consume", requestId, fingerprint, null, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (consumedItem is not null)
            NotifyInventoryChanged(character, consumedItem, remainingItemQuantity);

        var granted = new ProtoWriter();
        granted.Message(1, ComponentLicenseKind(componentId, categoryId, infinite ? null : grantUses, infinite));
        notices.Publish(ComponentApi + "GrantedNotice", granted.ToArray(), character: character);
        return Success(responseType, []);
    }

    private async Task<GatewayAnswer> InitiateAsync(GatewayRequest request, CancellationToken cancellationToken)
    {
        var responseType = JobApi + "InitiateResponse";
        if (!ValidCharacter(request)) return Error(400, "INVALID_CHARACTER", responseType);
        if (!TryParseDesign(request.Payload, out var design)) return Error(400, "INVALID_DESIGN", responseType);
        if (!ValidName(design.Name, design.LineName) ||
            !catalog.TryValidateLayout(design.Layout, out var shipTypeId, out var tier, out _) ||
            tier is < 1 or > 18)
            return Error(400, "INVALID_DESIGN", responseType);
        var quantity = Varint(request.Payload, 5);
        var requestId = RequestId(request);
        if (quantity is < 1 or > 5000 || requestId == Guid.Empty)
            return Error(400, "INVALID_SEQUENCING_REQUEST", responseType);
        var character = request.CharacterId!.Value;
        var fingerprint = Fingerprint(request.Payload);
        var now = NowMs;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var prior = await ReadReceiptAsync(connection, transaction, character, "sequence.initiate",
                requestId, fingerprint, cancellationToken);
            if (prior == ReceiptResult.Conflict) return Error(409, "REQUEST_CONFLICT", responseType);
            if (prior == ReceiptResult.Replay)
            {
                var priorJobId = await ReceiptJobAsync(connection, transaction, character, requestId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                var priorJob = await ReadJobAsync(source, priorJobId, character, cancellationToken);
                return priorJob is null
                    ? Error(409, "SEQUENCE_RECEIPT_INCOMPLETE", responseType)
                    : Success(responseType, EncodeInitiateResponse(priorJob));
            }
            await transaction.CommitAsync(cancellationToken);
        }
        var maxRuns = await SkillPolicyAsync(character, cancellationToken, "maxRuns");
        if (quantity > maxRuns) return Error(403, "SKINR_RUN_LIMIT", responseType);
        var maxJobs = await SkillPolicyAsync(character, cancellationToken, "maxConcurrentJobs");
        var durationBonus = await SkillPolicyAsync(character, cancellationToken, "durationBonusPercent");
        var (cost, durationMs) = CalculateQuote(tier, checked((int)quantity), durationBonus);
        var due = checked(now + durationMs);
        var skinHex = ShipSkinrDesignId.Create(character, design.Name, design.LineName, tier, design.Layout.Span);
        var jobId = Guid.NewGuid();
        IReadOnlyList<InventoryChange> binderChanges = [];
        byte[] editorConfigurations;

        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await PgShipSkinrStore.LockCharacterAsync(connection, transaction, character, cancellationToken);
            var receipt = await ReadReceiptAsync(connection, transaction, character, "sequence.initiate", requestId, fingerprint, cancellationToken);
            if (receipt == ReceiptResult.Conflict) return Error(409, "REQUEST_CONFLICT", responseType);
            if (receipt == ReceiptResult.Replay)
            {
                jobId = await ReceiptJobAsync(connection, transaction, character, requestId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                var replay = await ReadJobAsync(source, jobId, character, cancellationToken);
                return replay is null ? Error(409, "SEQUENCE_RECEIPT_INCOMPLETE", responseType) : Success(responseType, EncodeInitiateResponse(replay));
            }

            var activeJobs = await ActiveJobCountAsync(connection, transaction, character, cancellationToken);
            if (activeJobs >= maxJobs) return Error(403, "SKINR_JOB_LIMIT", responseType);
            var components = SequenceComponents(request.Payload, design.Layout, quantity);
            if (components is null) return Error(400, "COMPONENT_LICENSES_INVALID", responseType);
            var binderNeeds = await ValidateAndConsumeComponentLicensesAsync(
                connection, transaction, character, components, now, cancellationToken);
            if (binderNeeds is null) return Error(403, "COMPONENT_LICENSES_INSUFFICIENT", responseType);
            var consumedBinders = await ConsumeBindersAsync(connection, transaction, character, binderNeeds, cancellationToken);
            if (consumedBinders is null)
                return Error(403, "SKINR_BINDERS_INSUFFICIENT", responseType);
            binderChanges = consumedBinders;

            try
            {
                await wallets.AdjustPlexAsync(connection, transaction, character, -cost, cancellationToken);
            }
            catch (InsufficientFundsException)
            {
                return Error(403, "SKINR_PLEX_INSUFFICIENT", responseType);
            }
            catch (WalletNotFoundException)
            {
                return Error(404, "WALLET_NOT_FOUND", responseType);
            }

            editorConfigurations = await ReadSavedEditorConfigurationsAsync(
                connection, transaction, character, skinHex, cancellationToken);
            await using (var insert = new NpgsqlCommand(store.Q("""
                INSERT INTO {schema}.skinr_sequencing_jobs
                    (job_id, character_id, request_id, request_fingerprint, skin_hex, name, line_name,
                     ship_type_id, tier, licenses, plex_cost, due_ms, state, layout,
                     editor_configurations, created_ms)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, 'pending', $13, $14, $15)
                """), connection, transaction))
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = jobId });
                insert.Parameters.Add(new NpgsqlParameter { Value = character });
                insert.Parameters.Add(new NpgsqlParameter { Value = requestId });
                insert.Parameters.Add(new NpgsqlParameter { Value = fingerprint });
                insert.Parameters.Add(new NpgsqlParameter { Value = skinHex });
                insert.Parameters.Add(new NpgsqlParameter { Value = design.Name });
                insert.Parameters.Add(new NpgsqlParameter { Value = design.LineName });
                insert.Parameters.Add(new NpgsqlParameter { Value = shipTypeId });
                insert.Parameters.Add(new NpgsqlParameter { Value = tier });
                insert.Parameters.Add(new NpgsqlParameter { Value = quantity });
                insert.Parameters.Add(new NpgsqlParameter { Value = cost });
                insert.Parameters.Add(new NpgsqlParameter { Value = due });
                insert.Parameters.Add(new NpgsqlParameter { Value = design.Layout.ToArray() });
                insert.Parameters.Add(new NpgsqlParameter { Value = editorConfigurations });
                insert.Parameters.Add(new NpgsqlParameter { Value = now });
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await InsertReceiptAsync(connection, transaction, character, "sequence.initiate", requestId, fingerprint, jobId, now, cancellationToken);
            await scheduler.ScheduleAsync(connection, transaction,
                new JobSpec(ShipSkinrSequenceJobs.Kind, due)
                {
                    Key = jobId.ToString("N"),
                    OnDuplicate = OnDuplicate.Keep,
                }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        scheduler.Wake();
        foreach (var change in binderChanges)
            NotifyInventoryChanged(character, change.Item, change.RemainingQuantity);
        var result = await ReadJobAsync(source, jobId, character, cancellationToken);
        return result is null ? Error(500, "SEQUENCE_JOB_MISSING", responseType) : Success(responseType, EncodeInitiateResponse(result));
    }

    private async Task<GatewayAnswer> GetAllActiveJobsAsync(GatewayRequest request, CancellationToken cancellationToken)
    {
        var responseType = JobApi + "GetAllActiveResponse";
        if (!ValidCharacter(request)) return Error(400, "INVALID_CHARACTER", responseType);
        var jobs = await ReadJobsAsync(request.CharacterId!.Value, activeOnly: true, cancellationToken);
        var response = new ProtoWriter();
        foreach (var job in jobs)
        {
            var entry = new ProtoWriter();
            entry.Message(1, Uuid(job.Id));
            entry.Message(2, EncodeJobAttributes(job));
            response.Message(1, entry.ToArray());
        }
        return Success(responseType, response.ToArray());
    }

    private async Task<GatewayAnswer> GetJobAsync(GatewayRequest request, CancellationToken cancellationToken)
    {
        var responseType = JobApi + "GetResponse";
        if (!ValidCharacter(request)) return Error(400, "INVALID_CHARACTER", responseType);
        var jobId = GuidField(request.Payload, 1);
        var job = jobId == Guid.Empty ? null : await ReadJobAsync(source, jobId, request.CharacterId!.Value, cancellationToken);
        if (job is null) return Error(404, "SKINR_JOB_NOT_FOUND", responseType);
        var response = new ProtoWriter();
        response.Message(1, EncodeJobAttributes(job));
        return Success(responseType, response.ToArray());
    }

    private async Task<GatewayAnswer> GetOwnedSkinsAsync(GatewayRequest request, CancellationToken cancellationToken)
    {
        var responseType = LicenseApi + "GetOwnedResponse";
        if (!ValidCharacter(request)) return Error(400, "INVALID_CHARACTER", responseType);
        await using var command = source.CreateCommand(store.Q("""
            SELECT skin_hex FROM {schema}.skinr_ship_licenses
            WHERE character_id = $1 AND (activated OR unactivated > 0 OR escrowed > 0)
            ORDER BY first_acquired_ms, skin_hex
            """));
        command.Parameters.Add(new NpgsqlParameter { Value = request.CharacterId!.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var response = new ProtoWriter();
        while (await reader.ReadAsync(cancellationToken))
            response.Message(1, LicenseIdentifier(request.CharacterId.Value, reader.GetString(0)));
        return Success(responseType, response.ToArray());
    }

    private async Task<GatewayAnswer> GetSkinLicenseAsync(GatewayRequest request, CancellationToken cancellationToken)
    {
        var responseType = LicenseApi + "GetResponse";
        if (!TryLicenseIdentifier(request.Payload, 1, out var character, out var skinHex) ||
            character != request.CharacterId || !ValidCharacter(request))
            return Error(403, "SKINR_LICENSE_NOT_OWNED", responseType);
        var license = await ReadSkinLicenseAsync(character, skinHex, cancellationToken);
        if (license is null) return Error(404, "SKINR_LICENSE_NOT_FOUND", responseType);
        var payload = new ProtoWriter();
        payload.Message(1, EncodeLicenseAttributes(license));
        return Success(responseType, payload.ToArray());
    }

    private async Task<GatewayAnswer> ActivateSkinLicenseAsync(GatewayRequest request, CancellationToken cancellationToken)
    {
        var responseType = LicenseApi + "ActivateResponse";
        if (!TryLicenseIdentifier(request.Payload, 1, out var character, out var skinHex) ||
            character != request.CharacterId || !ValidCharacter(request))
            return Error(403, "SKINR_LICENSE_NOT_OWNED", responseType);
        var requestId = RequestId(request);
        if (requestId == Guid.Empty) return Error(400, "INVALID_SKIN_ACTIVATION", responseType);
        var fingerprint = Fingerprint(request.Payload);
        var now = NowMs;
        SkinLicense? current;
        SkinLicense? previous;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await PgShipSkinrStore.LockCharacterAsync(connection, transaction, character, cancellationToken);
            var receipt = await ReadReceiptAsync(connection, transaction, character, "license.activate", requestId, fingerprint, cancellationToken);
            if (receipt == ReceiptResult.Conflict) return Error(409, "REQUEST_CONFLICT", responseType);
            current = await ReadSkinLicenseAsync(connection, transaction, character, skinHex, forUpdate: true, cancellationToken);
            if (current is null) return Error(404, "SKINR_LICENSE_NOT_FOUND", responseType);
            previous = current;
            if (receipt == ReceiptResult.Replay)
            {
                await transaction.CommitAsync(cancellationToken);
                return Success(responseType, []);
            }
            if (!current.Activated && current.Unactivated <= 0)
                return Error(403, "SKINR_NO_TRANSFERABLE_QUANTITY", responseType);
            if (!current.Activated)
            {
                await using var update = new NpgsqlCommand(store.Q("""
                    UPDATE {schema}.skinr_ship_licenses
                    SET activated = true, unactivated = unactivated - 1, updated_ms = $3
                    WHERE character_id = $1 AND skin_hex = $2
                    """), connection, transaction);
                update.Parameters.Add(new NpgsqlParameter { Value = character });
                update.Parameters.Add(new NpgsqlParameter { Value = skinHex });
                update.Parameters.Add(new NpgsqlParameter { Value = now });
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await InsertReceiptAsync(connection, transaction, character, "license.activate", requestId, fingerprint, null, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        current = await ReadSkinLicenseAsync(character, skinHex, cancellationToken);
        if (current is not null && previous is not null)
            PublishLicenseUpdate(character, skinHex, current, previous);
        return Success(responseType, []);
    }

    private async Task<GatewayAnswer> ApplySkinAsync(GatewayRequest request, bool unapply, CancellationToken cancellationToken)
    {
        var responseType = LicenseApi + (unapply ? "UnapplyResponse" : "ApplyResponse");
        if (!TryLicenseIdentifier(request.Payload, 1, out var character, out var skinHex) ||
            character != request.CharacterId || !ValidCharacter(request))
            return Error(403, "SKINR_LICENSE_NOT_OWNED", responseType);
        var shipId = NestedSequential(request.Payload, 2, 1, 1);
        var requestId = RequestId(request);
        if (shipId <= 0 || requestId == Guid.Empty) return Error(400, "INVALID_SKIN_APPLICATION", responseType);
        if (cosmetics is null) return Error(503, "SKIN_COSMETICS_UNAVAILABLE", responseType);
        var design = await ReadDesignAsync(character, skinHex, cancellationToken);
        if (design is null) return Error(404, "SKIN_DESIGN_NOT_FOUND", responseType);
        var license = await ReadSkinLicenseAsync(character, skinHex, cancellationToken);
        if (license is not { Activated: true }) return Error(404, "ACTIVATED_LICENSE_NOT_FOUND", responseType);
        var session = sessions.Of(character);
        var item = await items.GetAsync(shipId, cancellationToken);
        if (item is null || item.OwnerId != character || item.TypeId != design.ShipTypeId)
            return Error(403, "SHIP_NOT_OWNED_OR_SKIN_TYPE_MISMATCH", responseType);
        var activeShip = session?.GetInt("shipid") ?? 0;
        var dockedLocation = session?.GetInt("stationid") ?? session?.GetInt("stationID") ?? session?.GetInt("structureid") ?? 0;
        var controlled = activeShip == shipId ||
            (dockedLocation > 0 && item.LocationId == dockedLocation && item.Flag == 4);
        if (!controlled) return Error(403, "SHIP_NOT_CONTROLLED", responseType);

        var fingerprint = Fingerprint(request.Payload);
        var now = NowMs;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            await PgShipSkinrStore.LockCharacterAsync(connection, transaction, character, cancellationToken);
            var receipt = await ReadReceiptAsync(connection, transaction, character, unapply ? "license.unapply" : "license.apply", requestId, fingerprint, cancellationToken);
            if (receipt == ReceiptResult.Conflict) return Error(409, "REQUEST_CONFLICT", responseType);
            if (receipt == ReceiptResult.Replay)
            {
                await transaction.CommitAsync(cancellationToken);
                return Success(responseType, []);
            }
            var path = $"/ships/{shipId.ToString(CultureInfo.InvariantCulture)}";
            var previous = cosmetics.Read("shipCosmetics", path).Data as JsonObject;
            var currentHex = previous?["skinHex"]?.GetValue<string>();
            if (unapply && !StringComparer.Ordinal.Equals(currentHex, skinHex))
                return Error(409, "SKINR_APPLICATION_CHANGED", responseType);
            var next = previous?.DeepClone() as JsonObject ?? new JsonObject();
            next["shipID"] = shipId;
            next["ownerID"] = item.OwnerId;
            next["characterID"] = character;
            next["typeID"] = item.TypeId;
            next["skinID"] = null;
            next["skinHex"] = unapply ? null : skinHex;
            next["updatedAt"] = DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("O", CultureInfo.InvariantCulture);
            var write = cosmetics.Write("shipCosmetics", path, next);
            if (!write.Success) return Error(500, "SKINR_APPLICATION_FAILED", responseType);
            await InsertReceiptAsync(connection, transaction, character, unapply ? "license.unapply" : "license.apply",
                requestId, fingerprint, null, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (session is not null)
        {
            session.SendNotification("OnShipCosmeticsChanged", "clientID", InventoryWire.Id(shipId), new Eve.Marshal.PyDict([]));
            session.SendNotification("OnCurrentShipSkinChange", "clientID", InventoryWire.Id(shipId), new Eve.Marshal.PyInt(0));
        }
        return Success(responseType, []);
    }

    private async Task<GatewayAnswer> GetSkinDataAsync(GatewayRequest request, CancellationToken cancellationToken)
    {
        var responseType = DataApi + "GetResponse";
        var hex = StringValue(request.Payload, 1);
        if (!IsHex(hex)) return Error(400, "INVALID_SKIN_IDENTIFIER", responseType);
        var design = await ReadDesignAsync(null, hex, cancellationToken);
        if (design is null) return Error(404, "SKIN_DESIGN_NOT_FOUND", responseType);
        var response = new ProtoWriter();
        response.Message(1, EncodeSkinDesign(design));
        return Success(responseType, response.ToArray());
    }

    private async Task<SkinDesign?> GetSavedDraftsByHashAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long character, string skinHex, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(store.Q("""
            SELECT character_id, name, line_name, ship_type_id, layout, editor_configurations, created_ms
            FROM {schema}.skinr_design_drafts
            WHERE character_id = $1 AND skin_hex = $2
            ORDER BY updated_ms DESC, draft_id
            LIMIT 1
            """), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        command.Parameters.Add(new NpgsqlParameter { Value = skinHex });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var layout = reader.GetFieldValue<byte[]>(4);
        if (!catalog.TryValidateLayout(layout, out var hull, out var tier, out _)) return null;
        return new SkinDesign(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), hull, tier,
            layout, reader.GetFieldValue<byte[]>(5), reader.GetInt64(6));
    }

    private async Task<SkinDesign?> ReadDesignAsync(long? character, string skinHex, CancellationToken cancellationToken)
    {
        if (character is { } owner && owner > 0)
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var draft = await GetSavedDraftsByHashAsync(connection, transaction, owner, skinHex, cancellationToken);
            if (draft is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return draft;
            }
        }
        if (character is null)
        {
            await using (var draftCommand = source.CreateCommand(store.Q("""
                SELECT character_id, name, line_name, ship_type_id, layout, editor_configurations, created_ms
                FROM {schema}.skinr_design_drafts
                WHERE skin_hex = $1
                ORDER BY updated_ms DESC, character_id, draft_id
                LIMIT 1
                """)))
            {
                draftCommand.Parameters.Add(new NpgsqlParameter { Value = skinHex });
                await using var draftReader = await draftCommand.ExecuteReaderAsync(cancellationToken);
                if (await draftReader.ReadAsync(cancellationToken))
                {
                    var draftLayout = draftReader.GetFieldValue<byte[]>(4);
                    if (!catalog.TryValidateLayout(draftLayout, out var hull, out var draftTier, out _)) return null;
                    return new SkinDesign(draftReader.GetInt64(0), draftReader.GetString(1), draftReader.GetString(2),
                        hull, draftTier, draftLayout, draftReader.GetFieldValue<byte[]>(5), draftReader.GetInt64(6));
                }
            }
        }

        var ownerClause = character is > 0 ? "AND character_id = $2" : "";
        await using var command = source.CreateCommand(store.Q($$"""
            SELECT character_id, name, line_name, ship_type_id, layout, editor_configurations, first_acquired_ms
            FROM {schema}.skinr_ship_licenses
            WHERE skin_hex = $1 {{ownerClause}}
            ORDER BY first_acquired_ms, character_id
            LIMIT 1
            """));
        command.Parameters.Add(new NpgsqlParameter { Value = skinHex });
        if (character is { } characterId)
            command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var layout = reader.GetFieldValue<byte[]>(4);
        if (!catalog.TryValidateLayout(layout, out var shipTypeId, out var tier, out _)) return null;
        return new SkinDesign(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), shipTypeId,
            tier, layout, reader.GetFieldValue<byte[]>(5), reader.GetInt64(6));
    }

    private async Task<IReadOnlyList<SkinJob>> ReadJobsAsync(long character, bool activeOnly, CancellationToken cancellationToken)
    {
        var stateClause = activeOnly ? "AND state = 'pending'" : "";
        await using var command = source.CreateCommand(store.Q($$"""
            SELECT job_id, character_id, skin_hex, ship_type_id, tier, licenses, due_ms, state, created_ms, completed_ms
            FROM {schema}.skinr_sequencing_jobs
            WHERE character_id = $1 {{stateClause}}
            ORDER BY created_ms, job_id
            """));
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SkinJob>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadJob(reader));
        return result;
    }

    private async Task<SkinJob?> ReadJobAsync(NpgsqlDataSource dataSource, Guid id, long character, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(store.Q("""
            SELECT job_id, character_id, skin_hex, ship_type_id, tier, licenses, due_ms, state, created_ms, completed_ms
            FROM {schema}.skinr_sequencing_jobs
            WHERE job_id = $1 AND character_id = $2
            """));
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    private static SkinJob ReadJob(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
        reader.GetInt32(5), reader.GetInt64(6), reader.GetString(7), reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetInt64(9));

    private static byte[] EncodeInitiateResponse(SkinJob job)
    {
        var response = new ProtoWriter();
        response.Message(1, Uuid(job.Id));
        response.Message(2, EncodeJobAttributes(job));
        return response.ToArray();
    }

    private static byte[] EncodeJobAttributes(SkinJob job)
    {
        var attributes = new ProtoWriter();
        var skin = new ProtoWriter();
        skin.String(1, job.SkinHex);
        attributes.Message(1, skin.ToArray());
        attributes.Message(2, Sequential(job.CharacterId));
        attributes.Uint(3, (ulong)job.Quantity);
        if (job.State == "completed")
            attributes.Message(7, Timestamp(job.CompletedMs ?? job.DueMs));
        else
            attributes.Message(4, []);
        attributes.Message(8, Timestamp(job.DueMs));
        return attributes.ToArray();
    }

    private static byte[] EncodeSkinDesign(SkinDesign design)
    {
        var payload = new ProtoWriter();
        payload.Message(1, Sequential(design.CharacterId));
        payload.String(2, design.Name);
        payload.String(3, design.LineName);
        payload.Bytes(4, design.Layout);
        var tier = new ProtoWriter();
        tier.Uint(1, (ulong)design.Tier);
        payload.Message(5, tier.ToArray());
        payload.Message(6, Timestamp(design.CreatedMs));
        return payload.ToArray();
    }

    private async Task<int> SkillPolicyAsync(long character, CancellationToken cancellationToken, string policy)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, typeId) in skillTypeIds)
            levels[name] = await skills.LevelAsync(connection, transaction, character, typeId, cancellationToken);
        return policy switch
        {
            "maxRuns" => 1 + SkillValue(levels, "Batch Sequencing", BatchRunsByLevel) +
                            SkillValue(levels, "Mass Sequencing", MassRunsByLevel) +
                            SkillValue(levels, "Industrial Sequencing", IndustrialRunsByLevel),
            "maxConcurrentJobs" => 1 + SkillValue(levels, "Parallel Sequencing", ParallelJobsByLevel) +
                                   SkillValue(levels, "Multi-thread Sequencing", MultiThreadJobsByLevel),
            "durationBonusPercent" => Math.Min(60,
                SkillValue(levels, "Basic Induction", DurationByLevel) +
                SkillValue(levels, "Induction Efficiency", DurationByLevel) +
                SkillValue(levels, "Advanced Induction Efficiency", DurationByLevel) +
                SkillValue(levels, "Peak Induction Acceleration", DurationByLevel)),
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
    }

    private static int SkillValue(IReadOnlyDictionary<string, int> levels, string name, IReadOnlyList<int> values)
    {
        var level = Math.Clamp(levels.GetValueOrDefault(name), 0, 5);
        return values[level];
    }

    private async Task<int> ActiveJobCountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long character, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(store.Q("""
            SELECT count(*) FROM {schema}.skinr_sequencing_jobs
            WHERE character_id = $1 AND state = 'pending'
            """), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task<Dictionary<int, long>?> ValidateAndConsumeComponentLicensesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long character,
        IReadOnlyDictionary<int, ComponentNeed> needs, long now, CancellationToken cancellationToken)
    {
        var binderNeeds = new Dictionary<int, long>();
        foreach (var (componentId, need) in needs)
        {
            await using var command = new NpgsqlCommand(store.Q("""
                SELECT finite_uses, infinite FROM {schema}.skinr_component_licenses
                WHERE character_id = $1 AND component_id = $2 FOR UPDATE
                """), connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = character });
            command.Parameters.Add(new NpgsqlParameter { Value = componentId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var finite = reader.GetInt64(0);
            var infinite = reader.GetBoolean(1);
            await reader.DisposeAsync();

            if (need.Infinite)
            {
                if (!infinite) return null;
                var component = catalog.ComponentFor(componentId);
                if (component is null || component.BinderTypeId <= 0 || component.BinderCount <= 0) return null;
                binderNeeds[component.BinderTypeId] = checked(binderNeeds.GetValueOrDefault(component.BinderTypeId) +
                    (long)component.BinderCount * need.Quantity);
            }
            else
            {
                if (finite < need.Quantity) return null;
                await using var update = new NpgsqlCommand(store.Q("""
                    UPDATE {schema}.skinr_component_licenses
                    SET finite_uses = finite_uses - $3, updated_ms = $4
                    WHERE character_id = $1 AND component_id = $2
                    """), connection, transaction);
                update.Parameters.Add(new NpgsqlParameter { Value = character });
                update.Parameters.Add(new NpgsqlParameter { Value = componentId });
                update.Parameters.Add(new NpgsqlParameter { Value = need.Quantity });
                update.Parameters.Add(new NpgsqlParameter { Value = now });
                await update.ExecuteNonQueryAsync(cancellationToken);
                if (finite == need.Quantity)
                {
                    await using var remove = new NpgsqlCommand(store.Q("""
                        DELETE FROM {schema}.skinr_component_licenses
                        WHERE character_id = $1 AND component_id = $2 AND finite_uses = 0 AND NOT infinite
                        """), connection, transaction);
                    remove.Parameters.Add(new NpgsqlParameter { Value = character });
                    remove.Parameters.Add(new NpgsqlParameter { Value = componentId });
                    await remove.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
        return binderNeeds;
    }

    private async Task<IReadOnlyList<InventoryChange>?> ConsumeBindersAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long character,
        IReadOnlyDictionary<int, long> required, CancellationToken cancellationToken)
    {
        if (required.Count == 0) return Array.Empty<InventoryChange>();
        var session = sessions.Of(character);
        var location = session?.GetInt("stationid") ?? session?.GetInt("stationID") ??
                       session?.GetInt("structureid") ?? session?.GetInt("shipid") ?? 0;
        if (location <= 0) return null;
        var itemIds = new List<long>();
        await using (var query = new NpgsqlCommand(
            $"""
            SELECT id FROM {_inventorySchema}.items
            WHERE owner_id = $1 AND location_id = $2 AND flag IN (4, 5)
              AND type_id = ANY($3) AND quantity > 0
            ORDER BY id
            """, connection, transaction))
        {
            query.Parameters.Add(new NpgsqlParameter { Value = character });
            query.Parameters.Add(new NpgsqlParameter { Value = location });
            query.Parameters.Add(new NpgsqlParameter { Value = required.Keys.ToArray() });
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) itemIds.Add(reader.GetInt64(0));
        }

        var stacks = new List<Item>();
        foreach (var itemId in itemIds)
        {
            var item = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
            if (item is not null && item.OwnerId == character && item.LocationId == location &&
                item.Flag is 4 or 5 && required.ContainsKey(item.TypeId))
                stacks.Add(item);
        }
        var changes = new List<InventoryChange>();
        foreach (var typeId in required.Keys.Order())
        {
            var remaining = required[typeId];
            foreach (var item in stacks.Where(item => item.TypeId == typeId).OrderBy(item => item.Id))
            {
                if (remaining == 0) break;
                var take = Math.Min(remaining, item.Quantity);
                var remainingQuantity = item.Quantity - take;
                if (remainingQuantity == 0) await items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
                else await items.SetQuantityAsync(connection, transaction, item.Id, remainingQuantity, cancellationToken);
                changes.Add(new InventoryChange(item, remainingQuantity));
                remaining -= take;
            }
            if (remaining > 0) return null;
        }
        return changes;
    }

    private async Task<byte[]> ReadSavedEditorConfigurationsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long character, string skinHex, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(store.Q("""
            SELECT editor_configurations FROM {schema}.skinr_design_drafts
            WHERE character_id = $1 AND skin_hex = $2
            ORDER BY updated_ms DESC, draft_id LIMIT 1
            """), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        command.Parameters.Add(new NpgsqlParameter { Value = skinHex });
        return await command.ExecuteScalarAsync(cancellationToken) as byte[] ?? [];
    }

    private async Task<SkinLicense?> ReadSkinLicenseAsync(long character, string hex, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand(store.Q("""
            SELECT activated, unactivated, escrowed, first_acquired_ms, updated_ms
            FROM {schema}.skinr_ship_licenses WHERE character_id = $1 AND skin_hex = $2
            """));
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        command.Parameters.Add(new NpgsqlParameter { Value = hex });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadLicense(reader) : null;
    }

    private async Task<SkinLicense?> ReadSkinLicenseAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long character, string hex,
        bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(store.Q($$"""
            SELECT activated, unactivated, escrowed, first_acquired_ms, updated_ms
            FROM {schema}.skinr_ship_licenses
            WHERE character_id = $1 AND skin_hex = $2 {{(forUpdate ? "FOR UPDATE" : "")}}
            """), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        command.Parameters.Add(new NpgsqlParameter { Value = hex });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadLicense(reader) : null;
    }

    private static SkinLicense ReadLicense(NpgsqlDataReader reader) => new(
        reader.GetBoolean(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));

    private async Task<ReceiptResult> ReadReceiptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long character, string operation,
        Guid requestId, string fingerprint, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(store.Q("""
            SELECT fingerprint FROM {schema}.skinr_request_receipts
            WHERE character_id = $1 AND operation = $2 AND request_id = $3
            """), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        command.Parameters.Add(new NpgsqlParameter { Value = operation });
        command.Parameters.Add(new NpgsqlParameter { Value = requestId });
        var prior = await command.ExecuteScalarAsync(cancellationToken) as string;
        return prior is null ? ReceiptResult.New :
            StringComparer.Ordinal.Equals(prior, fingerprint) ? ReceiptResult.Replay : ReceiptResult.Conflict;
    }

    private async Task<Guid> ReceiptJobAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long character, Guid requestId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(store.Q("""
            SELECT result_id FROM {schema}.skinr_request_receipts
            WHERE character_id = $1 AND operation = 'sequence.initiate' AND request_id = $2
            """), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        command.Parameters.Add(new NpgsqlParameter { Value = requestId });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? id : Guid.Empty;
    }

    private async Task InsertReceiptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long character, string operation,
        Guid requestId, string fingerprint, Guid? resultId, long now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(store.Q("""
            INSERT INTO {schema}.skinr_request_receipts
                (character_id, operation, request_id, fingerprint, result_id, created_ms)
            VALUES ($1, $2, $3, $4, $5, $6)
            """), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        command.Parameters.Add(new NpgsqlParameter { Value = operation });
        command.Parameters.Add(new NpgsqlParameter { Value = requestId });
        command.Parameters.Add(new NpgsqlParameter { Value = fingerprint });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)resultId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private IReadOnlyDictionary<int, ComponentNeed>? SequenceComponents(byte[] payload, ReadOnlyMemory<byte> layout, long runs)
    {
        var counts = new Dictionary<int, long>();
        foreach (var field in ProtoReader.Fields(layout))
        {
            if (field.Number != 1 || field.WireType != 2) continue;
            var (_, componentId, _) = ShipSkinrCatalog.ReadSlot(field.Bytes);
            if (componentId <= 0) return null;
            counts[componentId] = counts.GetValueOrDefault(componentId) + runs;
        }

        var supplied = new Dictionary<int, ComponentNeed>();
        foreach (var field in ProtoReader.Fields(payload))
        {
            if (field.Number != 4 || field.WireType != 2) continue;
            var component = ProtoReader.Fields(field.Bytes);
            ReadOnlyMemory<byte> identifier = default;
            long finite = 0;
            var infinite = false;
            foreach (var value in component)
            {
                if (value.Number == 1 && value.WireType == 2) identifier = value.Bytes;
                if (value.Number == 2 && value.WireType == 0) finite = (long)Math.Min(long.MaxValue, value.Scalar);
                if (value.Number == 3 && value.WireType == 0) infinite = value.Scalar != 0;
            }
            if (identifier.IsEmpty || infinite == (finite > 0)) return null;
            var (id, category) = ComponentIdentifier(identifier);
            if (id <= 0 || !counts.ContainsKey(id) || supplied.ContainsKey(id) ||
                catalog.ComponentFor(id) is not { } componentData || componentData.CategoryId != category)
                return null;
            if (infinite)
                supplied[id] = new ComponentNeed(true, counts[id]);
            else if (finite == counts[id])
                supplied[id] = new ComponentNeed(false, finite);
            else return null;
        }
        return supplied.Count == counts.Count ? supplied : null;
    }

    private sealed record InventoryChange(Item Item, long RemainingQuantity);

    private void NotifyInventoryChanged(long character, Item item, long remainingQuantity)
    {
        if (sessions.Of(character) is not { } session) return;
        var type = catalog.ItemInfoFor(item.TypeId)
            ?? throw new InvalidOperationException($"the pinned SDE has no group/category for inventory type {item.TypeId}");
        var row = new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag,
            remainingQuantity, type.GroupId, type.CategoryId);
        var changes = new Eve.Marshal.PyDict([
            new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(
                new Eve.Marshal.PyInt(9), InventoryWire.Id(item.Quantity)),
        ]);
        session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(row), changes,
            Eve.Marshal.PyNone.Instance);
    }

    private static byte[] EncodeLicenseAttributes(SkinLicense license)
    {
        var attributes = new ProtoWriter();
        attributes.Bool(1, license.Activated);
        attributes.Uint(2, (ulong)license.Unactivated);
        attributes.Uint(3, (ulong)license.Escrowed);
        attributes.Message(4, Timestamp(license.FirstAcquiredMs));
        return attributes.ToArray();
    }

    private static byte[] LicenseIdentifier(long character, string hex)
    {
        var identifier = new ProtoWriter();
        identifier.Message(1, Sequential(character));
        var skin = new ProtoWriter();
        skin.String(1, hex);
        identifier.Message(2, skin.ToArray());
        return identifier.ToArray();
    }

    private void PublishLicenseUpdate(long character, string hex, SkinLicense current, SkinLicense previous)
    {
        var payload = new ProtoWriter();
        payload.Message(1, LicenseIdentifier(character, hex));
        payload.Message(2, EncodeLicenseAttributes(current));
        payload.Message(3, EncodeLicenseAttributes(previous));
        notices.Publish(Prefix + "license.api.UpdatedNotice", payload.ToArray(), character: character);
    }

    private static byte[] ComponentLicenseKind(int id, int category, long? finite, bool infinite)
    {
        var kind = new ProtoWriter();
        kind.Message(1, ComponentIdentifier(id, category));
        if (infinite) kind.Bool(3, true);
        else kind.Uint(2, (ulong)(finite ?? 0));
        return kind.ToArray();
    }

    private static byte[] ComponentIdentifier(int id, int category)
    {
        var component = new ProtoWriter();
        component.Message(category == 2 ? 2 : 1, Sequential(id));
        return component.ToArray();
    }

    private static (long Character, int Component, int Category) ComponentLicenseId(byte[] payload, int field)
    {
        var outer = Nested(payload, field);
        if (outer is null) return (0, 0, 0);
        var character = Positive(outer.Value, 1, 1);
        var component = Nested(outer.Value, 2);
        if (component is null) return (character, 0, 0);
        var (id, category) = ComponentIdentifier(component.Value);
        return (character, id, category);
    }

    private static (int Id, int Category) ComponentIdentifier(ReadOnlyMemory<byte> message)
    {
        foreach (var field in ProtoReader.Fields(message))
        {
            if (field.Number is not (1 or 2) || field.WireType != 2) continue;
            var id = PositiveSequential(field.Bytes);
            return (checked((int)id), field.Number == 2 ? 2 : 1);
        }
        return (0, 0);
    }

    private static bool TryLicenseIdentifier(byte[] payload, int field, out long character, out string skinHex)
    {
        character = 0;
        skinHex = "";
        var identifier = Nested(payload, field);
        if (identifier is null) return false;
        character = Positive(identifier.Value, 1, 1);
        var skin = Nested(identifier.Value, 2);
        skinHex = skin is { } value ? StringValue(value, 1) : "";
        return IsHex(skinHex);
    }


    private static byte[] Uuid(Guid id)
    {
        var identifier = new ProtoWriter();
        identifier.Bytes(1, id.ToByteArray(bigEndian: true));
        return identifier.ToArray();
    }

    private static byte[] Sequential(long value)
    {
        var identifier = new ProtoWriter();
        identifier.Uint(1, (ulong)value);
        return identifier.ToArray();
    }

    private static byte[] Timestamp(long unixMilliseconds)
    {
        var timestamp = new ProtoWriter();
        var seconds = Math.DivRem(unixMilliseconds, 1000, out var milliseconds);
        timestamp.Uint(1, (ulong)seconds);
        timestamp.Uint(2, (ulong)(milliseconds * 1_000_000));
        return timestamp.ToArray();
    }

    private static Guid RequestId(GatewayRequest request) =>
        request.CorrelationUuid.Length == 16 ? new Guid(request.CorrelationUuid, bigEndian: true) : Guid.Empty;

    private static Guid GuidField(byte[] payload, int field)
    {
        var identifier = Nested(payload, field);
        if (identifier is null) return Guid.Empty;
        foreach (var value in ProtoReader.Fields(identifier.Value))
            if (value.Number == 1 && value.WireType == 2 && value.Bytes.Length == 16)
                return new Guid(value.Bytes.Span, bigEndian: true);
        return Guid.Empty;
    }

    private static long NestedSequential(byte[] payload, int first, int second, int third) =>
        Positive(payload, first, second, third);

    private static long Positive(ReadOnlyMemory<byte> payload, int first, int second, int third)
    {
        var current = payload;
        foreach (var fieldNumber in new[] { first, second })
        {
            var nested = Nested(current, fieldNumber);
            if (nested is null) return 0;
            current = nested.Value;
        }
        var value = ProtoReader.Fields(current).FirstOrDefault(field => field.Number == third && field.WireType == 0).Scalar;
        return value is > 0 and <= long.MaxValue ? (long)value : 0;
    }

    private static long Positive(ReadOnlyMemory<byte> payload, int nestedField, int valueField)
    {
        var nested = Nested(payload, nestedField);
        if (nested is null) return 0;
        var value = ProtoReader.Fields(nested.Value).FirstOrDefault(field => field.Number == valueField && field.WireType == 0).Scalar;
        return value is > 0 and <= long.MaxValue ? (long)value : 0;
    }

    private static ReadOnlyMemory<byte>? Nested(ReadOnlyMemory<byte> payload, int number)
    {
        foreach (var field in ProtoReader.Fields(payload))
            if (field.Number == number && field.WireType == 2) return field.Bytes;
        return null;
    }

    private static long Varint(byte[] payload, int field)
    {
        foreach (var value in ProtoReader.Fields(payload))
            if (value.Number == field && value.WireType == 0 && value.Scalar <= long.MaxValue) return (long)value.Scalar;
        return 0;
    }

    private static long PositiveSequential(ReadOnlyMemory<byte> payload)
    {
        foreach (var field in ProtoReader.Fields(payload))
            if (field.Number == 1 && field.WireType == 0 && field.Scalar is > 0 and <= int.MaxValue)
                return (long)field.Scalar;
        return 0;
    }

    private static string StringValue(byte[] payload, int field)
    {
        foreach (var value in ProtoReader.Fields(payload))
            if (value.Number == field && value.WireType == 2) return Encoding.UTF8.GetString(value.Bytes.Span);
        return "";
    }

    private static string StringValue(ReadOnlyMemory<byte> payload, int field)
    {
        foreach (var value in ProtoReader.Fields(payload))
            if (value.Number == field && value.WireType == 2) return Encoding.UTF8.GetString(value.Bytes.Span);
        return "";
    }

    private static bool IsHex(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidName(string name, string lineName) =>
        name.EnumerateRunes().Count() is > 0 and <= 20 &&
        lineName.EnumerateRunes().Count() <= 50 &&
        !(name + lineName).Any(char.IsControl);

    private static bool TryParseDesign(byte[] payload, out SequenceDesign design)
    {
        var name = "";
        var lineName = "";
        ReadOnlyMemory<byte> layout = default;
        try
        {
            foreach (var field in ProtoReader.Fields(payload))
            {
                if (field.Number == 1 && field.WireType == 2) name = Encoding.UTF8.GetString(field.Bytes.Span);
                else if (field.Number == 2 && field.WireType == 2) lineName = Encoding.UTF8.GetString(field.Bytes.Span);
                else if (field.Number == 3 && field.WireType == 2) layout = field.Bytes;
            }
        }
        catch (FormatException)
        {
            design = default;
            return false;
        }
        design = new SequenceDesign(name, lineName, layout);
        return !layout.IsEmpty;
    }

    private static string Fingerprint(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static bool ValidCharacter(GatewayRequest request) => request.CharacterId is > 0 and <= long.MaxValue;

    private static GatewayAnswer Success(string type, byte[] payload) => new(200, "OK", type, payload);

    private static GatewayAnswer Error(uint status, string message, string responseType) => new(status, message, responseType, []);

    private enum ReceiptResult { New, Replay, Conflict }
    private sealed record ComponentNeed(bool Infinite, long Quantity);
    private sealed record SkinLicense(bool Activated, long Unactivated, long Escrowed, long FirstAcquiredMs, long UpdatedMs);
    private sealed record SkinDesign(long CharacterId, string Name, string LineName, int ShipTypeId, int Tier, byte[] Layout, byte[] EditorConfigurations, long CreatedMs);
    private sealed record SkinJob(Guid Id, long CharacterId, string SkinHex, int ShipTypeId, int Tier, int Quantity, long DueMs, string State, long CreatedMs, long? CompletedMs);
    private readonly record struct SequenceDesign(string Name, string LineName, ReadOnlyMemory<byte> Layout);
}
