using Npgsql;

namespace Eve.Market;

/// <summary>
/// The custody and money invariants of the market (M8), as queries over the market, wallet and item schemas. Each check returns one text
/// row per broken fact (at most <see cref="MaxRowsPerCheck"/> per check); no row means the invariant holds. The expiry job runs them.
/// </summary>
/// <remarks>
/// Escrow model (as MarketTrading writes it): a buy order's ISK leaves the wallet at placement (journal ref 42, key ...:escrow) and is
/// held in orders.escrow_isk. A fill consumes escrow when the buyer side has no transfer or purchase leg (a seller hitting a bid, or a
/// buyer modifying a bid across asks). So, per wallet: -sum(ref 42 market journal) = open buy escrow + consumed escrow.
/// The wallet writes one journal row per settlement leg, keyed "{operation key}:{leg index}"; the legs of a fill are matched by prefix.
/// </remarks>
public sealed class MarketReconciliation(NpgsqlDataSource source,string marketSchema,string walletSchema,string itemSchema)
{
    public const int MaxRowsPerCheck = 20;
    /// <summary>The wallet journal's escrow reference type (42).</summary>
    public const int EscrowRefType = 42;

    /// <summary>Runs every check; the result lists each broken fact as text. An empty list means the invariants hold.</summary>
    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken token = default)
    {
        var problems = new List<string>();
        foreach (var (name, sql) in Checks())
        {
            await using var command = source.CreateCommand($"SELECT detail FROM ({sql}) AS findings LIMIT {MaxRowsPerCheck}");
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) problems.Add($"{name}: {reader.GetString(0)}");
        }
        return problems;
    }

    private IEnumerable<(string Name, string Sql)> Checks()
    {
        var m = marketSchema;
        var w = walletSchema;
        var i = itemSchema;
        yield return ("escrow.wallet", $"""
            WITH fills_k AS (
                SELECT f.id,f.order_id,f.buyer_wallet,f.buyer_account,f.gross,'market:{m}:'||f.operation_key||':fill:'||f.fill_index AS k
                FROM {m}.fills f),
            open_escrow AS (
                SELECT wallet_owner_id AS owner_id,account_key,sum(escrow_isk) AS amount FROM {m}.orders
                WHERE bid AND NOT is_npc AND state=0 GROUP BY 1,2),
            debits AS (
                SELECT owner_id,account_key,-sum(amount) AS amount FROM {w}.journal
                WHERE ref_type_id={EscrowRefType} AND starts_with(operation_key,'market:{m}:') GROUP BY 1,2),
            consumed AS (
                SELECT fk.buyer_wallet AS owner_id,fk.buyer_account AS account_key,sum(fk.gross) AS amount
                FROM fills_k fk JOIN {m}.orders mo ON mo.id=fk.order_id
                WHERE NOT (mo.is_npc AND mo.bid)
                  AND NOT EXISTS (SELECT 1 FROM {w}.journal j WHERE j.owner_id=fk.buyer_wallet AND j.account_key=fk.buyer_account
                                  AND (starts_with(j.operation_key,fk.k||':transfer:') OR starts_with(j.operation_key,fk.k||':purchase:')))
                GROUP BY 1,2),
            totals AS (
                SELECT owner_id,account_key FROM open_escrow UNION SELECT owner_id,account_key FROM debits
                UNION SELECT owner_id,account_key FROM consumed)
            SELECT 'wallet '||t.owner_id||'/'||t.account_key||' debited '||COALESCE(d.amount,0)||' but open escrow '||COALESCE(o.amount,0)
                   ||' plus consumed '||COALESCE(c.amount,0) AS detail
            FROM totals t
            LEFT JOIN debits d USING (owner_id,account_key)
            LEFT JOIN open_escrow o USING (owner_id,account_key)
            LEFT JOIN consumed c USING (owner_id,account_key)
            WHERE COALESCE(d.amount,0) <> COALESCE(o.amount,0)+COALESCE(c.amount,0)
            """);
        yield return ("escrow.items", $"""
            SELECT 'open sell order '||o.id||' escrows '||COALESCE(sum(it.quantity),0)||' of '||o.vol_remaining AS detail
            FROM {m}.orders o LEFT JOIN {i}.items it ON it.location_id=9200000000+o.id
            WHERE NOT o.bid AND NOT o.is_npc AND o.state=0
            GROUP BY o.id,o.vol_remaining HAVING COALESCE(sum(it.quantity),0)<>o.vol_remaining
            UNION ALL
            SELECT 'closed or bid order '||o.id||' still holds '||sum(it.quantity)||' escrowed items'
            FROM {i}.items it JOIN {m}.orders o ON it.location_id=9200000000+o.id
            WHERE o.state<>0 OR o.bid
            GROUP BY o.id
            UNION ALL
            SELECT 'item '||it.id||' escrowed at no order'
            FROM {i}.items it
            WHERE it.location_id>=9200000000 AND it.location_id<9300000000
              AND NOT EXISTS (SELECT 1 FROM {m}.orders o WHERE o.id=it.location_id-9200000000)
            """);
        yield return ("fill.legs", $"""
            WITH fills_k AS (
                SELECT f.id,f.order_id,'market:{m}:'||f.operation_key||':fill:'||f.fill_index AS k FROM {m}.fills f)
            SELECT 'fill '||fk.id||' legs: transfer '||count(j.id) FILTER (WHERE starts_with(j.operation_key,fk.k||':transfer:'))
                   ||', sale '||count(j.id) FILTER (WHERE starts_with(j.operation_key,fk.k||':sale:'))
                   ||', purchase '||count(j.id) FILTER (WHERE starts_with(j.operation_key,fk.k||':purchase:'))
                   ||', transfer sum '||COALESCE(sum(j.amount) FILTER (WHERE starts_with(j.operation_key,fk.k||':transfer:')),0) AS detail
            FROM fills_k fk JOIN {m}.orders mo ON mo.id=fk.order_id
            LEFT JOIN {w}.journal j ON starts_with(j.operation_key,fk.k||':')
            GROUP BY fk.id,fk.k,mo.is_npc
            HAVING count(j.id) FILTER (WHERE starts_with(j.operation_key,fk.k||':transfer:')) NOT IN (0,2)
                OR COALESCE(sum(j.amount) FILTER (WHERE starts_with(j.operation_key,fk.k||':transfer:')),0)<>0
                OR count(j.id) FILTER (WHERE starts_with(j.operation_key,fk.k||':sale:'))>1
                OR count(j.id) FILTER (WHERE starts_with(j.operation_key,fk.k||':purchase:'))>1
                OR (count(j.id)=0 AND NOT mo.is_npc)
            """);
        yield return ("orders", $"""
            SELECT 'order '||id||' volume remaining '||vol_remaining||' escrow '||escrow_isk AS detail FROM {m}.orders
            WHERE vol_remaining<0 OR vol_remaining>vol_entered OR escrow_isk<0
            UNION ALL
            SELECT 'order '||id||' state '||state||' with remaining '||vol_remaining FROM {m}.orders
            WHERE NOT is_npc AND ((state=0 AND vol_remaining=0) OR (state=1 AND vol_remaining>0))
            UNION ALL
            SELECT 'closed order '||id||' still holds escrow' FROM {m}.orders
            WHERE state IN (2,3) AND (escrow_isk<>0 OR escrow_item_id IS NOT NULL)
            UNION ALL
            SELECT 'open bid '||id||' escrow '||escrow_isk||' is not price times remaining' FROM {m}.orders
            WHERE bid AND NOT is_npc AND state=0 AND escrow_isk<>price*vol_remaining
            """);
    }
}
