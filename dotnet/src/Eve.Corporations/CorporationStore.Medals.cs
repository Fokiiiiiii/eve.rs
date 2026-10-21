using System.Data;
using System.Text;
using System.Text.Json;
using Eve.Characters;
using Eve.Data;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Corporations;

public sealed partial class CorporationStore
{
    public sealed record MedalGraphic(long Part, string Graphic, long Color);
    public sealed record Medal(long Id, long CorporationId, long CreatorId, string Title, string Description,
        long CreatedMs, long Status, bool IsDeleted, IReadOnlyList<MedalGraphic> Graphics, long RecipientCount);
    public sealed record MedalAward(long MedalId, long CharacterId, long IssuerId, string Reason, long CreatedMs);
    public sealed record ReceivedMedal(Medal Medal, MedalAward Award);

    public long ClockMs => _now();

    public async Task<long> CreateMedalAsync(long corporationId, long creatorId, string title, string description,
        IReadOnlyList<MedalGraphic> graphics, CancellationToken token = default)
    {
        var data = JsonSerializer.Serialize(new
        {
            status = 3,
            graphics = graphics.Select(g => new { part = g.Part, graphic = g.Graphic, color = g.Color })
        });
        return await WriteAsync(async (connection, tx) =>
        {
            await using var command = Command(connection, tx,
                $"INSERT INTO {_schema}.medals(corporation_id,creator_id,title,description,data,created_ms) VALUES($1,$2,$3,$4,$5::jsonb,$6) RETURNING id",
                corporationId, creatorId, title, description, data, _now());
            await using var reader = await command.ExecuteReaderAsync(token);
            await reader.ReadAsync(token);
            return reader.GetInt64(0);
        }, token);
    }

    public async Task SetMedalStatusesAsync(long corporationId, IReadOnlyList<(long MedalId, long Status)> statuses,
        CancellationToken token = default)
    {
        if (statuses.Count == 0) return;
        await WriteAsync(async (connection, tx) =>
        {
            foreach (var (medalId, status) in statuses)
            {
                await using var command = Command(connection, tx,
                    $"UPDATE {_schema}.medals SET data=jsonb_set(data,'{{status}}',to_jsonb($3::bigint),true) WHERE corporation_id=$1 AND id=$2",
                    corporationId, medalId, status);
                await command.ExecuteNonQueryAsync(token);
            }
            return true;
        }, token);
    }

    public async Task<IReadOnlyList<Medal>> MedalsAsync(long corporationId, CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection, null,
            $"SELECT m.id,m.corporation_id,m.creator_id,m.title,m.description,m.created_ms,m.data,COUNT(a.character_id)::bigint " +
            $"FROM {_schema}.medals m LEFT JOIN {_schema}.medal_awards a ON a.medal_id=m.id " +
            "WHERE m.corporation_id=$1 GROUP BY m.id ORDER BY m.id", corporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var medals = new List<Medal>();
        while (await reader.ReadAsync(token)) medals.Add(ReadMedal(reader));
        return medals;
    }

    public async Task<bool> HasMedalAwardAsync(long characterId, long medalId, CancellationToken token = default)
    {
        if (characterId <= 0 || medalId <= 0) return false;
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection, null,
            $"SELECT EXISTS(SELECT 1 FROM {_schema}.medal_awards WHERE character_id=$1 AND medal_id=$2)",
            characterId, medalId);
        return (bool)(await command.ExecuteScalarAsync(token) ?? false);
    }

    public async Task<Medal?> MedalAsync(long medalId, CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection, null,
            $"SELECT m.id,m.corporation_id,m.creator_id,m.title,m.description,m.created_ms,m.data,COUNT(a.character_id)::bigint " +
            $"FROM {_schema}.medals m LEFT JOIN {_schema}.medal_awards a ON a.medal_id=m.id " +
            "WHERE m.id=$1 GROUP BY m.id", medalId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadMedal(reader) : null;
    }

    public async Task<IReadOnlyList<MedalAward>> MedalRecipientsAsync(long corporationId, long medalId, CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection, null,
            $"SELECT a.medal_id,a.character_id,a.issuer_id,a.reason,a.created_ms " +
            $"FROM {_schema}.medal_awards a JOIN {_schema}.medals m ON m.id=a.medal_id " +
            "WHERE m.corporation_id=$1 AND m.id=$2 ORDER BY a.created_ms,a.award_id",
            corporationId, medalId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var recipients = new List<MedalAward>();
        while (await reader.ReadAsync(token))
            recipients.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt64(4)));
        return recipients;
    }

    public async Task<IReadOnlyList<ReceivedMedal>> MedalsReceivedAsync(long characterId, CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection, null,
            $"SELECT m.id,m.corporation_id,m.creator_id,m.title,m.description,m.created_ms,m.data,0::bigint," +
            "a.medal_id,a.character_id,a.issuer_id,a.reason,a.created_ms " +
            $"FROM {_schema}.medal_awards a JOIN {_schema}.medals m ON m.id=a.medal_id " +
            "WHERE a.character_id=$1 ORDER BY m.id,a.created_ms,a.award_id", characterId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var received = new List<ReceivedMedal>();
        while (await reader.ReadAsync(token))
            received.Add(new(ReadMedal(reader), new(reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10),
                reader.GetString(11), reader.GetInt64(12))));
        return received;
    }

    public async Task AwardMedalAsync(long corporationId, long medalId, IReadOnlyList<long> characterIds,
        long issuerId, string reason, CancellationToken token = default)
    {
        if (characterIds.Count == 0) return;
        await WriteAsync(async (connection, tx) =>
        {
            foreach (var characterId in characterIds)
            {
                await using var command = Command(connection, tx,
                    $"INSERT INTO {_schema}.medal_awards(medal_id,character_id,issuer_id,reason,created_ms) " +
                    $"SELECT m.id,$3,$4,$5,$6 FROM {_schema}.medals m WHERE m.id=$1 AND m.corporation_id=$2",
                    medalId, corporationId, characterId, issuerId, reason, _now());
                await command.ExecuteNonQueryAsync(token);
            }
            return true;
        }, token);
    }

    private static Medal ReadMedal(NpgsqlDataReader reader)
    {
        var data = ReadMedalData(reader.GetString(6));
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5), data.Status, data.IsDeleted, data.Graphics, reader.GetInt64(7));
    }

    private static (long Status, bool IsDeleted, IReadOnlyList<MedalGraphic> Graphics) ReadMedalData(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (3, false, []);
        var status = root.TryGetProperty("status", out var statusValue) && statusValue.TryGetInt64(out var parsedStatus) ? parsedStatus : 3;
        var isDeleted = root.TryGetProperty("isDeleted", out var deletedValue) && deletedValue.ValueKind == JsonValueKind.True;
        var graphics = new List<MedalGraphic>();
        if (root.TryGetProperty("graphics", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object) continue;
                var part = value.TryGetProperty("part", out var partValue) && partValue.TryGetInt64(out var parsedPart) ? parsedPart : 0;
                var graphic = value.TryGetProperty("graphic", out var graphicValue)
                    ? graphicValue.ValueKind == JsonValueKind.String ? graphicValue.GetString() ?? "" : graphicValue.ToString()
                    : "";
                var color = value.TryGetProperty("color", out var colorValue) && colorValue.TryGetInt64(out var parsedColor) ? parsedColor : 0;
                graphics.Add(new(part, graphic, color));
            }
        }
        return (status, isDeleted, graphics);
    }

private const int CorporationMemberCashGrantRefTypeId = 10;
private const int VoteRunForCeo = 0;
}
