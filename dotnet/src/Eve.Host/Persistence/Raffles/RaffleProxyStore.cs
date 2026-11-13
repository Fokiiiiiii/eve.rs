using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Eve.Wallet;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Npgsql;
using Eve.Scheduler;

namespace Eve.Host.Persistence;

public sealed class RaffleProxyStore(NpgsqlDataSource source, PgItemStore items, WalletTransactions wallets, long plexPrice = 3_500_000, string schema = "raffle", string sdeSchema = Eve.Sde.SdeReader.DefaultSchema, string identitySchema = Eve.Identity.PgAccountDirectory.DefaultSchema, Func<long>? now = null)
{
    private const long SystemOwnerId = 1;
    private const int WalletAdjustmentRefType = 3; // EVEJS walletState default: GM_CASH_TRANSFER
    private const decimal TaxRate = 0.05m;
    private readonly long _plexPrice = plexPrice;
    private readonly Func<long> _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private CharacterSessions? _characterSessions;
    private Func<ServiceCall, CancellationToken, ValueTask<double?>>? _plexPriceProvider;
    private Func<bool>? _hyperNetKillSwitchProvider;

    public void SetCharacterSessions(CharacterSessions sessions) =>
        _characterSessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    // Called by composition once the live global-config store is available.
    public void SetPlexPriceProvider(Func<ServiceCall, CancellationToken, ValueTask<double?>> provider) =>
        _plexPriceProvider = provider ?? throw new ArgumentNullException(nameof(provider));

    public void SetHyperNetKillSwitchProvider(Func<bool> provider) =>
        _hyperNetKillSwitchProvider = provider ?? throw new ArgumentNullException(nameof(provider));
    private readonly string _schema = Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$")
        ? schema : throw new ArgumentException("not a usable schema name", nameof(schema));
    private readonly string _sdeSchema = Regex.IsMatch(sdeSchema, "^[a-z_][a-z0-9_]{0,62}$")
        ? sdeSchema : throw new ArgumentException("not a usable SDE schema name", nameof(sdeSchema));
    private readonly string _identitySchema = Regex.IsMatch(identitySchema, "^[a-z_][a-z0-9_]{0,62}$")
        ? identitySchema : throw new ArgumentException("not a usable identity schema name", nameof(identitySchema));

    private readonly ConcurrentDictionary<long, ClientSession> _ticketSubscribers = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, ClientSession>> _raffleSubscribers = new();


    private const string ExpiryJobKind = "raffles.expire";
    private sealed record ExpiryPayload(long RaffleId);
    private DurableScheduler? _scheduler;

    public void RegisterScheduler(DurableScheduler scheduler)
    {
        _scheduler = scheduler;
        scheduler.Register(ExpiryJobKind, async (job, cancellationToken) =>
        {
            var payload = job.Payload<ExpiryPayload>()
                ?? throw new InvalidDataException("A raffle expiry job has no payload.");
            if (payload.RaffleId <= 0) throw new InvalidDataException("A raffle expiry job has an invalid raffle id.");
            var expired = await ExpireRaffleAsync(job.Connection, job.Transaction, payload.RaffleId,
                job.NowMs, cancellationToken);
            if (expired is not null)
                job.AfterCommit(ct => NotifyExpiredRaffleAsync(expired, ct));
        });
    }

    public async Task ScheduleExistingAsync(CancellationToken cancellationToken = default)
    {
        if (_scheduler is not { } scheduler) return;
        var jobs = new List<(long RaffleId, long ExpiresAt)>();
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand($"SELECT raffle_id,expires_at_ms FROM {_schema}.raffle_records WHERE status=6 ORDER BY raffle_id", connection))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) jobs.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }
        await using var jobConnection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await jobConnection.BeginTransactionAsync(cancellationToken);
        foreach (var (raffleId, expiresAt) in jobs)
            await ScheduleExpiryAsync(jobConnection, transaction, scheduler, raffleId, expiresAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static Task ScheduleExpiryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DurableScheduler scheduler, long raffleId, long expiresAtMs, CancellationToken cancellationToken) =>
        scheduler.ScheduleAsync(connection, transaction, new JobSpec(ExpiryJobKind, expiresAtMs)
        {
            Key = raffleId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Payload = new ExpiryPayload(raffleId),
        }, cancellationToken);

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            CREATE SCHEMA IF NOT EXISTS {_schema};
            CREATE TABLE IF NOT EXISTS {_schema}.raffle_records (
                raffle_id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                running_id BIGINT GENERATED BY DEFAULT AS IDENTITY UNIQUE,
                owner_id BIGINT NOT NULL,
                item_id BIGINT NOT NULL,
                type_id INTEGER NOT NULL,
                location_id BIGINT NOT NULL,
                escrow_location_id BIGINT NULL,
                solar_system_id BIGINT NULL,
                group_id INTEGER NOT NULL DEFAULT 0,
                category_id INTEGER NOT NULL DEFAULT 0,
                meta_group_id INTEGER NOT NULL DEFAULT 0,
                is_copy BOOLEAN NOT NULL DEFAULT FALSE,
                ticket_count INTEGER NOT NULL CHECK (ticket_count IN (8,16,48,512)),
                ticket_price NUMERIC(20,2) NOT NULL CHECK (ticket_price > 0),
                restriction_id INTEGER NULL,
                created_at_ms BIGINT NOT NULL,
                expires_at_ms BIGINT NOT NULL,
                sold_ticket_count INTEGER NOT NULL DEFAULT 0 CHECK (sold_ticket_count >= 0),
                pending_isk NUMERIC(20,2) NOT NULL DEFAULT 0 CHECK (pending_isk >= 0),
                status SMALLINT NOT NULL DEFAULT 6,
                winning_ticket INTEGER NULL,
                ended_at_ms BIGINT NULL
            );
            ALTER TABLE {_schema}.raffle_records ADD COLUMN IF NOT EXISTS running_id BIGINT GENERATED BY DEFAULT AS IDENTITY;
            ALTER TABLE {_schema}.raffle_records ADD COLUMN IF NOT EXISTS group_id INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE {_schema}.raffle_records ADD COLUMN IF NOT EXISTS category_id INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE {_schema}.raffle_records ADD COLUMN IF NOT EXISTS meta_group_id INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE {_schema}.raffle_records ADD COLUMN IF NOT EXISTS is_copy BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE {_schema}.raffle_records ADD COLUMN IF NOT EXISTS escrow_location_id BIGINT NULL;
            ALTER TABLE {_schema}.raffle_records ALTER COLUMN escrow_location_id DROP NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS raffle_records_running_id ON {_schema}.raffle_records(running_id);
            CREATE TABLE IF NOT EXISTS {_schema}.raffle_tickets (
                raffle_id BIGINT NOT NULL REFERENCES {_schema}.raffle_records(raffle_id),
                ticket_number INTEGER NOT NULL,
                owner_id BIGINT NOT NULL,
                purchased_at_ms BIGINT NOT NULL,
                PRIMARY KEY (raffle_id, ticket_number)
            );
            CREATE INDEX IF NOT EXISTS raffle_tickets_owner_history
                ON {_schema}.raffle_tickets(owner_id, purchased_at_ms DESC);
            CREATE TABLE IF NOT EXISTS {_schema}.raffle_bulk_operations (
                operation_key TEXT PRIMARY KEY,
                raffle_id BIGINT NOT NULL,
                ticket_count INTEGER NOT NULL,
                ticket_numbers INTEGER[] NOT NULL
            );
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Recover prizes created by the previous system-owned escrow representation before
        // clearing the legacy location. The row and item changes commit together.
        var legacy = new List<(long RaffleId, long ItemId, long OwnerId, long StationId, long EscrowId)>();
        await using (var query = new NpgsqlCommand($"SELECT raffle_id,item_id,owner_id,location_id,escrow_location_id FROM {_schema}.raffle_records WHERE status IN (6,7) AND escrow_location_id IS NOT NULL FOR UPDATE", connection, transaction))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                legacy.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
        foreach (var row in legacy)
        {
            await items.ReleaseRaffleEscrowAsync(connection, transaction, row.ItemId, SystemOwnerId, row.EscrowId, row.OwnerId, row.StationId, cancellationToken);
            await items.PutInRaffleEscrowAsync(connection, transaction, row.ItemId, row.OwnerId, row.StationId, cancellationToken);
            await using var clear = new NpgsqlCommand($"UPDATE {_schema}.raffle_records SET escrow_location_id=NULL WHERE raffle_id=$1", connection, transaction);
            clear.Parameters.AddWithValue(row.RaffleId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Debits a buyer, reserves one unique ticket and settles a sold-out raffle atomically.</summary>



    public async Task ExpireDueAsync(long nowUnixMs, CancellationToken cancellationToken = default)
    {
        var due = new List<long>();
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand($"SELECT raffle_id FROM {_schema}.raffle_records WHERE status=6 AND expires_at_ms <= $1 ORDER BY raffle_id LIMIT 100", connection))
        {
            command.Parameters.AddWithValue(nowUnixMs);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) due.Add(reader.GetInt64(0));
        }
        foreach (var raffleId in due)
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var expired = await ExpireRaffleAsync(connection, transaction, raffleId, nowUnixMs, cancellationToken);
            if (expired is null) { await transaction.RollbackAsync(cancellationToken); continue; }
            await transaction.CommitAsync(cancellationToken);
            await NotifyExpiredRaffleAsync(expired, cancellationToken);
        }
    }

    private sealed record ExpiredRaffle(long RaffleId, long OwnerId, ItemRow RestoredItem,
        IReadOnlyList<WalletBalanceChange> WalletChanges);

    private async Task<ExpiredRaffle?> ExpireRaffleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long raffleId, long nowUnixMs, CancellationToken cancellationToken)
    {
        long ownerId, itemId, locationId;
        int groupId, categoryId;
        decimal ticketPrice;
        await using (var command = new NpgsqlCommand($"""
            SELECT owner_id,item_id,location_id,ticket_price,group_id,category_id
            FROM {_schema}.raffle_records
            WHERE raffle_id=$1 AND status=6 AND expires_at_ms <= $2
            FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(raffleId);
            command.Parameters.AddWithValue(nowUnixMs);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            ownerId=reader.GetInt64(0); itemId=reader.GetInt64(1); locationId=reader.GetInt64(2);
            ticketPrice=reader.GetDecimal(3); groupId=reader.GetInt32(4); categoryId=reader.GetInt32(5);
        }
        var owners = new List<(long Owner, long Count)>();
        await using (var command = new NpgsqlCommand($"SELECT owner_id,count(*) FROM {_schema}.raffle_tickets WHERE raffle_id=$1 GROUP BY owner_id ORDER BY owner_id", connection, transaction))
        {
            command.Parameters.AddWithValue(raffleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) owners.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }
        var walletChanges = new List<WalletBalanceChange>();
        foreach (var (ticketOwner, count) in owners)
        {
            var balance = await wallets.AdjustAsync(connection, transaction, ticketOwner, ticketPrice * count, ownerId,
                WalletAdjustmentRefType, $"HyperNet raffle refund {raffleId}", nowUnixMs,
                $"raffle:{raffleId}:refund:{ticketOwner}", cancellationToken, raffleId);
            walletChanges.Add(new WalletBalanceChange(ticketOwner, 1000, balance));
        }
        var escrowedItem = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
        if (escrowedItem is not { Flag: ItemFlags.RaffleEscrow, OwnerId: var escrowOwner } || escrowOwner != ownerId || escrowedItem.LocationId != locationId)
            throw new InvalidOperationException("raffles.FailureToDeliverItemError");
        var restoredItem = await items.ReleaseRaffleEscrowAsync(connection, transaction, itemId, ownerId, locationId, ownerId, locationId, cancellationToken);
        var restoredRow = InventoryRow(restoredItem, groupId, categoryId);
        await using (var update = new NpgsqlCommand($"UPDATE {_schema}.raffle_records SET status=9,winning_ticket=NULL,ended_at_ms=$2,pending_isk=0 WHERE raffle_id=$1 AND status=6", connection, transaction))
        {
            update.Parameters.AddWithValue(raffleId); update.Parameters.AddWithValue(nowUnixMs);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("RAFFLE_EXPIRY_CONFLICT");
        }
        return new ExpiredRaffle(raffleId, ownerId, restoredRow, walletChanges);
    }

    private async Task NotifyExpiredRaffleAsync(ExpiredRaffle expired, CancellationToken cancellationToken)
    {
        foreach (var change in expired.WalletChanges) AnnounceWalletChangeSafely(expired.RaffleId, change);
        NotifyItemChangeForCharacterSafely(expired.OwnerId, null, expired.RaffleId, expired.RestoredItem,
            new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(4), new PyInt(ItemFlags.RaffleEscrow))]));
        var raffleData = await ReadRaffleWireAsync(expired.RaffleId, cancellationToken);
        var recipients = await InterestedAsync(expired.RaffleId, null, cancellationToken);
        foreach (var session in recipients)
        {
            NotifySafely(session, expired.RaffleId, "OnRaffleUpdatedServer", new PyValue[] { new PyLong(expired.RaffleId), raffleData });
            NotifySafely(session, expired.RaffleId, "OnRaffleFinishedServer", new PyValue[] { new PyLong(expired.RaffleId), PyNone.Instance }, "charid");
        }
    }

    public async Task AwardItemAsync(ServiceCall call, long raffleId, CancellationToken cancellationToken = default)
    {
        var winnerId = call.Session.CharacterId ?? 0;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long ownerId, itemId, locationId;
        int groupId, categoryId;
        int? winningTicket;
        short status;
        await using (var command = new NpgsqlCommand($"SELECT owner_id,item_id,location_id,winning_ticket,status,group_id,category_id FROM {_schema}.raffle_records WHERE raffle_id=$1 FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue(raffleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("raffles.FailureToDeliverItemError");
            ownerId=reader.GetInt64(0); itemId=reader.GetInt64(1); locationId=reader.GetInt64(2);
            winningTicket=reader.IsDBNull(3)?null:reader.GetInt32(3); status=reader.GetInt16(4);
            groupId=reader.GetInt32(5); categoryId=reader.GetInt32(6);
        }
        if (status != 7 || winnerId <= 0 || winningTicket is null)
            throw new InvalidOperationException("raffles.FailureToDeliverItemError");
        await using (var ticket = new NpgsqlCommand($"SELECT owner_id FROM {_schema}.raffle_tickets WHERE raffle_id=$1 AND ticket_number=$2", connection, transaction))
        {
            ticket.Parameters.AddWithValue(raffleId); ticket.Parameters.AddWithValue(winningTicket.Value);
            var holder = await ticket.ExecuteScalarAsync(cancellationToken);
            if (holder is not long holderId || holderId != winnerId) throw new InvalidOperationException("raffles.FailureToDeliverItemError");
        }
        var prize = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
        if (prize is null || prize.OwnerId != ownerId || prize.LocationId != locationId || prize.Flag != ItemFlags.RaffleEscrow)
            throw new InvalidOperationException("raffles.FailureToDeliverItemError");
        var awardedItem = await items.ReleaseRaffleEscrowAsync(connection, transaction, itemId, ownerId, locationId, winnerId, locationId, cancellationToken);
        var row = InventoryRow(awardedItem, groupId, categoryId);
        await using (var update = new NpgsqlCommand($"UPDATE {_schema}.raffle_records SET status=8 WHERE raffle_id=$1 AND status=7", connection, transaction))
        {
            update.Parameters.AddWithValue(raffleId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("raffles.FailureToDeliverItemError");
        }
        await transaction.CommitAsync(cancellationToken);
        var oldValues = new PyDict([
            new(new PyInt(2), new PyInt(ownerId)),
            new(new PyInt(4), new PyInt(ItemFlags.RaffleEscrow)),
        ]);
        NotifyItemChangeForCharacterSafely(winnerId, call.Session, raffleId, row, oldValues);
        if (ownerId != winnerId)
            NotifyItemChangeForCharacterSafely(ownerId, null, raffleId, row, oldValues);
    }

    public async Task<long> CreateRaffleAsync(ServiceCall call, long nowUnixMs, double plexPrice,
        CancellationToken cancellationToken = default)
    {
        var ownerId = call.Session.CharacterId ?? 0;
        var stationId = call.Session.DockedLocationId ?? 0;
        if (ownerId <= 0) throw new InvalidOperationException("ItemOwnerError");
        if (stationId <= 0) throw new InvalidOperationException("ItemLocationError");
        var data = call.Arg(0);
        var requestedOwner = ReadField(data, "owner_id");
        if (requestedOwner is not null && CreationInteger(requestedOwner, "ItemOwnerError") != ownerId)
            throw new InvalidOperationException("ItemOwnerError");
        var tokenId = CreationInteger(ReadField(data, "token_id"), "TokenPaymentError");
        var tokenLocationId = CreationInteger(ReadField(data, "token_location_id"), "TokenLocationError");
        var itemId = CreationInteger(ReadField(data, "item_id"), "ItemEscrowError");
        var typeId = checked((int)CreationInteger(ReadField(data, "type_id"), "ItemTypeError"));
        var locationId = CreationInteger(ReadField(data, "location_id"), "ItemLocationError");
        var ticketCount = checked((int)CreationInteger(ReadField(data, "ticket_count"), "TicketCountError"));
        decimal ticketPrice;
        try { ticketPrice = DecimalValue(ReadField(data, "ticket_price") ?? PyNone.Instance); }
        catch (InvalidOperationException) { throw new InvalidOperationException("TicketPriceError"); }
        var restrictionValue = ReadField(data, "restriction_id");
        int? restrictionId = restrictionValue is null or PyNone ? null : checked((int)CreationInteger(restrictionValue, "UnknownError"));
        if (restrictionId != 1) restrictionId = null;
        if (locationId != stationId) throw new InvalidOperationException("ItemLocationError");
        if (tokenLocationId != stationId) throw new InvalidOperationException("TokenLocationError");
        if (!new[] { 8, 16, 48, 512 }.Contains(ticketCount)) throw new InvalidOperationException("TicketCountError");
        if (ticketPrice <= 0 || decimal.Round(ticketPrice, 2) != ticketPrice) throw new InvalidOperationException("TicketPriceError");
        var total = ticketPrice * ticketCount;
        if (total < 1_000m || total > 10_000_000_000_000m)
            throw new InvalidOperationException("TicketPriceError");
        if (!double.IsFinite(plexPrice) || plexPrice == 0) throw new InvalidOperationException("TicketPriceError");
        // Client util.py enables true division and evaluates total / tokenPrice * fee left to right.
        double tokenPrice = Math.Max(1d, plexPrice / 10d);
        var requiredTokens = Math.Max(1L, (long)Math.Truncate((double)total / tokenPrice * 0.05d));
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Client requires an NPC station in a non-Triglavian system. Derive its system
        // from pinned SDE rather than trusting the creation request's filter metadata.
        long solarSystemId;
        await using (var station = new NpgsqlCommand($"""
            SELECT (s.doc->>'solarSystemID')::bigint,
                   (m.doc->>'regionID')::bigint, (m.doc->>'factionID')::bigint
            FROM {_sdeSchema}."npcStations" s
            JOIN {_sdeSchema}."mapSolarSystems" m ON m.key=s.doc->>'solarSystemID'
            WHERE s.key=$1
            """, connection, transaction))
        {
            station.Parameters.AddWithValue(stationId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await using var reader = await station.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0) || reader.IsDBNull(1))
                throw new InvalidOperationException("ItemLocationError");
            solarSystemId = reader.GetInt64(0);
            // Client tests faction 500026. Pinned Pochven documents can omit factionID;
            // the existing universe authority identifies Pochven by region 10000070.
            if (reader.GetInt64(1) == 10000070 || (!reader.IsDBNull(2) && reader.GetInt64(2) == 500026))
                throw new InvalidOperationException("ItemTriglavianSystemError");
        }
        if (ReadField(data, "solar_system_id") is { } declaredSystem && declaredSystem is not PyNone && Integer(declaredSystem) != solarSystemId)
            throw new InvalidOperationException("ItemLocationError");
        var prize = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
        var tokens = await items.LockAndGetAsync(connection, transaction, tokenId, cancellationToken);
        if (prize is null) throw new InvalidOperationException("ItemEscrowError");
        if (tokens is null) throw new InvalidOperationException("TokenPaymentError");
        if (prize.TypeId != typeId) throw new InvalidOperationException("TypeMismatchError");
        if (tokens.TypeId != 52568) throw new InvalidOperationException("TokenTypeError");
        if (prize.OwnerId != ownerId) throw new InvalidOperationException("ItemOwnerError");
        if (tokens.OwnerId != ownerId) throw new InvalidOperationException("TokenOwnerError");
        if (prize.Id == tokens.Id || prize.TypeId == 52568) throw new InvalidOperationException("ItemTypeError");
        if (prize.LocationId != stationId || locationId != stationId) throw new InvalidOperationException("ItemLocationError");
        if (tokens.LocationId != stationId || tokenLocationId != stationId) throw new InvalidOperationException("TokenLocationError");
        if (prize.Flag != ItemFlags.Hangar || (call.Session.GetInt("shipid") is { } activeShip && activeShip == prize.Id))
            throw new InvalidOperationException("ItemInventoryError");
        if (tokens.Flag != ItemFlags.Hangar || tokens.Singleton) throw new InvalidOperationException("TokenInventoryError");
        if (tokens.Quantity < requiredTokens) throw new InvalidOperationException("TokenAmountError");
        int groupId, categoryId, metaGroupId;
        bool dynamicType;
        await using (var typeCommand = new NpgsqlCommand($"""
            SELECT (t.doc->>'groupID')::integer, (g.doc->>'categoryID')::integer,
                   COALESCE((t.doc->>'metaGroupID')::integer,0), COALESCE((t.doc->>'isDynamicType')::boolean,FALSE)
            FROM {_sdeSchema}.types t
            JOIN {_sdeSchema}.groups g ON (g.doc->>'_key')::integer=(t.doc->>'groupID')::integer
            WHERE (t.doc->>'_key')::integer=$1
            """, connection, transaction))
        {
            typeCommand.Parameters.AddWithValue(typeId);
            await using var typeReader = await typeCommand.ExecuteReaderAsync(cancellationToken);
            if (!await typeReader.ReadAsync(cancellationToken)) throw new InvalidOperationException("ItemTypeError");
            groupId=typeReader.GetInt32(0); categoryId=typeReader.GetInt32(1); metaGroupId=typeReader.GetInt32(2); dynamicType=typeReader.GetBoolean(3);
        }
        int tokenGroupId, tokenCategoryId;
        await using (var typeCommand = new NpgsqlCommand($"""
            SELECT (t.doc->>'groupID')::integer, (g.doc->>'categoryID')::integer
            FROM {_sdeSchema}.types t
            JOIN {_sdeSchema}.groups g ON (g.doc->>'_key')::integer=(t.doc->>'groupID')::integer
            WHERE (t.doc->>'_key')::integer=$1
            """, connection, transaction))
        {
            typeCommand.Parameters.AddWithValue(tokens.TypeId);
            await using var typeReader = await typeCommand.ExecuteReaderAsync(cancellationToken);
            if (!await typeReader.ReadAsync(cancellationToken)) throw new InvalidOperationException("TokenTypeError");
            tokenGroupId = typeReader.GetInt32(0); tokenCategoryId = typeReader.GetInt32(1);
        }
        var lists = await RaffleTypeListsAsync(connection, transaction, cancellationToken);
        if (IsInTypeList(lists[36], typeId, groupId, categoryId) ||
            IsInTypeList(lists[142], typeId, groupId, categoryId) || IsInTypeList(lists[133], typeId, groupId, categoryId))
            throw new InvalidOperationException("ItemTypeError");
        if (prize.Singleton && !dynamicType && !IsInTypeList(lists[134], typeId, groupId, categoryId))
            throw new InvalidOperationException("ItemSingletonError");
        if ((await items.LockContainerAsync(connection, transaction, prize.Id, cancellationToken)).Count != 0)
            throw new InvalidOperationException("ItemEscrowError");
        var isCopy = prize.BlueprintCopy;
        var expiryMs = checked(nowUnixMs + 3L * 24 * 60 * 60 * 1000);
        var escrowedPrize = await items.PutInRaffleEscrowAsync(connection, transaction, prize.Id, ownerId, stationId, cancellationToken);
        var remainingTokens = tokens.Quantity - requiredTokens;
        if (remainingTokens == 0) await items.DeleteAsync(connection, transaction, tokenId, cancellationToken);
        else await items.SetQuantityAsync(connection, transaction, tokenId, remainingTokens, cancellationToken);
        long raffleId;
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.raffle_records
                (owner_id,item_id,type_id,location_id,solar_system_id,group_id,category_id,meta_group_id,is_copy,
                 ticket_count,ticket_price,restriction_id,created_at_ms,expires_at_ms)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14) RETURNING raffle_id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(prize.Id);
            command.Parameters.AddWithValue(typeId); command.Parameters.AddWithValue(stationId);
            command.Parameters.AddWithValue((object?)solarSystemId ?? DBNull.Value); command.Parameters.AddWithValue(groupId);
            command.Parameters.AddWithValue(categoryId); command.Parameters.AddWithValue(metaGroupId); command.Parameters.AddWithValue(isCopy);
            command.Parameters.AddWithValue(ticketCount); command.Parameters.AddWithValue(ticketPrice);
            command.Parameters.AddWithValue((object?)restrictionId ?? DBNull.Value); command.Parameters.AddWithValue(nowUnixMs); command.Parameters.AddWithValue(expiryMs);
            raffleId = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("RAFFLE_CREATE_FAILED"));
        }
        if (_scheduler is { } scheduler)
            await ScheduleExpiryAsync(connection, transaction, scheduler, raffleId, expiryMs, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var prizeRow = InventoryRow(escrowedPrize, groupId, categoryId);
        NotifyItemChangeForCharacterSafely(ownerId, call.Session, raffleId, prizeRow,
            new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(4), new PyInt(ItemFlags.Hangar))]));
        var tokenRow = InventoryRow(tokens with { Quantity = remainingTokens }, tokenGroupId, tokenCategoryId);
        NotifyItemChangeForCharacterSafely(ownerId, call.Session, raffleId, tokenRow,
            new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(9), new PyInt(tokens.Quantity))]));
        return raffleId;
    }

    private readonly SemaphoreSlim _typeListsGate = new(1, 1);
    private IReadOnlyDictionary<int, System.Text.Json.JsonElement>? _typeLists;

    private async Task<IReadOnlyDictionary<int, System.Text.Json.JsonElement>> RaffleTypeListsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        if (_typeLists is { } cached) return cached;
        await _typeListsGate.WaitAsync(cancellationToken);
        try
        {
            if (_typeLists is { } ready) return ready;
            var loaded = new Dictionary<int, System.Text.Json.JsonElement>();
            await using var command = new NpgsqlCommand($"SELECT (doc->>'_key')::integer,doc::text FROM {_sdeSchema}.\"typeLists\" WHERE (doc->>'_key')::integer = ANY($1)", connection, transaction);
            command.Parameters.AddWithValue(new[] { 36, 142, 133, 134 });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                using var document = System.Text.Json.JsonDocument.Parse(reader.GetString(1));
                loaded.Add(reader.GetInt32(0), document.RootElement.Clone());
            }
            if (loaded.Count != 4) throw new InvalidOperationException("SDE_RAFFLE_TYPE_LISTS_MISSING");
            _typeLists = loaded;
            return loaded;
        }
        finally { _typeListsGate.Release(); }
    }

    // The client unions each included class after applying its corresponding exclusions.
    private static bool IsInTypeList(System.Text.Json.JsonElement list, int type, int group, int category)
    {
        static bool Has(System.Text.Json.JsonElement doc, string name, int id) =>
            doc.TryGetProperty(name, out var values) && values.ValueKind == System.Text.Json.JsonValueKind.Array &&
            values.EnumerateArray().Any(value => value.TryGetInt32(out var number) && number == id);
        return (Has(list, "includedTypeIDs", type) && !Has(list, "excludedTypeIDs", type)) ||
            (Has(list, "includedGroupIDs", group) && !Has(list, "excludedGroupIDs", group)) ||
            (Has(list, "includedCategoryIDs", category) && !Has(list, "excludedCategoryIDs", category));
    }

    private static PyValue? ReadField(PyValue value, string name)
    {
        var dictionary = value switch { PyObject { Args: PyDict args } => args, PyDict args => args, _ => null };
        return dictionary?.Entries.FirstOrDefault(pair => TextKey(pair.Key) == name).Value;
    }

    private static long CreationInteger(PyValue? value, string reason) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new InvalidOperationException(reason),
    };

    private static MachoWrappedException WrappedRaffleError(string className, string message) =>
        new(MachoErrors.Wrapped(className, [new PyStr(message)],
            [new KeyValuePair<PyValue, PyValue>(new PyStr("msg"), new PyStr(message))]));

    private static string? TextKey(PyValue value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => DecodeTextKey(bytes),
        _ => null,
    };

    private static string? DecodeTextKey(PyBytes bytes)
    {
        try { return new System.Text.UTF8Encoding(false, true).GetString(bytes.Value.Span); }
        catch (System.Text.DecoderFallbackException) { return null; }
    }

    private static decimal DecimalValue(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value >= 0 && number.Value <= (double)decimal.MaxValue => (decimal)number.Value,
        _ => throw new InvalidOperationException("raffles.InvalidPriceError"),
    };

    public async ValueTask<PyValue> InvokeAsync(string operation, ServiceCall call, CancellationToken cancellationToken)
    {
        try { return await InvokeCoreAsync(operation, call, cancellationToken); }
        catch (InsufficientFundsException)
        {
            throw WrappedRaffleError("raffles.NotEnoughISKError", "NotEnoughISKError");
        }
        catch (InvalidOperationException error) when (operation == "CreateRaffle")
        {
            var reason = error.Message is "ItemOwnerError" or "ItemLocationError" or "ItemEscrowError" or
                "ItemTypeError" or "ItemInventoryError" or "ItemSingletonError" or "ItemTriglavianSystemError" or
                "TokenPaymentError" or "TokenTypeError" or "TokenAmountError" or "TokenOwnerError" or
                "TokenLocationError" or "TokenInventoryError" or "TypeMismatchError" or "TicketCountError" or
                "TicketPriceError" or "UnknownError"
                    ? error.Message
                    : error.Message switch
                    {
                        "raffles.InvalidPriceError" => "TicketPriceError",
                        "raffles.ItemNotAvailableError" => "ItemEscrowError",
                        _ => "UnknownError",
                    };
            throw WrappedRaffleError("raffles.CreateError", reason);
        }
        catch (InvalidOperationException error) when (error.Message is "raffles.TicketUnavailableError" or
            "raffles.FailureToDeliverItemError" or "RAFFLE_NOT_FOUND")
        {
            var (className, message) = error.Message switch
            {
                "raffles.TicketUnavailableError" => ("raffles.TicketUnavailableError", "TicketUnavailableError"),
                "raffles.FailureToDeliverItemError" => ("raffles.FailureToDeliverItemError", "FailureToDeliverItemError"),
                _ => ("raffles.RaffleNotFoundError", "RaffleNotFoundError"),
            };
            throw WrappedRaffleError(className, message);
        }
    }

    private async ValueTask<PyValue> InvokeCoreAsync(string operation, ServiceCall call, CancellationToken cancellationToken)
    {
        var userId = call.Session.UserId;
        if (_hyperNetKillSwitchProvider?.Invoke() == true)
            throw new MachoWrappedException(MachoErrors.Wrapped("raffles.RafflesError", [new PyStr("HyperNetDisabled")],
                [new KeyValuePair<PyValue, PyValue>(new PyStr("msg"), new PyStr("HyperNetDisabled"))]));
        if (operation == "AmIBanned")
        {
            var accountBanned = userId is { } accountId &&
                await IsAccountBannedAsync(accountId, cancellationToken) is true;
            return accountBanned ? PyBool.True : PyBool.False;
        }
        if (operation is "CreateRaffle" or "BuyTicket" or "BuyRandomTickets" or "AwardItem")
        {
            if (userId is not { } authenticatedUserId) throw MachoErrors.ThrowUserError("ACCOUNT_REQUIRED");
            var accountBanned = await IsAccountBannedAsync(authenticatedUserId, cancellationToken);
            if (accountBanned is null) throw MachoErrors.ThrowUserError("ACCOUNT_REQUIRED");
            if (accountBanned.Value) throw MachoErrors.ThrowUserError("ACCESS_DENIED");
        }
        var now = _now();
        await ExpireDueAsync(now, cancellationToken);
        if (operation == "CreateRaffle")
        {
            double? effectivePrice = _plexPriceProvider is { } readPrice
                ? await readPrice(call, cancellationToken) : _plexPrice;
            if (effectivePrice is null or 0) throw new InvalidOperationException("raffles.InvalidPriceError");
            var id = await CreateRaffleAsync(call, now, effectivePrice.Value, cancellationToken);
            var created = await ReadRaffleWireAsync(id, cancellationToken);
            var recipients = CharacterRecipients(call.Session.CharacterId ?? 0, call.Session);
            call.Session.AfterResponse(() =>
            {
                foreach (var session in recipients)
                    NotifySafely(session, id, "OnRaffleCreatedServer", new PyValue[] { new PyLong(id), created });
            });
            return new PyLong(id);
        }
        if (operation is "BuyTicket" or "BuyRandomTickets")
        {
            var raffleId = Integer(call.Arg(0));
            var buyerId = call.Session.CharacterId ?? 0;
            var purchase = operation == "BuyTicket"
                ? await BuyTicketAsync(raffleId, buyerId, checked((int)Integer(call.Arg(1))), now, cancellationToken)
                : await BuyRandomTicketsForCallAsync(raffleId, buyerId, checked((int)Integer(call.Arg(1))), now,
                    call.Session.SessionId > 0 && Integer(call.CallId) > 0
                        ? $"raffle:bulk:{call.Session.SessionId}:{buyerId}:{Integer(call.CallId)}" : null, cancellationToken);
            var data = await ReadRaffleWireAsync(raffleId, cancellationToken);
            if (purchase.WasNew)
            {
                foreach (var change in purchase.WalletChanges) AnnounceWalletChangeSafely(raffleId, change);
                var recipients = await InterestedAsync(raffleId, call.Session, cancellationToken);
                var ticketUpdate = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyLong(raffleId), new PyInt(purchase.SoldTicketCount))]);
                foreach (var session in recipients) NotifySafely(session, raffleId, "OnTicketsUpdatedServer", ticketUpdate, "charid");
                foreach (var session in recipients) NotifySafely(session, raffleId, "OnRaffleUpdatedServer", new PyValue[] { new PyLong(raffleId), data });
                if (purchase.WinningTicket is { } winning)
                {
                    var winner = await ReadTicketWireAsync(raffleId, winning, cancellationToken);
                    foreach (var session in recipients) NotifySafely(session, raffleId, "OnRaffleFinishedServer", new PyValue[] { new PyLong(raffleId), winner }, "charid");
                }
            }
            return data;
        }
        if (operation == "AwardItem")
        {
            var raffleId = Integer(call.Arg(0));
            await AwardItemAsync(call, raffleId, cancellationToken);
            var data = await ReadRaffleWireAsync(raffleId, cancellationToken);
            foreach (var session in await InterestedAsync(raffleId, call.Session, cancellationToken))
                NotifySafely(session, raffleId, "OnRaffleUpdatedServer", new PyValue[] { new PyLong(raffleId), data });
            return PyNone.Instance;
        }
        if (operation == "GetRaffle") return await ReadRaffleWireAsync(Integer(call.Arg(0)), cancellationToken);
        if (operation == "GetMyRaffleHistory") return await ReadMyHistoryAsync(call, cancellationToken);
        if (operation == "GetMyActiveTickets") return await ReadMyActiveTicketsAsync(call, now, cancellationToken);
        if (operation == "GetCreatedParticipated") return await ReadCharacterStatisticsAsync(call.Session.CharacterId ?? 0, now, cancellationToken);
        if (operation == "GetActiveHistoricPrices") return await ReadTypeStatisticsAsync(checked((int)Integer(call.Arg(0))), now, cancellationToken);
        if (operation == "Grab") return await ReadBrowseAsync(call, false, now, cancellationToken);
        if (operation == "FilteredGrab") return await ReadBrowseAsync(call, true, now, cancellationToken);
        if (operation == "SubscribeToTickets")
        {
            if (_ticketSubscribers.TryAdd(call.Session.SessionId, call.Session))
                call.Session.Closed += () => ForgetSubscriber(call.Session);
            return PyNone.Instance;
        }
        if (operation == "UnSubscribeFromTickets") { _ticketSubscribers.TryRemove(call.Session.SessionId, out _); return PyNone.Instance; }
        if (operation == "SubscribeToRaffle")
        {
            var raffleId = Integer(call.Arg(0));
            _ = await ReadRaffleWireAsync(raffleId, cancellationToken);
            if (_raffleSubscribers.GetOrAdd(raffleId, _ => new()).TryAdd(call.Session.SessionId, call.Session))
                call.Session.Closed += () => ForgetSubscriber(call.Session);
            return PyNone.Instance;
        }
        if (operation == "UnsubscribeFromRaffle")
        {
            var raffleId = Integer(call.Arg(0));
            if (_raffleSubscribers.TryGetValue(raffleId, out var subscribers)) subscribers.TryRemove(call.Session.SessionId, out _);
            return PyNone.Instance;
        }
        throw new InvalidOperationException($"raffleProxy.{operation} is not available");
    }

    private static void NotifySafely(ClientSession session, long raffleId, string eventName, PyValue value, string idType = "clientID")
    {
        try { session.SendNotification(eventName, idType, value); }
        catch (Exception) { Console.Error.WriteLine($"raffle {raffleId} notification failed for session {session.SessionId}"); }
    }

    private static void NotifySafely(ClientSession session, long raffleId, string eventName, PyValue[] values, string idType = "clientID")
    {
        try { session.SendNotification(eventName, idType, values); }
        catch (Exception) { Console.Error.WriteLine($"raffle {raffleId} notification failed for session {session.SessionId}"); }
    }

    private static void NotifyItemChangeSafely(ClientSession session, long raffleId, ItemRow item, PyDict oldValues)
    {
        try { session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(item), oldValues, PyNone.Instance); }
        catch (Exception) { Console.Error.WriteLine($"raffle {raffleId} inventory notification failed for session {session.SessionId}"); }
    }

    private void NotifyItemChangeForCharacterSafely(long characterId, ClientSession? include, long raffleId, ItemRow item, PyDict oldValues)
    {
        foreach (var session in CharacterRecipients(characterId, include))
            NotifyItemChangeSafely(session, raffleId, item, oldValues);
    }

    private void AnnounceWalletChangeSafely(long raffleId, WalletBalanceChange change)
    {
        try { wallets.AnnounceCommitted(change); }
        catch (Exception) { Console.Error.WriteLine($"raffle {raffleId} wallet notification failed for character {change.OwnerId}"); }
    }

    private IReadOnlyList<ClientSession> CharacterRecipients(long characterId, ClientSession? include)
    {
        var recipients = new Dictionary<long, ClientSession>();
        if (include is not null) recipients[include.SessionId] = include;
        if (_characterSessions?.Of(characterId) is { } session) recipients[session.SessionId] = session;
        return recipients.Values.ToArray();
    }

    private void ForgetSubscriber(ClientSession session)
    {
        // Conditional removal protects a newer session if an ID was reused before this callback.
        ((ICollection<KeyValuePair<long, ClientSession>>)_ticketSubscribers).Remove(new(session.SessionId, session));
        foreach (var subscribers in _raffleSubscribers.Values)
            ((ICollection<KeyValuePair<long, ClientSession>>)subscribers).Remove(new(session.SessionId, session));
    }

    private async Task<IReadOnlyList<ClientSession>> InterestedAsync(long raffleId, ClientSession? include, CancellationToken cancellationToken)
    {
        var recipients = new Dictionary<long, ClientSession>();
        if (include is not null) recipients[include.SessionId] = include;
        foreach (var pair in _ticketSubscribers) recipients[pair.Key] = pair.Value;
        if (_raffleSubscribers.TryGetValue(raffleId, out var raffleSessions))
            foreach (var pair in raffleSessions) recipients[pair.Key] = pair.Value;
        if (_characterSessions is { } sessions)
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"""
                SELECT owner_id FROM {_schema}.raffle_records WHERE raffle_id=$1
                UNION
                SELECT owner_id FROM {_schema}.raffle_tickets WHERE raffle_id=$1
                """, connection);
            command.Parameters.AddWithValue(raffleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (sessions.Of(reader.GetInt64(0)) is { } session) recipients[session.SessionId] = session;
        }
        return recipients.Values.ToArray();
    }

    private static ItemRow InventoryRow(Item item, int groupId, int categoryId) =>
        new(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Quantity, groupId, categoryId);

    private async Task<PyValue> ReadTicketWireAsync(long raffleId, int ticketNumber, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT r.running_id,t.owner_id FROM {_schema}.raffle_records r JOIN {_schema}.raffle_tickets t USING(raffle_id) WHERE r.raffle_id=$1 AND t.ticket_number=$2", connection);
        command.Parameters.AddWithValue(raffleId); command.Parameters.AddWithValue(ticketNumber);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return PyNone.Instance;
        return Py.KeyVal(("running_id", new PyLong(reader.GetInt64(0))), ("raffle_id", new PyLong(raffleId)),
            ("owner_id", new PyLong(reader.GetInt64(1))), ("number", new PyInt(ticketNumber)));
    }

    private static PyValue AssertIsPyObject(PyValue value) => value;

    private async Task<PyValue> ReadRaffleWireAsync(long raffleId, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        long runningId, ownerId, itemId, locationId, created, expires, ended;
        long? solarSystemId;
        int typeId, ticketCount, soldCount;
        decimal ticketPrice, pendingIsk;
        int status;
        int? restriction, winner;
        bool isCopy;
        await using (var command = new NpgsqlCommand($"""
            SELECT running_id, raffle_id, owner_id, item_id, type_id, location_id, solar_system_id, ticket_count, ticket_price,
                   restriction_id, created_at_ms, expires_at_ms, sold_ticket_count, pending_isk,
                   status, winning_ticket, COALESCE(ended_at_ms,0), is_copy
            FROM {_schema}.raffle_records WHERE raffle_id=$1
            """, connection))
        {
            command.Parameters.AddWithValue(raffleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("RAFFLE_NOT_FOUND");
            runningId = reader.GetInt64(0); _ = reader.GetInt64(1); ownerId = reader.GetInt64(2); itemId = reader.GetInt64(3); typeId = reader.GetInt32(4);
            locationId = reader.GetInt64(5); solarSystemId = reader.IsDBNull(6) ? null : reader.GetInt64(6); ticketCount = reader.GetInt32(7); ticketPrice = reader.GetDecimal(8);
            restriction = reader.IsDBNull(9) ? null : reader.GetInt32(9); created = reader.GetInt64(10); expires = reader.GetInt64(11);
            soldCount = reader.GetInt32(12); pendingIsk = reader.GetDecimal(13); status = reader.GetInt32(14);
            winner = reader.IsDBNull(15) ? null : reader.GetInt32(15); ended = reader.GetInt64(16); isCopy = reader.GetBoolean(17);
        }
        var tickets = new List<PyValue>();
        long? winningOwner = null;
        await using (var command = new NpgsqlCommand($"SELECT ticket_number,owner_id FROM {_schema}.raffle_tickets WHERE raffle_id=$1 ORDER BY ticket_number", connection))
        {
            command.Parameters.AddWithValue(raffleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var number = reader.GetInt32(0); var ticketOwner = reader.GetInt64(1);
                tickets.Add(Py.KeyVal(("running_id", new PyLong(runningId)), ("raffle_id", new PyLong(raffleId)),
                    ("owner_id", new PyLong(ticketOwner)), ("number", new PyInt(number))));
                if (winner == number) winningOwner = ticketOwner;
            }
        }
        var winningTicket = winner is { } winningNumber && winningOwner is { } winnerOwner
            ? Py.KeyVal(("running_id", new PyLong(runningId)), ("raffle_id", new PyLong(raffleId)),
                ("owner_id", new PyLong(winnerOwner)), ("number", new PyInt(winningNumber)))
            : PyNone.Instance;
        return Py.KeyVal(
            ("running_id", new PyLong(runningId)), ("raffle_id", new PyLong(raffleId)),
            ("owner_id", new PyLong(ownerId)), ("location_id", new PyLong(locationId)),
            ("solar_system_id", solarSystemId is { } systemId ? new PyLong(systemId) : PyNone.Instance), ("item_id", new PyLong(itemId)),
            ("type_id", new PyInt(typeId)), ("ticket_count", new PyInt(ticketCount)),
            ("ticket_price", new PyReal((double)ticketPrice)),
            ("restriction_id", restriction is { } id ? new PyInt(id) : PyNone.Instance),
            ("creation_time", new PyLong(Eve.Services.Py.FileTimeFromUnixMs(created))),
            ("expiration_time", new PyLong(Eve.Services.Py.FileTimeFromUnixMs(expires))),
            ("sold_ticket_count", new PyInt(soldCount)), ("sold_tickets", Py.List(tickets.ToArray())),
            ("winning_ticket", winningTicket), ("raffle_status", new PyInt(status)),
            ("end_date", ended > 0 ? new PyLong(Eve.Services.Py.FileTimeFromUnixMs(ended)) : PyNone.Instance),
            ("meta_data", new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("is_copy"), isCopy ? PyBool.True : PyBool.False)])));
    }


    private async Task<PyValue> ReadMyHistoryAsync(ServiceCall call, CancellationToken cancellationToken)
    {
        var ownerId = call.Session.CharacterId ?? 0;
        if (ownerId <= 0) return Py.Tuple(Py.List(), new PyInt(50));
        var cursorValue = call.Kwarg("running_id");
        var cursor = cursorValue is null || cursorValue is PyNone ? 0 : Integer(cursorValue);
        var ids = new List<long>();
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand($"""
            SELECT r.raffle_id FROM {_schema}.raffle_records r
            WHERE (r.owner_id=$1 OR EXISTS (SELECT 1 FROM {_schema}.raffle_tickets t WHERE t.raffle_id=r.raffle_id AND t.owner_id=$1))
              AND ($2=0 OR r.running_id<$2)
            ORDER BY r.running_id DESC LIMIT 50
            """, connection))
        {
            command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(cursor);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt64(0));
        }
        var rows = new List<PyValue>();
        foreach (var id in ids) rows.Add(await ReadRaffleWireAsync(id, cancellationToken));
        return Py.Tuple(Py.List(rows.ToArray()), new PyInt(50));
    }

    private async Task<PyValue> ReadMyActiveTicketsAsync(ServiceCall call, long nowUnixMs, CancellationToken cancellationToken)
    {
        var ownerId = call.Session.CharacterId ?? 0;
        var rows = new List<PyValue>();
        if (ownerId <= 0) return Py.List();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT r.running_id,r.raffle_id,t.owner_id,t.ticket_number
            FROM {_schema}.raffle_records r JOIN {_schema}.raffle_tickets t USING(raffle_id)
            WHERE t.owner_id=$1 AND r.status=6 AND r.expires_at_ms>$2
            ORDER BY r.running_id DESC,t.ticket_number
            """, connection);
        command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(nowUnixMs);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(Py.KeyVal(("running_id", new PyLong(reader.GetInt64(0))), ("raffle_id", new PyLong(reader.GetInt64(1))),
                ("owner_id", new PyLong(reader.GetInt64(2))), ("number", new PyInt(reader.GetInt32(3)))));
        return Py.List(rows.ToArray());
    }

    private async Task<PyValue> ReadCharacterStatisticsAsync(long ownerId, long nowUnixMs, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT
              count(*) FILTER (WHERE EXISTS(SELECT 1 FROM {_schema}.raffle_tickets t WHERE t.raffle_id=r.raffle_id AND t.owner_id=$1)),
              count(*) FILTER (WHERE r.winning_ticket IS NOT NULL AND EXISTS(SELECT 1 FROM {_schema}.raffle_tickets t WHERE t.raffle_id=r.raffle_id AND t.ticket_number=r.winning_ticket AND t.owner_id=$1)),
              count(*) FILTER (WHERE r.owner_id=$1 AND r.status=8),
              count(*) FILTER (WHERE r.owner_id=$1 AND r.status=7),
              count(*) FILTER (WHERE r.owner_id=$1 AND r.status=9),
              count(*) FILTER (WHERE r.owner_id=$1 AND r.status=6 AND r.expires_at_ms>$2)
            FROM {_schema}.raffle_records r
            """, connection);
        command.Parameters.AddWithValue(ownerId); command.Parameters.AddWithValue(nowUnixMs);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return Py.KeyVal();
        return Py.KeyVal(("raffles_participated", new PyInt(reader.GetInt32(0))),
            ("raffles_won", new PyInt(reader.GetInt32(1))), ("finished_delivered", new PyInt(reader.GetInt32(2))),
            ("finished_undelivered", new PyInt(reader.GetInt32(3))), ("finished_expired", new PyInt(reader.GetInt32(4))),
            ("created_running", new PyInt(reader.GetInt32(5))));
    }

    private async Task<PyValue> ReadTypeStatisticsAsync(int typeId, long nowUnixMs, CancellationToken cancellationToken)
    {
        var active = new List<decimal>(); var historic = new List<decimal>();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT ticket_price*ticket_count,status=6 AND expires_at_ms>$2 FROM {_schema}.raffle_records WHERE type_id=$1", connection);
        command.Parameters.AddWithValue(typeId); command.Parameters.AddWithValue(nowUnixMs);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            (reader.GetBoolean(1) ? active : historic).Add(reader.GetDecimal(0));
        static PyValue Stat(List<decimal> prices, bool avg = false) =>
            new PyReal(prices.Count == 0 ? 0 : avg ? (double)decimal.Round(prices.Average(), 0, MidpointRounding.AwayFromZero) : (double)prices[0]);
        return Py.KeyVal(("historic_count", new PyInt(historic.Count)), ("historic_min", Stat(historic.Order().ToList())),
            ("historic_max", Stat(historic.OrderDescending().ToList())), ("historic_average", Stat(historic, true)),
            ("active_count", new PyInt(active.Count)), ("active_min", Stat(active.Order().ToList())),
            ("active_max", Stat(active.OrderDescending().ToList())), ("active_average", Stat(active, true)));
    }

    private async Task<bool?> IsAccountBannedAsync(long userId, CancellationToken cancellationToken)
    {
        if (userId <= 0) return null;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT banned FROM {_identitySchema}.accounts WHERE id=$1", connection);
        command.Parameters.AddWithValue(checked((int)userId));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is bool banned ? banned : null;
    }

    private async Task<PyValue> ReadBrowseAsync(ServiceCall call, bool filtered, long nowUnixMs, CancellationToken cancellationToken)
    {
        var filters = filtered ? call.Kwarg("filters") ?? call.Arg(0) : Py.Dict();
        var constraints = filtered ? call.Kwarg("constraints") ?? call.Arg(1) : Py.Dict();
        var typeId = FieldInteger(filters, "type_id");
        var groupId = FieldInteger(filters, "group_id");
        var categoryId = FieldInteger(filters, "category_id");
        var systemId = FieldInteger(filters, "solar_system_id");
        var metaGroupId = FieldInteger(filters, "meta_group_id");
        var blueprintType = FieldInteger(filters, "blueprint_type");
        if (blueprintType is < 0 or > 2) throw MachoErrors.ThrowUserError("RAFFLE_FILTER_INVALID");
        var minPrice = FieldDecimal(constraints, "min_ticket_price"); var maxPrice = FieldDecimal(constraints, "max_ticket_price");
        var minCount = FieldInteger(constraints, "min_ticket_count"); var maxCount = FieldInteger(constraints, "max_ticket_count");
        var sizeValue = call.Kwarg("size");
        var size = sizeValue is null || sizeValue is PyNone ? 256 : (int)Math.Clamp(Integer(sizeValue), 0, 256);
        if (size == 0) return Py.List();
        var ids = new List<long>();
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand($"""
            SELECT raffle_id FROM {_schema}.raffle_records
            WHERE status=6 AND expires_at_ms>$1
              AND (restriction_id IS NULL OR owner_id=$2 OR EXISTS (
                  SELECT 1 FROM {_schema}.raffle_tickets t WHERE t.raffle_id=raffle_records.raffle_id AND t.owner_id=$2))
              AND ($3=0 OR type_id=$3) AND ($4=0 OR group_id=$4) AND ($5=0 OR category_id=$5)
              AND ($6=0 OR solar_system_id=$6) AND ($7=0 OR meta_group_id=$7)
              AND ($8=0 OR (category_id=9 AND (($8=1 AND is_copy=FALSE) OR ($8=2 AND is_copy=TRUE))))
              AND ($9=0 OR ticket_price>=$9) AND ($10=0 OR ticket_price<=$10)
              AND ($11=0 OR ticket_count>=$11) AND ($12=0 OR ticket_count<=$12)
            ORDER BY created_at_ms DESC, raffle_id DESC LIMIT $13
            """, connection))
        {
            command.Parameters.AddWithValue(nowUnixMs); command.Parameters.AddWithValue(call.Session.CharacterId ?? 0);
            command.Parameters.AddWithValue(typeId); command.Parameters.AddWithValue(groupId); command.Parameters.AddWithValue(categoryId);
            command.Parameters.AddWithValue(systemId); command.Parameters.AddWithValue(metaGroupId); command.Parameters.AddWithValue(blueprintType);
            command.Parameters.AddWithValue(minPrice); command.Parameters.AddWithValue(maxPrice);
            command.Parameters.AddWithValue(minCount); command.Parameters.AddWithValue(maxCount); command.Parameters.AddWithValue(size);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt64(0));
        }
        var rows = new List<PyValue>();
        foreach (var id in ids) rows.Add(await ReadRaffleWireAsync(id, cancellationToken));
        return Py.List(rows.ToArray());
    }

    private static long FieldInteger(PyValue value, string field) => ReadField(value, field) is { } item and not PyNone
        ? Integer(item) : 0;
    private static decimal FieldDecimal(PyValue value, string field) => ReadField(value, field) is { } item and not PyNone
        ? DecimalValue(item) : 0;

    private static long Integer(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new InvalidOperationException("raffles.TicketUnavailableError"),
    };

    public Task<PurchaseResult> BuyTicketAsync(long raffleId, long buyerId, int ticketNumber,
        long nowUnixMs, CancellationToken cancellationToken = default) =>
        BuyTicketsAsync(raffleId, buyerId, [ticketNumber], nowUnixMs, cancellationToken);

    public Task<PurchaseResult> BuyRandomTicketsAsync(long raffleId, long buyerId, int ticketCount,
        long nowUnixMs, CancellationToken cancellationToken = default) =>
        BuyRandomTicketsForCallAsync(raffleId, buyerId, ticketCount, nowUnixMs, null, cancellationToken);

    private async Task<PurchaseResult> BuyRandomTicketsForCallAsync(long raffleId, long buyerId, int ticketCount,
        long nowUnixMs, string? operationKey, CancellationToken cancellationToken)
    {
        if (ticketCount < 0) throw new InvalidOperationException("raffles.TicketUnavailableError");
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (ticketCount == 0)
        {
            await using (var active = new NpgsqlCommand($"SELECT 1 FROM {_schema}.raffle_records WHERE raffle_id=$1 AND status=6 AND expires_at_ms>$2 FOR UPDATE", connection, transaction))
            {
                active.Parameters.AddWithValue(raffleId); active.Parameters.AddWithValue(nowUnixMs);
                if (await active.ExecuteScalarAsync(cancellationToken) is null)
                    throw new InvalidOperationException("raffles.TicketUnavailableError");
            }
            var noOp = await BuyTicketsCoreAsync(connection, transaction, raffleId, buyerId, [], nowUnixMs, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return noOp;
        }
        if (operationKey is not null)
        {
            await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, transaction))
            {
                gate.Parameters.AddWithValue(operationKey);
                await gate.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var previous = new NpgsqlCommand($"SELECT raffle_id,ticket_count,ticket_numbers FROM {_schema}.raffle_bulk_operations WHERE operation_key=$1", connection, transaction);
            previous.Parameters.AddWithValue(operationKey);
            await using var reader = await previous.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var priorRaffle = reader.GetInt64(0); var priorCount = reader.GetInt32(1); var priorTickets = reader.GetFieldValue<int[]>(2);
                await reader.DisposeAsync();
                if (priorRaffle != raffleId || priorCount != ticketCount) throw new InvalidOperationException("raffles.TicketUnavailableError");
                var replay = await BuyTicketsCoreAsync(connection, transaction, raffleId, buyerId, priorTickets, nowUnixMs, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }
        }
        int count;
        await using (var command = new NpgsqlCommand($"SELECT ticket_count FROM {_schema}.raffle_records WHERE raffle_id=$1 AND status=6 AND expires_at_ms>$2 FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue(raffleId); command.Parameters.AddWithValue(nowUnixMs);
            if (await command.ExecuteScalarAsync(cancellationToken) is not int availableCount)
                throw new InvalidOperationException("raffles.TicketUnavailableError");
            count = availableCount;
        }
        var sold = new HashSet<int>();
        await using (var tickets = new NpgsqlCommand($"SELECT ticket_number FROM {_schema}.raffle_tickets WHERE raffle_id=$1", connection, transaction))
        {
            tickets.Parameters.AddWithValue(raffleId);
            await using var reader = await tickets.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) sold.Add(reader.GetInt32(0));
        }
        var available = Enumerable.Range(0, count).Where(number => !sold.Contains(number)).ToArray();
        if (ticketCount > available.Length) throw new InvalidOperationException("raffles.TicketUnavailableError");
        for (var i = available.Length - 1; i > 0; i--)
        {
            var swap = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
            (available[i], available[swap]) = (available[swap], available[i]);
        }
        var selected = available.Take(ticketCount).ToArray();
        var result = await BuyTicketsCoreAsync(connection, transaction, raffleId, buyerId, selected, nowUnixMs, cancellationToken);
        if (operationKey is not null)
        {
            await using var insert = new NpgsqlCommand($"INSERT INTO {_schema}.raffle_bulk_operations(operation_key,raffle_id,ticket_count,ticket_numbers) VALUES($1,$2,$3,$4)", connection, transaction);
            insert.Parameters.AddWithValue(operationKey); insert.Parameters.AddWithValue(raffleId);
            insert.Parameters.AddWithValue(ticketCount); insert.Parameters.AddWithValue(selected);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<PurchaseResult> BuyTicketsAsync(long raffleId, long buyerId, IReadOnlyCollection<int> requestedTickets,
        long nowUnixMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var result = await BuyTicketsCoreAsync(connection, transaction, raffleId, buyerId, requestedTickets, nowUnixMs, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<PurchaseResult> BuyTicketsCoreAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long raffleId, long buyerId, IReadOnlyCollection<int> requestedTickets, long nowUnixMs, CancellationToken cancellationToken)
    {
        if (raffleId <= 0 || buyerId <= 0) throw new ArgumentOutOfRangeException(nameof(raffleId));
        var requested = requestedTickets.Distinct().Order().ToArray();
        long ownerId, expiresAt;
        int ticketCount, soldCount;
        decimal ticketPrice, pendingIsk;
        short status;
        await using (var command = new NpgsqlCommand($"""
            SELECT owner_id,ticket_count,ticket_price,sold_ticket_count,pending_isk,expires_at_ms,status
            FROM {_schema}.raffle_records WHERE raffle_id=$1 FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(raffleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("RAFFLE_NOT_FOUND");
            ownerId=reader.GetInt64(0); ticketCount=reader.GetInt32(1); ticketPrice=reader.GetDecimal(2);
            soldCount=reader.GetInt32(3); pendingIsk=reader.GetDecimal(4); expiresAt=reader.GetInt64(5); status=reader.GetInt16(6);
        }
        if (requested.Any(number => number < 0 || number >= ticketCount))
            throw new InvalidOperationException("raffles.TicketUnavailableError");
        var newTickets = new List<int>();
        foreach (var number in requested)
        {
            await using var existing = new NpgsqlCommand($"SELECT owner_id FROM {_schema}.raffle_tickets WHERE raffle_id=$1 AND ticket_number=$2", connection, transaction);
            existing.Parameters.AddWithValue(raffleId); existing.Parameters.AddWithValue(number);
            var holder = await existing.ExecuteScalarAsync(cancellationToken);
            if (holder is null) newTickets.Add(number);
            else if ((long)holder != buyerId) throw new InvalidOperationException("raffles.TicketUnavailableError");
        }
        int? winningTicket = null;
        if (status != 6)
        {
            await using var winnerCommand = new NpgsqlCommand($"SELECT winning_ticket FROM {_schema}.raffle_records WHERE raffle_id=$1", connection, transaction);
            winnerCommand.Parameters.AddWithValue(raffleId);
            var winner = await winnerCommand.ExecuteScalarAsync(cancellationToken);
            winningTicket = winner is int number ? number : null;
        }
        if (newTickets.Count == 0)
        {
            return new PurchaseResult(raffleId, requested.FirstOrDefault(-1), soldCount, pendingIsk, winningTicket, false, []);
        }
        if (status != 6 || expiresAt <= nowUnixMs || soldCount + newTickets.Count > ticketCount)
            throw new InvalidOperationException("raffles.TicketUnavailableError");
        var amount = ticketPrice * newTickets.Count;
        var ticketHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join(',', newTickets))));
        var purchaseKey = $"raffle:{raffleId}:purchase:{buyerId}:{ticketHash}";
        var walletChanges = new List<WalletBalanceChange>();
        var buyerBalance = await wallets.AdjustAsync(connection, transaction, buyerId, -amount, ownerId,
            WalletAdjustmentRefType, $"HyperNet ticket purchase {raffleId}", nowUnixMs, purchaseKey,
            cancellationToken, raffleId);
        walletChanges.Add(new WalletBalanceChange(buyerId, 1000, buyerBalance));
        foreach (var number in newTickets)
        {
            await using var insert = new NpgsqlCommand($"INSERT INTO {_schema}.raffle_tickets(raffle_id,ticket_number,owner_id,purchased_at_ms) VALUES($1,$2,$3,$4)", connection, transaction);
            insert.Parameters.AddWithValue(raffleId); insert.Parameters.AddWithValue(number);
            insert.Parameters.AddWithValue(buyerId); insert.Parameters.AddWithValue(nowUnixMs);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        soldCount += newTickets.Count;
        pendingIsk += amount;
        await using (var update = new NpgsqlCommand($"UPDATE {_schema}.raffle_records SET sold_ticket_count=$2,pending_isk=$3 WHERE raffle_id=$1", connection, transaction))
        {
            update.Parameters.AddWithValue(raffleId); update.Parameters.AddWithValue(soldCount); update.Parameters.AddWithValue(pendingIsk);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        if (soldCount == ticketCount)
        {
            winningTicket = System.Security.Cryptography.RandomNumberGenerator.GetInt32(ticketCount);
            var gross = ticketPrice * ticketCount;
            var tax = decimal.Round(gross * TaxRate, 2, MidpointRounding.AwayFromZero);
            var payout = gross - tax;
            if (payout > 0)
            {
                var sellerBalance = await wallets.AdjustAsync(connection, transaction, ownerId, payout, SystemOwnerId,
                    WalletAdjustmentRefType, $"HyperNet raffle payout {raffleId}", nowUnixMs,
                    $"raffle:{raffleId}:seller-payout", cancellationToken, raffleId);
                walletChanges.Add(new WalletBalanceChange(ownerId, 1000, sellerBalance));
            }
            pendingIsk = 0;
            await using var settle = new NpgsqlCommand($"UPDATE {_schema}.raffle_records SET status=7,winning_ticket=$2,ended_at_ms=$3,pending_isk=0 WHERE raffle_id=$1 AND status=6", connection, transaction);
            settle.Parameters.AddWithValue(raffleId); settle.Parameters.AddWithValue(winningTicket.Value); settle.Parameters.AddWithValue(nowUnixMs);
            if (await settle.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("RAFFLE_SETTLEMENT_CONFLICT");
        }
        return new PurchaseResult(raffleId, newTickets[0], soldCount, pendingIsk, winningTicket, true, walletChanges);
    }

}

public sealed record PurchaseResult(long RaffleId, int TicketNumber, int SoldTicketCount, decimal PendingIsk, int? WinningTicket, bool WasNew,
    IReadOnlyList<WalletBalanceChange> WalletChanges);
