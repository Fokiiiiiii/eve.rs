using Eve.Characters;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Corporations;

[Service("standingMgr")]
public sealed partial class StandingMgrService
{
    private const string NpcNpcQuery = "standing.npcNpc";
    private const string CharacterQuery = "standing.character";
    private const string CorporationQuery = "standing.corporation";
    private const string TransactionQuery = "standing.transactions";
    private const string CompositionQuery = "standing.compositions";
    private readonly Eve.ReadSide.ReadSide _reads;
    private readonly Eve.Corporations.CorporationStore _corporations;
    private readonly Func<long> _nowUnixMs;

    public StandingMgrService(Eve.ReadSide.ReadSide reads, Eve.Corporations.CorporationStore corporations, Func<long> nowUnixMs)
    {
        _reads = reads;
        _corporations = corporations;
        _nowUnixMs = nowUnixMs;

        reads.Register(new Eve.ReadSide.RowsetQuery(
            NpcNpcQuery,
            """
            (
                WITH npc AS (
                    SELECT key::bigint AS id,
                           NULLIF(doc->>'factionID', '')::bigint AS faction_id,
                           NULLIF(doc->>'enemyID', '')::bigint AS enemy_id,
                           NULLIF(doc->>'friendID', '')::bigint AS friend_id
                    FROM {sde}."npcCorporations"
                ),
                factions AS (
                    SELECT key::bigint AS id,
                           NULLIF(doc->>'corporationID', '')::bigint AS corporation_id
                    FROM {sde}.factions
                ),
                relations AS (
                    SELECT faction_id AS from_id, id AS to_id, 2::double precision AS standing
                    FROM npc WHERE faction_id IS NOT NULL
                    UNION ALL
                    SELECT id, faction_id, 2::double precision
                    FROM npc WHERE faction_id IS NOT NULL
                    UNION ALL
                    SELECT id, enemy_id, -1::double precision
                    FROM npc WHERE enemy_id IS NOT NULL
                    UNION ALL
                    SELECT id, friend_id, 1::double precision
                    FROM npc WHERE friend_id IS NOT NULL
                    UNION ALL
                    SELECT faction.id, enemy.faction_id, -1::double precision
                    FROM factions faction
                    JOIN npc executive ON executive.id = faction.corporation_id
                    JOIN npc enemy ON enemy.id = executive.enemy_id
                    WHERE enemy.faction_id IS NOT NULL AND enemy.faction_id <> faction.id
                    UNION ALL
                    SELECT faction.id, friend.faction_id, 1::double precision
                    FROM factions faction
                    JOIN npc executive ON executive.id = faction.corporation_id
                    JOIN npc friend ON friend.id = executive.friend_id
                    WHERE friend.faction_id IS NOT NULL AND friend.faction_id <> faction.id
                )
                SELECT from_id, to_id, max(standing) AS standing
                FROM relations
                WHERE from_id <> to_id
                GROUP BY from_id, to_id
            ) npc_standings
            """,
            [
                Eve.ReadSide.Column.Of("fromID", Eve.ReadSide.ColumnType.Int, "from_id"),
                Eve.ReadSide.Column.Of("toID", Eve.ReadSide.ColumnType.Int, "to_id"),
                Eve.ReadSide.Column.Of("standing", Eve.ReadSide.ColumnType.Real, "standing"),
            ])
        {
            OrderBy = "fromID, toID",
            Cache = true,
        });
        reads.Register(new Eve.ReadSide.RowsetQuery(
            CharacterQuery,
            "{relations}.relations",
            [
                Eve.ReadSide.Column.Of("fromID", Eve.ReadSide.ColumnType.Int, "subject"),
                Eve.ReadSide.Column.Of("standing", Eve.ReadSide.ColumnType.Real, "value"),
            ])
        {
            Where = "kind = 'standing' AND object = $1",
            OrderBy = "fromID",
        });
        reads.Register(new Eve.ReadSide.RowsetQuery(
            CorporationQuery,
            """
            (
                WITH direct AS (
                    SELECT subject AS from_id, floor(value * 1000 + 0.5) / 1000 AS standing
                    FROM {relations}.relations
                    WHERE kind = 'standing' AND object = $1
                ),
                member_average AS (
                    SELECT standing.subject AS from_id,
                           floor(avg(standing.value) * 1000 + 0.5) / 1000 AS standing
                    FROM {relations}.relations member
                    JOIN {relations}.relations standing
                      ON standing.kind = 'standing' AND standing.object = member.subject
                    WHERE member.kind = 'affiliation.corporation' AND member.object = $1
                    GROUP BY standing.subject
                )
                SELECT from_id, standing FROM direct
                UNION ALL
                SELECT from_id, standing FROM member_average
                WHERE NOT EXISTS (SELECT 1 FROM direct)
            ) standings
            """,
            [
                Eve.ReadSide.Column.Of("fromID", Eve.ReadSide.ColumnType.Int, "from_id"),
                Eve.ReadSide.Column.Of("standing", Eve.ReadSide.ColumnType.Real, "standing"),
            ])
        {
            OrderBy = "fromID",
        });
        reads.Register(new Eve.ReadSide.RowsetQuery(
            TransactionQuery,
            "{relations}.relation_history",
            [
                Eve.ReadSide.Column.Of("atMs", Eve.ReadSide.ColumnType.Long, "at_ms"),
                Eve.ReadSide.Column.Of("oldValue", Eve.ReadSide.ColumnType.Real, "old_value"),
                Eve.ReadSide.Column.Of("newValue", Eve.ReadSide.ColumnType.Real, "new_value"),
                Eve.ReadSide.Column.Of("reason", Eve.ReadSide.ColumnType.Text, "reason"),
            ])
        {
            Where = "kind = 'standing' AND subject = $1 AND object = $2",
            OrderBy = "at_ms DESC, id DESC",
            Limit = 512,
        });
        reads.Register(new Eve.ReadSide.RowsetQuery(
            CompositionQuery,
            """
            {relations}.relations member
            JOIN {relations}.relations standing
              ON standing.kind = 'standing'
             AND standing.subject = $1
             AND standing.object = member.subject
            """,
            [
                Eve.ReadSide.Column.Of("standing", Eve.ReadSide.ColumnType.Real,
                    "floor(standing.value * 1000 + 0.5) / 1000"),
                Eve.ReadSide.Column.Of("ownerID", Eve.ReadSide.ColumnType.Int, "member.subject"),
            ])
        {
            Where = "member.kind = 'affiliation.corporation' AND member.object = $2",
            OrderBy = "standing DESC, ownerID",
        });
    }

    [Method("GetNPCNPCStandings")]
    public async ValueTask<PyValue> GetNPCNPCStandings(ServiceCall call)
    {
        var rows = await _reads.RowsAsync(NpcNpcQuery, cancellationToken: call.Cancellation);
        return Rowset(rows);
    }

    [Method("GetCharStandings")]
    public async ValueTask<PyValue> GetCharStandings(ServiceCall call)
    {
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        if (characterId == 0)
        {
            characterId = call.Session.GetInt("characterID") ?? 0;
        }
        var rows = await _reads.RowsAsync(CharacterQuery, [characterId], call.Cancellation);
        return Rowset(rows);
    }

    [Method("GetCorpStandings")]
    public async ValueTask<PyValue> GetCorpStandings(ServiceCall call)
    {
        var corporationId = call.Session.GetInt("corporationID") ?? call.Session.GetInt("corpid") ?? 0;
        var rows = await _reads.RowsAsync(CorporationQuery, [corporationId], call.Cancellation);
        return Rowset(rows);
    }

    [Method("GetStandingTransactions")]
    public async ValueTask<PyValue> GetStandingTransactions(ServiceCall call)
    {
        var fromId = PositiveId(call.Arg(0));
        var toId = PositiveId(call.Arg(1));
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        if (fromId == 0 || toId == 0 || characterId <= 0 || toId != characterId)
        {
            return new PyList([]);
        }
        var rows = await _reads.RowsAsync(TransactionQuery, [fromId, toId], call.Cancellation);
        var transactions = new List<PyValue>(rows.Count);
        foreach (var row in rows.Rows)
        {
            if (row[3] is not string reason ||
                !TryStandingTransaction(reason, Convert.ToInt64(row[0]), row[1], row[2], fromId, toId, out var transaction))
            {
                continue;
            }
            transactions.Add(transaction);
        }
        return new PyList(transactions);
    }

    [Method("GetStandingCompositions")]
    public async ValueTask<PyValue> GetStandingCompositions(ServiceCall call)
    {
        var fromId = PositiveId(call.Arg(0));
        var corporationId = PositiveId(call.Arg(1));
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        if (characterId <= 0 || fromId == 0 || corporationId == 0 ||
            (await _corporations.MemberAsync(characterId, call.Cancellation))?.CorporationId != corporationId ||
            await _corporations.GetAsync(corporationId, call.Cancellation) is not { IsNpc: false })
        {
            return EmptyCompositionRowset();
        }
        var rows = await _reads.RowsAsync(CompositionQuery, [fromId, corporationId], call.Cancellation);
        return ObjectCaching.MethodResult(rows.ToCRowset(), _nowUnixMs(), "5 minutes", "corpid");
    }

    private static PyValue Rowset(Eve.ReadSide.RowSet rows) => new PyObject(
        new PyStr("eve.common.script.sys.rowset.Rowset"),
        Py.Dict(
            ("header", new PyList([.. rows.Columns.Select(column => (PyValue)new PyStr(column.Name))])),
            ("RowClass", new PyToken("util.Row")),
            ("lines", new PyList([.. rows.Rows.Select(row => (PyValue)new PyList(rows.Values(row)))]))));

    private static PyValue EmptyCompositionRowset() => new PyObject(
        new PyStr("carbon.common.script.sys.crowset.CRowset"),
        Py.Dict(
            ("header", Eve.Marshal.Descriptor.FromHeader(new PyObjectEx(
                ObjectExKind.Ex1,
                Py.Tuple(new PyToken("blue.DBRowDescriptor"), Py.Tuple(
                    Py.Tuple(Py.Tuple(new PyStr("standing"), new PyInt(5))),
                    Py.Tuple(Py.Tuple(new PyStr("ownerID"), new PyInt(3))))),
                [],
                [])).Header),
            ("RowClass", new PyToken("util.Row")),
            ("lines", new PyList([]))));

    private static long PositiveId(PyValue value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value > 0 && number.Value <= long.MaxValue => (long)number.Value,
        _ => 0,
    };

    private static bool TryStandingTransaction(
        string reason,
        long atMs,
        object? oldValue,
        object? newValue,
        long fromId,
        long toId,
        out PyValue transaction)
    {
        transaction = PyNone.Instance;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(reason);
            var root = document.RootElement;
            if (!root.TryGetProperty("eventTypeID", out var eventTypeValue) ||
                !eventTypeValue.TryGetInt32(out var eventTypeId) ||
                eventTypeId <= 0)
            {
                return false;
            }
            var oldNumber = oldValue is null ? 0d : Convert.ToDouble(oldValue);
            var newNumber = newValue is null ? 0d : Convert.ToDouble(newValue);
            var modification = JsonNumber(root, "modification") ?? newNumber - oldNumber;
            var eventDateTime = JsonLong(root, "eventDateTime") ?? Py.FileTimeFromUnixMs(atMs);
            var message = root.TryGetProperty("msg", out var msg) && msg.ValueKind == System.Text.Json.JsonValueKind.String
                ? msg.GetString() ?? ""
                : "";
            transaction = Py.KeyVal(
                ("eventTypeID", new PyInt(eventTypeId)),
                ("eventDateTime", new PyLong(eventDateTime)),
                ("modification", Py.Number(modification)),
                ("fromID", new PyInt(fromId)),
                ("toID", new PyInt(toId)),
                ("msg", new PyWString(message)),
                ("int_1", JsonLong(root, "int_1") is { } first ? new PyInt(first) : PyNone.Instance),
                ("int_2", JsonLong(root, "int_2") is { } second ? new PyInt(second) : PyNone.Instance),
                ("int_3", JsonLong(root, "int_3") is { } third ? new PyInt(third) : PyNone.Instance));
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static double? JsonNumber(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.Number &&
        value.TryGetDouble(out var number) &&
        double.IsFinite(number)
            ? number
            : null;

    private static long? JsonLong(System.Text.Json.JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }
        return value.ValueKind == System.Text.Json.JsonValueKind.String &&
               long.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }
}
