using Eve.Services.Core;
using Eve.Services;
using Eve.Marshal;
using Eve.Inventory;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.ProjectDiscovery;

// Explicit operator policy for recorded level advancement, not a correctness award.
internal sealed class ProjectDiscoveryParticipationRewards(NpgsqlDataSource source, string schema, WalletTransactions wallets,
    long xp, decimal isk, int refType, Func<long> now, string policy = "subcell-levels", PgItemStore? items = null, ItemType? crate = null)
{
    public async ValueTask<bool> IsEntitledAsync(long character, CancellationToken ct)
    {
        string eligibility = policy == "exoplanet-local-finish" ? "NOT COALESCE(t.participation_skipped,false)" : "t.participation_steps>=8 AND NOT t.participation_skipped";
        await using var command = source.CreateCommand($"SELECT EXISTS (SELECT 1 FROM {schema}.characters c LEFT JOIN {schema}.project_discovery_tutorials t ON t.character_id=c.id WHERE c.id=$1 AND {eligibility} AND NOT EXISTS (SELECT 1 FROM {schema}.project_discovery_tutorial_receipts r WHERE r.character_id=c.id))");
        command.Parameters.AddWithValue(character);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }
    public async ValueTask<ProjectDiscoveryRewardGrant> GrantAsync(ServiceCall call)
    {
        long character = call.Session.CharacterId.GetValueOrDefault();
        long account = call.Session.UserId.GetValueOrDefault();
        long session = call.Session.SessionId;
        long callId = call.CallId switch { PyInt n => n.Value, PyLong n => n.Value, _ => 0 };
        var ct = call.Cancellation;
        if (character <= 0 || account <= 0 || session <= 0 || callId <= 0) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_ARGUMENTS_INVALID");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var ensure = new NpgsqlCommand($"INSERT INTO {schema}.project_discovery_tutorials(character_id,finished,tutorial_level,updated_ms) VALUES($1,false,0,0) ON CONFLICT DO NOTHING", connection, tx))
        { ensure.Parameters.AddWithValue(character); await ensure.ExecuteNonQueryAsync(ct); }
        long station;
        await using (var owner = new NpgsqlCommand($"SELECT account_id,home_station_id FROM {schema}.characters WHERE id=$1 FOR SHARE", connection, tx))
        {
            owner.Parameters.AddWithValue(character); await using var ownerRow = await owner.ExecuteReaderAsync(ct);
            if (!await ownerRow.ReadAsync(ct) || ownerRow.GetInt32(0) != account || ownerRow.GetInt64(1) <= 0) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_TUTORIAL_UNAVAILABLE");
            station = ownerRow.GetInt64(1);
        }
        await using var row = new NpgsqlCommand($"SELECT participation_steps, participation_skipped FROM {schema}.project_discovery_tutorials WHERE character_id=$1 FOR UPDATE", connection, tx);
        row.Parameters.AddWithValue(character);
        await using var reader = await row.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct); bool eligible = !reader.GetBoolean(1) && (policy == "exoplanet-local-finish" || reader.GetInt32(0) >= 8);
        await reader.DisposeAsync();
        await using (var prior = new NpgsqlCommand($"SELECT account_id,granted FROM {schema}.project_discovery_tutorial_claim_calls WHERE character_id=$1 AND session_id=$2 AND call_id=$3", connection, tx))
        {
            prior.Parameters.AddWithValue(character); prior.Parameters.AddWithValue(session); prior.Parameters.AddWithValue(callId);
            await using var previous = await prior.ExecuteReaderAsync(ct);
            if (await previous.ReadAsync(ct))
            {
                if (previous.GetInt64(0) != account) throw MachoErrors.ThrowUserError("PROJECT_DISCOVERY_ARGUMENTS_INVALID");
                return new(previous.GetBoolean(1), null);
            }
        }
        if (!eligible)
        {
            await SaveCallAsync(connection, tx, character, account, session, callId, false, ct);
            await tx.CommitAsync(ct); return new(false, null);
        }
        await using var receipt = new NpgsqlCommand($"INSERT INTO {schema}.project_discovery_tutorial_receipts(character_id,policy,experience,isk,created_ms) VALUES($1,$5,$2,$3,$4) ON CONFLICT DO NOTHING", connection, tx);
        receipt.Parameters.AddWithValue(character); receipt.Parameters.AddWithValue(xp); receipt.Parameters.AddWithValue(isk); receipt.Parameters.AddWithValue(now()); receipt.Parameters.AddWithValue(policy);
        if (await receipt.ExecuteNonQueryAsync(ct) != 1)
        {
            await SaveCallAsync(connection, tx, character, account, session, callId, false, ct);
            await tx.CommitAsync(ct); return new(false, null);
        }
        if (xp > 0)
        {
            await using var progress = new NpgsqlCommand($"INSERT INTO {schema}.project_discovery_progress(character_id,experience) VALUES($1,$2) ON CONFLICT(character_id) DO UPDATE SET experience={schema}.project_discovery_progress.experience+EXCLUDED.experience", connection, tx);
            progress.Parameters.AddWithValue(character); progress.Parameters.AddWithValue(xp); await progress.ExecuteNonQueryAsync(ct);
        }
        long? createdItem = null;
        if (policy == "exoplanet-local-finish")
        {
            if (items is null || crate is null) throw new InvalidOperationException("Missing configured tutorial crate.");
            long item = await items.CreateAsync(connection, tx, new NewItem(crate.TypeId, character, station, ItemFlags.Hangar), ct);
            createdItem = item;
            await using var itemReceipt = new NpgsqlCommand($"UPDATE {schema}.project_discovery_tutorial_receipts SET item_id=$2,item_type_id=$3,delivery_station_id=$4 WHERE character_id=$1", connection, tx);
            itemReceipt.Parameters.AddWithValue(character); itemReceipt.Parameters.AddWithValue(item); itemReceipt.Parameters.AddWithValue(crate!.TypeId); itemReceipt.Parameters.AddWithValue(station); await itemReceipt.ExecuteNonQueryAsync(ct);
        }
        decimal? balance = null;
        if (isk > 0) balance = await wallets.AdjustAsync(connection, tx, character, isk, 0, refType, "Project Discovery tutorial participation", now(), $"project-discovery-tutorial:{character}", ct);
        await SaveCallAsync(connection, tx, character, account, session, callId, true, ct);
        await tx.CommitAsync(ct);
        return new(true, balance, createdItem, createdItem is null ? null : station);
    }
    private async Task SaveCallAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long actor, long account, long session, long callId, bool result, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"INSERT INTO {schema}.project_discovery_tutorial_claim_calls(character_id,account_id,session_id,call_id,granted) VALUES($1,$2,$3,$4,$5)", conn, tx);
        command.Parameters.AddWithValue(actor); command.Parameters.AddWithValue(account); command.Parameters.AddWithValue(session); command.Parameters.AddWithValue(callId); command.Parameters.AddWithValue(result);
        await command.ExecuteNonQueryAsync(ct);
    }
    public void Announce(long character, decimal balance) => wallets.AnnounceCommitted(new(character, 1000, balance));
    public void AnnounceItem(ClientSession session, long item, long station)
    {
        if (crate is null) return;
        var row = new ItemRow(item, crate.TypeId, session.CharacterId.GetValueOrDefault(), station, ItemFlags.Hangar, 1, crate.GroupId, crate.CategoryId);
        session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(row), new PyDict([]), PyNone.Instance);
    }
}
