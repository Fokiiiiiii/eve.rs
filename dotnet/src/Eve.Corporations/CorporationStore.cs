using System.Data;
using System.Text;
using System.Text.Json;
using Eve.Characters;
using Eve.Data;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Corporations;

/// <summary>Corporations and alliances own their schema. A membership change and its fee share one PostgreSQL transaction.</summary>
public sealed partial class CorporationStore
{
    public const string DefaultSchema = "corporations";
    public const decimal CorporationFoundingCost = 1599800m;
    public const decimal AllianceFoundingCost = 1000000000m;
    private readonly NpgsqlDataSource _source;
    private readonly PgCharacterStore _characters;
    private readonly PgWalletStore _walletStore;
    private readonly WalletTransactions _wallets;
    private readonly PgSkillStore _skills;
    private readonly string _schema;
    private readonly Func<long> _now;
    public AllianceAffiliationMutationCallback? AllianceAffiliationWriter { get; set; }

    public CorporationMemberAffiliationMutationCallback? CorporationMemberAffiliationWriter { get; set; }

    private const string CorpColumns = "id,name,ticker,ceo_id,creator_id,alliance_id,station_id,solar_system_id,tax_rate,lp_tax_rate,description,url,member_limit,is_npc,friendly_fire,created_ms,settings,deleted";
    private const string MemberColumns = "character_id,corporation_id,started_ms,title,roles,grantable_roles,roles_hq,grantable_hq,roles_base,grantable_base,roles_other,grantable_other,base_id,title_mask,block_roles,account_key,division_id,squadron_id,role_stasis_ms";

    public CorporationStore(NpgsqlDataSource source, PgCharacterStore characters, PgWalletStore walletStore,
        WalletTransactions wallets, PgSkillStore skills, Func<long> now, string schema = DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        _source = source; _characters = characters; _walletStore = walletStore; _wallets = wallets; _skills = skills; _now = now; _schema = schema;
    }

    public static IReadOnlyList<Migration> MigrationSet() =>
    [
        .. Migrations.Embedded(typeof(CorporationStore).Assembly, "Migrations"),
        new Migration("004_invitation_ids", """
            ALTER TABLE applications ADD COLUMN invitation_id bigint;
            CREATE SEQUENCE invitation_ids AS bigint START WITH 1;
            UPDATE applications SET invitation_id=id WHERE status=8 AND NOT archived;
            SELECT setval('invitation_ids', COALESCE(MAX(id),1), MAX(id) IS NOT NULL) FROM applications;
            DROP INDEX applications_active;
            CREATE UNIQUE INDEX applications_active ON applications(corporation_id,character_id) WHERE NOT archived AND status <> 8;
            CREATE UNIQUE INDEX applications_invitation_id ON applications(invitation_id) WHERE invitation_id IS NOT NULL;
            """),
        new Migration("005_medal_award_occurrences", """
            ALTER TABLE medal_awards DROP CONSTRAINT medal_awards_pkey;
            ALTER TABLE medal_awards ADD COLUMN award_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY;
            CREATE INDEX medal_awards_medal_time ON medal_awards(medal_id,created_ms,award_id);
            CREATE INDEX medal_awards_character_time ON medal_awards(character_id,medal_id,award_id);
            """),
    ];
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);
    internal static string Key(string name) => name.Normalize(NormalizationForm.FormC).Trim().ToLowerInvariant();
    private static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }

    private static Corporation ReadCorporation(NpgsqlDataReader r) => new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetInt64(4),
        r.IsDBNull(5) ? null : r.GetInt64(5),r.GetInt64(6),r.GetInt32(7),r.GetDecimal(8),r.GetDecimal(9),r.GetString(10),r.GetString(11),
        r.GetInt32(12),r.GetBoolean(13),r.GetBoolean(14),r.GetInt64(15),Json(r.GetString(16)),r.GetBoolean(17));
    private static Member ReadMember(NpgsqlDataReader r) => new(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),r.GetString(3),
        new RoleMasks(r.GetInt64(4),r.GetInt64(5),r.GetInt64(6),r.GetInt64(7),r.GetInt64(8),r.GetInt64(9),r.GetInt64(10),r.GetInt64(11)),
        r.IsDBNull(12) ? null : r.GetInt64(12),r.GetInt32(13),r.GetBoolean(14),r.GetInt32(15),r.GetInt32(16),r.GetInt32(17),r.IsDBNull(18) ? null : r.GetInt64(18));

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] parameters)
    {
        var command = new NpgsqlCommand(sql,connection,transaction);
        foreach (var value in parameters) command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command;
    }

    private async Task<T> WriteAsync<T>(Func<NpgsqlConnection,NpgsqlTransaction,Task<T>> action, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var connection = await _source.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable,token);
            try
            {
                var result = await action(connection,transaction);
                await transaction.CommitAsync(token);
                return result;
            }
            catch (PostgresException error) when (attempt < 3 && error.SqlState is "40001" or "40P01")
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await Task.Delay(5 * (attempt + 1),token);
            }
        }
    }

    public async Task<Corporation?> GetAsync(long id, CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        return await GetAsync(connection,null,id,false,token);
    }
    private async Task<Corporation?> GetAsync(NpgsqlConnection connection,NpgsqlTransaction? tx,long id,bool locked,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"SELECT {CorpColumns} FROM {_schema}.corporations WHERE id=$1" + (locked ? " FOR UPDATE" : ""),id);
        await using var r = await command.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? ReadCorporation(r) : null;
    }
    public async Task<IReadOnlyList<Corporation>> GetManyAsync(IEnumerable<long> ids,CancellationToken token = default)
    {
        await using var command = _source.CreateCommand($"SELECT {CorpColumns} FROM {_schema}.corporations WHERE id=ANY($1) ORDER BY id");
        command.Parameters.Add(new NpgsqlParameter { Value = ids.Distinct().ToArray() });
        var result = new List<Corporation>();
        await using var r = await command.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) result.Add(ReadCorporation(r));
        return result;
    }
    public async Task<Member?> MemberAsync(long character,CancellationToken token = default)
    {
        await using var command = _source.CreateCommand($"SELECT {MemberColumns} FROM {_schema}.members WHERE character_id=$1");
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        await using var r = await command.ExecuteReaderAsync(token);
        if (await r.ReadAsync(token)) return ReadMember(r);
        var c = await _characters.GetAsync(character,token);
        return c is not null && await GetAsync(c.CorporationId,token) is { IsNpc:true } ? NpcMember(c) : null;
    }
    public async Task<IReadOnlyList<Member>> MembersAsync(long corporation,CancellationToken token = default)
    {
        if (await GetAsync(corporation,token) is { IsNpc:true })
            return (await _characters.ForCorporationAsync(corporation,token)).Select(NpcMember).ToArray();
        await using var command = _source.CreateCommand($"SELECT {MemberColumns} FROM {_schema}.members WHERE corporation_id=$1 ORDER BY character_id");
        command.Parameters.Add(new NpgsqlParameter { Value = corporation });
        var result = new List<Member>();
        await using var r = await command.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) result.Add(ReadMember(r));
        return result;
    }

    public async Task<IReadOnlyList<Employment>> EmploymentAsync(long character,CancellationToken token = default)
    {
        await using var command = _source.CreateCommand($"SELECT id,character_id,corporation_id,started_ms,ended_ms,deleted FROM {_schema}.employment WHERE character_id=$1 ORDER BY started_ms DESC,id DESC");
        command.Parameters.Add(new NpgsqlParameter { Value = character });
        var result = new List<Employment>();
        await using var r = await command.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) result.Add(new Employment(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),r.GetInt64(3),r.IsDBNull(4) ? null : r.GetInt64(4),r.GetBoolean(5)));
        return result;
    }

    /// <summary>Resolve the wallet from canonical membership, never from caller-supplied owner or cached session masks.</summary>
    public async Task<WalletAccount> WalletAccountAsync(long characterId,int accountKey,bool requireTake,CancellationToken token = default)
    {
        if (accountKey is < 1000 or > 1006) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var character = await _characters.GetAsync(characterId,token);
        var corp = character is null ? null : await GetAsync(character.CorporationId,token);
        var member = await MemberAsync(characterId,token);
        if (corp is null || corp.IsNpc || corp.Deleted || member?.CorporationId != corp.Id) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var roles = CorporationRoles.Session(corp,member,character!.StationId,await TitlesAsync(corp.Id,token)).AtAll;
        var canTake = (roles & CorporationRoles.Director) != 0 || (roles & CorporationRoles.WalletTakeRole(accountKey)) != 0;
        if (!canTake && (requireTake || (roles & (CorporationRoles.Accountant | CorporationRoles.JuniorAccountant)) == 0))
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return new WalletAccount(corp.Id,accountKey);
    }

    /// <summary>The market's Trader authority, read under the same transaction as custody and wallet settlement.</summary>
    public async Task<(WalletAccount Wallet,SessionRoles Roles)> MarketAuthorityAsync(NpgsqlConnection connection,NpgsqlTransaction tx,
        long actor,int? accountKey,long stationId,CancellationToken token = default)
    {
        var character = await _characters.LockAndGetAsync(connection,tx,actor,token) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
        Corporation? corp;
        await using (var command = Command(connection,tx,$"SELECT {CorpColumns} FROM {_schema}.corporations WHERE id=$1 FOR SHARE",character.CorporationId))
        await using (var r = await command.ExecuteReaderAsync(token))
            corp = await r.ReadAsync(token) ? ReadCorporation(r) : null;
        var member = await MemberAsync(connection,tx,actor,token);
        if (corp is null || corp.IsNpc || corp.Deleted || member?.CorporationId != corp.Id) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var roles = CorporationRoles.Session(corp,member,stationId,await TitlesAsync(connection,tx,corp.Id,token));
        if ((roles.AtAll & CorporationRoles.Trader) == 0) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        var key = accountKey ?? member.AccountKey;
        if (key is < 1000 or > 1006) throw new CorporationError("CORPORATION_ACCESS_DENIED");
        return (new WalletAccount(corp.Id,key),roles);
    }

    public Task<Corporation> CreateAsync(long founder,NewCorporation request,string operationKey,CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var name = request.Name.Normalize(NormalizationForm.FormC).Trim();
        var ticker = request.Ticker.Normalize(NormalizationForm.FormC).Trim();
        if (name.Length == 0) throw new CorporationError("CORPORATION_NAME_REQUIRED");
        if (ticker.Length == 0) ticker = new string(name.ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9').Take(5).ToArray());
        if (ticker.Length == 0) ticker = "CORP";
        var settings = request.Settings.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? new Dictionary<string,object?>()
            : JsonSerializer.Deserialize<Dictionary<string,object?>>(request.Settings.GetRawText())!;
        settings.TryAdd("shape1",419);
        settings["applicationsEnabled"] = request.ApplicationsEnabled;
        var settingsText = JsonSerializer.Serialize(settings);
        return WriteAsync(async (connection,tx) =>
        {
            var character = await _characters.LockAndGetAsync(connection,tx,founder,token) ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            await using (var prior = Command(connection,tx,$"SELECT {CorpColumns}, settings=$2::jsonb FROM {_schema}.corporations WHERE founding_key=$1",operationKey,settingsText))
            await using (var r = await prior.ExecuteReaderAsync(token))
            {
                if (await r.ReadAsync(token))
                {
                    var existing = ReadCorporation(r);
                    if (existing.CreatorId != founder || existing.Name != name || existing.Ticker != ticker || existing.Description != request.Description
                        || existing.Url != request.Url || existing.TaxRate != request.TaxRate || existing.LoyaltyPointTaxRate != request.LoyaltyPointTaxRate
                        || existing.FriendlyFire != request.FriendlyFire || !r.GetBoolean(18))
                        throw new CorporationError("FOUNDING_OPERATION_CONFLICT");
                    return existing;
                }
            }
            await using (var npcTicker = Command(connection,tx,$"SELECT 1 FROM {_schema}.owner_names WHERE ticker_key=$1 AND is_npc AND NOT deleted",Key(ticker)))
                if (await npcTicker.ExecuteScalarAsync(token) is not null) throw new CorporationError("CORPORATION_TICKER_TAKEN");
            await using var allocate = Command(connection,tx,$"SELECT nextval('{_schema}.corporation_ids')");
            var id = (long)(await allocate.ExecuteScalarAsync(token))!;
            try
            {
                await using var owner = Command(connection,tx,$"INSERT INTO {_schema}.owner_names(owner_id,name_key,ticker_key) VALUES($1,$2,$3)",id,Key(name),Key(ticker));
                await owner.ExecuteNonQueryAsync(token);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new CorporationError(error.ConstraintName?.Contains("ticker",StringComparison.Ordinal) == true ? "CORPORATION_TICKER_TAKEN" : "CORPORATION_NAME_TAKEN");
            }
            var now = _now();
            await using (var insert = Command(connection,tx,$"""
                INSERT INTO {_schema}.corporations(id,name,ticker,ceo_id,creator_id,station_id,solar_system_id,tax_rate,lp_tax_rate,description,url,member_limit,friendly_fire,created_ms,settings,founding_key)
                VALUES($1,$2,$3,$4,$4,$5,$6,$7,$8,$9,$10,20,$11,$12,$13::jsonb,$14)
                """,id,name,ticker,founder,character.StationId,character.SolarSystemId,request.TaxRate,request.LoyaltyPointTaxRate,request.Description,request.Url,request.FriendlyFire,now,settingsText,operationKey))
                await insert.ExecuteNonQueryAsync(token);
            await CanLeaveAsync(connection,tx,character,token);
            await using (var titles = Command(connection,tx,$"INSERT INTO {_schema}.titles(corporation_id,id,name) SELECT $1,1 << n,'' FROM generate_series(0,15) n",id))
                await titles.ExecuteNonQueryAsync(token);
            await _wallets.AdjustAsync(connection,tx,founder,-CorporationFoundingCost,1000125,39,"Corporation registration fee",now,operationKey + ":fee",token);
            await _walletStore.OpenAsync(connection,tx,id,0,0,token);
            await _wallets.OpenCorporationDivisionsAsync(connection,tx,id,token);
            await MoveAsync(connection,tx,character,id,null,now,true,token);
            return (await GetAsync(connection,tx,id,false,token))!;
        },token);
    }

    private async Task MoveAsync(NpgsqlConnection connection,NpgsqlTransaction tx,Character character,long corporation,long? alliance,long now,bool founder,CancellationToken token)
    {
        await using (var seed = Command(connection,tx,$"""
            INSERT INTO {_schema}.employment(character_id,corporation_id,started_ms)
            SELECT $1,$2,$3 WHERE NOT EXISTS(SELECT 1 FROM {_schema}.employment WHERE character_id=$1)
            """,character.Id,character.CorporationId,character.CreatedAt.ToUnixTimeMilliseconds()))
            await seed.ExecuteNonQueryAsync(token);
        await using (var close = Command(connection,tx,$"UPDATE {_schema}.employment SET ended_ms=$2 WHERE character_id=$1 AND ended_ms IS NULL",character.Id,now))
            await close.ExecuteNonQueryAsync(token);
        await using (var employment = Command(connection,tx,$"INSERT INTO {_schema}.employment(character_id,corporation_id,started_ms) VALUES($1,$2,$3)",character.Id,corporation,now))
            await employment.ExecuteNonQueryAsync(token);
        await using (var member = Command(connection,tx,$"""
            INSERT INTO {_schema}.members(character_id,corporation_id,started_ms,roles)
            VALUES($1,$2,$3,$4)
            ON CONFLICT(character_id) DO UPDATE SET corporation_id=$2,started_ms=$3,roles=$4,grantable_roles=0,
                roles_hq=0,grantable_hq=0,roles_base=0,grantable_base=0,roles_other=0,grantable_other=0,
                title='',base_id=NULL,title_mask=0,block_roles=false,account_key=1000,division_id=0,squadron_id=0,role_stasis_ms=NULL
            """,character.Id,corporation,now,founder ? CorporationRoles.Director : 0L))
            await member.ExecuteNonQueryAsync(token);
        var destination = await GetAsync(connection,tx,corporation,false,token);
        await _characters.SetAffiliationAsync(connection,tx,character.Id,corporation,alliance,destination is null ? null : Faction(destination),token);
        if (CorporationMemberAffiliationWriter is { } writeMemberAffiliation)
            await writeMemberAffiliation(connection,tx,character.Id,corporation,token);
    }

    public Task UpdateCorporationAsync(long actor,long corporation,string description,string url,decimal taxRate,decimal loyaltyPointTaxRate,bool applicationsEnabled,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            await RequireRoleAsync(connection,tx,corporation,actor,CorporationRoles.Director,token);
            await using var update = Command(connection,tx,$"""
                UPDATE {_schema}.corporations
                SET description=$2,url=$3,tax_rate=$4,lp_tax_rate=$5,
                    settings=settings || jsonb_build_object('applicationsEnabled',$6)
                WHERE id=$1
                """,corporation,description,url,taxRate,loyaltyPointTaxRate,applicationsEnabled);
            await update.ExecuteNonQueryAsync(token);
            return 0;
        },token);

    public Task UpdateSettingsAsync(long actor,long corporation,string jsonPatch,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            await RequireRoleAsync(connection,tx,corporation,actor,CorporationRoles.Director,token);
            await using var update = Command(connection,tx,$"UPDATE {_schema}.corporations SET settings=settings || $2::jsonb WHERE id=$1",corporation,jsonPatch);
            await update.ExecuteNonQueryAsync(token);
            return 0;
        },token);

    public Task UpdateDivisionNamesAsync(long actor,long corporation,string jsonPatch,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            await RequireRoleAsync(connection,tx,corporation,actor,CorporationRoles.Director,token);
            await using var update = Command(connection,tx,$$"""
                UPDATE {{_schema}}.corporations
                SET settings=jsonb_set(settings,'{divisionNames}',COALESCE(settings->'divisionNames','{}'::jsonb) || $2::jsonb,true)
                WHERE id=$1
                """,corporation,jsonPatch);
            await update.ExecuteNonQueryAsync(token);
            return 0;
        },token);

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
}
