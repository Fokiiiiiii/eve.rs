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
public sealed record CorporationShareholding(long ShareholderId,long CorporationId,long Shares);
public sealed record CorporationMemberGrant(int Property,decimal Amount);

public sealed partial class CorporationStore
{
    public const string DefaultSchema = "corporations";
    public const decimal CorporationFoundingCost = 1599800m;
    public const decimal AllianceFoundingCost = 1000000000m;
private readonly Func<NpgsqlConnection,NpgsqlTransaction,long,long,long,long,bool,CancellationToken,Task>? _setBlueprintLock;
    private readonly NpgsqlDataSource _source;
    private readonly PgCharacterStore _characters;
    private readonly PgWalletStore _walletStore;
    private readonly WalletTransactions _wallets;
    private readonly PgSkillStore _skills;
    private readonly string _schema;
    private readonly Func<long> _now;
    public AllianceAffiliationMutationCallback? AllianceAffiliationWriter { get; set; }

    public CorporationMemberAffiliationMutationCallback? CorporationMemberAffiliationWriter { get; set; }

    private const string CorpColumns = "id,name,ticker,ceo_id,creator_id,alliance_id,station_id,solar_system_id,tax_rate,lp_tax_rate,description,url,member_limit,is_npc,friendly_fire,created_ms,settings,deleted,ceo_roles_suspended";
    private const string MemberColumns = "character_id,corporation_id,started_ms,title,roles,grantable_roles,roles_hq,grantable_hq,roles_base,grantable_base,roles_other,grantable_other,base_id,title_mask,block_roles,account_key,division_id,squadron_id,role_stasis_ms";

    public CorporationStore(NpgsqlDataSource source, PgCharacterStore characters, PgWalletStore walletStore,
        WalletTransactions wallets, PgSkillStore skills, Func<long> now, string schema = DefaultSchema,
        Func<NpgsqlConnection,NpgsqlTransaction,long,long,long,long,bool,CancellationToken,Task>? setBlueprintLock = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        _source = source; _characters = characters; _walletStore = walletStore; _wallets = wallets; _skills = skills; _now = now; _schema = schema;
        _setBlueprintLock = setBlueprintLock;
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
        new Migration("006_alliance_shares_votes", """
            ALTER TABLE corporations ADD COLUMN IF NOT EXISTS ceo_roles_suspended boolean NOT NULL DEFAULT false;

            ALTER TABLE alliances
                ADD COLUMN IF NOT EXISTS current_prime_hour smallint NOT NULL DEFAULT 0 CHECK (current_prime_hour BETWEEN 0 AND 23),
                ADD COLUMN IF NOT EXISTS new_prime_hour smallint NOT NULL DEFAULT 0 CHECK (new_prime_hour BETWEEN 0 AND 23),
                ADD COLUMN IF NOT EXISTS new_prime_hour_valid_after_filetime bigint NOT NULL DEFAULT 0 CHECK (new_prime_hour_valid_after_filetime >= 0),
                ADD COLUMN IF NOT EXISTS current_capital_system_id bigint CHECK (current_capital_system_id IS NULL OR current_capital_system_id > 0),
                ADD COLUMN IF NOT EXISTS new_capital_system_id bigint CHECK (new_capital_system_id IS NULL OR new_capital_system_id > 0),
                ADD COLUMN IF NOT EXISTS new_capital_system_valid_after_filetime bigint NOT NULL DEFAULT 0 CHECK (new_capital_system_valid_after_filetime >= 0);

            CREATE TABLE alliance_contacts (
                alliance_id bigint NOT NULL REFERENCES alliances(id) ON DELETE CASCADE,
                contact_id bigint NOT NULL CHECK (contact_id > 0),
                relationship_id integer NOT NULL CHECK (relationship_id BETWEEN -10 AND 10),
                label_mask bigint NOT NULL DEFAULT 0 CHECK (label_mask >= 0),
                PRIMARY KEY (alliance_id,contact_id)
            );
            CREATE INDEX alliance_contacts_by_contact ON alliance_contacts(contact_id);

            CREATE TABLE alliance_contact_labels (
                alliance_id bigint NOT NULL REFERENCES alliances(id) ON DELETE CASCADE,
                label_id smallint NOT NULL CHECK (label_id BETWEEN 1 AND 63),
                name text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
                color integer NOT NULL CHECK (color BETWEEN 0 AND 16777215),
                PRIMARY KEY (alliance_id,label_id)
            );
            CREATE UNIQUE INDEX alliance_contact_labels_name ON alliance_contact_labels(alliance_id,lower(name));

            CREATE TABLE share_operation_receipts (
                operation_key text PRIMARY KEY CHECK (length(operation_key) > 0),
                request_fingerprint text NOT NULL,
                created_ms bigint NOT NULL
            );

            CREATE TABLE vote_shareholders (
                corporation_id bigint NOT NULL REFERENCES corporations(id) ON DELETE CASCADE,
                shareholder_id bigint NOT NULL CHECK (shareholder_id > 0),
                shares bigint NOT NULL CHECK (shares > 0),
                PRIMARY KEY (corporation_id,shareholder_id)
            );
            INSERT INTO vote_shareholders(corporation_id,shareholder_id,shares)
            SELECT c.id,c.id,1000 FROM corporations c WHERE NOT c.is_npc AND NOT c.deleted AND NOT EXISTS(SELECT 1 FROM vote_shareholders s WHERE s.corporation_id=c.id)
            ON CONFLICT (corporation_id,shareholder_id) DO NOTHING;

            CREATE TABLE vote_cases (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                corporation_id bigint NOT NULL REFERENCES corporations(id) ON DELETE CASCADE,
                creator_id bigint NOT NULL CHECK (creator_id > 0),
                vote_case_text text NOT NULL,
                description text NOT NULL,
                vote_type integer NOT NULL CHECK (vote_type BETWEEN 0 AND 6),
                options_payload bytea NOT NULL,
                option_count integer NOT NULL CHECK (option_count BETWEEN 2 AND 50),
                parameter bigint,
                parameter1 bigint,
                parameter2 bigint,
                start_filetime bigint NOT NULL,
                end_filetime bigint NOT NULL CHECK (end_filetime > start_filetime),
                sanction_in_effect boolean NOT NULL DEFAULT false,
                sanction_expires_filetime bigint NOT NULL,
                sanctioned_by bigint,
                sanctioned_filetime bigint
            );
            CREATE INDEX vote_cases_corporation_end ON vote_cases(corporation_id,end_filetime,id);

            CREATE TABLE vote_ballots (
                vote_case_id bigint NOT NULL REFERENCES vote_cases(id) ON DELETE CASCADE,
                shareholder_id bigint NOT NULL CHECK (shareholder_id > 0),
                option_id bigint NOT NULL CHECK (option_id >= 0),
                voting_power bigint NOT NULL CHECK (voting_power > 0),
                created_ms bigint NOT NULL,
                PRIMARY KEY (vote_case_id,shareholder_id)
            );

            CREATE TABLE vote_case_resolutions (
                vote_case_id bigint PRIMARY KEY REFERENCES vote_cases(id) ON DELETE CASCADE,
                corporation_id bigint NOT NULL REFERENCES corporations(id) ON DELETE CASCADE,
                winning_option_id bigint NOT NULL CHECK (winning_option_id >= 0),
                passed boolean NOT NULL,
                resolved_filetime bigint NOT NULL CHECK (resolved_filetime > 0)
            );

            CREATE TABLE vote_member_role_snapshots (
                vote_case_id bigint NOT NULL REFERENCES vote_cases(id) ON DELETE CASCADE,
                corporation_id bigint NOT NULL REFERENCES corporations(id) ON DELETE CASCADE,
                character_id bigint NOT NULL CHECK (character_id > 0),
                prior_ceo_id bigint,
                roles bigint NOT NULL,
                grantable_roles bigint NOT NULL,
                roles_hq bigint NOT NULL,
                grantable_hq bigint NOT NULL,
                roles_base bigint NOT NULL,
                grantable_base bigint NOT NULL,
                roles_other bigint NOT NULL,
                grantable_other bigint NOT NULL,
                title_mask integer NOT NULL,
                role_stasis_ms bigint,
                PRIMARY KEY (vote_case_id,character_id)
            );
            """),
        new Migration("007_corporation_welcome_mail", """
            ALTER TABLE corporations ADD COLUMN welcome_mail text NOT NULL DEFAULT '';
            """),
    ];
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);
    internal static string Key(string name) => name.Normalize(NormalizationForm.FormC).Trim().ToLowerInvariant();
    private static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }

    private static Corporation ReadCorporation(NpgsqlDataReader r) => new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetInt64(4),
        r.IsDBNull(5) ? null : r.GetInt64(5),r.GetInt64(6),r.GetInt32(7),r.GetDecimal(8),r.GetDecimal(9),r.GetString(10),r.GetString(11),
        r.GetInt32(12),r.GetBoolean(13),r.GetBoolean(14),r.GetInt64(15),Json(r.GetString(16)),r.GetBoolean(17),r.GetBoolean(18));
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

    public async Task<IReadOnlyList<Corporation>> AllAsync(CancellationToken token = default)
    {
        await using var command = _source.CreateCommand($"SELECT {CorpColumns} FROM {_schema}.corporations WHERE deleted=FALSE ORDER BY id");
        var result = new List<Corporation>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(ReadCorporation(reader));
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
            await using (var prior = Command(connection,tx,$"SELECT {CorpColumns}, settings=$2::jsonb AS settings_match FROM {_schema}.corporations WHERE founding_key=$1",operationKey,settingsText))
            await using (var r = await prior.ExecuteReaderAsync(token))
            {
                if (await r.ReadAsync(token))
                {
                    var existing = ReadCorporation(r);
                    if (existing.CreatorId != founder || existing.Name != name || existing.Ticker != ticker || existing.Description != request.Description
                        || existing.Url != request.Url || existing.TaxRate != request.TaxRate || existing.LoyaltyPointTaxRate != request.LoyaltyPointTaxRate
                        || existing.FriendlyFire != request.FriendlyFire || !r.GetBoolean(r.GetOrdinal("settings_match")))
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
            await using (var shares = Command(connection,tx,$"""
                INSERT INTO {_schema}.vote_shareholders(corporation_id,shareholder_id,shares)
                VALUES($1,$1,1000)
                ON CONFLICT (corporation_id,shareholder_id) DO NOTHING
                """,id))
                await shares.ExecuteNonQueryAsync(token);
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


    public async Task<string> GetCorpWelcomeMailAsync(long corporation,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,
            $"SELECT welcome_mail FROM {_schema}.corporations WHERE id=$1 AND NOT deleted",corporation);
        return await command.ExecuteScalarAsync(token) as string
            ?? throw new CorporationError("CORPORATION_NOT_FOUND");
    }

    public Task SetCorpWelcomeMailAsync(long actor,long corporation,string welcomeMail,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            var corp = await GetAsync(connection,tx,corporation,true,token)
                ?? throw new CorporationError("CORPORATION_NOT_FOUND");
            var character = await _characters.LockAndGetAsync(connection,tx,actor,token);
            var member = await MemberAsync(connection,tx,actor,token);
            if (character is null || member is null || character.CorporationId != corp.Id ||
                member.CorporationId != corp.Id || corp.IsNpc || corp.Deleted)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var roles = CorporationRoles.Session(corp,member,character.StationId,
                await TitlesAsync(connection,tx,corp.Id,token)).AtAll;
            if ((roles & CorporationRoles.PersonnelManager) == 0)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            await using var update = Command(connection,tx,
                $"UPDATE {_schema}.corporations SET welcome_mail=$2 WHERE id=$1",corp.Id,welcomeMail);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new CorporationError("CORPORATION_NOT_FOUND");
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

private const int CorporationMemberCashGrantRefTypeId = 10;
private const int VoteRunForCeo = 0;
    private const int VoteWar = 1;
    private const int VoteCreateShares = 2;
    private const int VoteExpelMember = 3;
    private const int VoteGeneral = 4;
    private const int VoteLockBlueprint = 5;
    private const int VoteUnlockBlueprint = 6;
    private const long VoteFiletimeEpoch = 116444736000000000L;
    private const long VoteTicksPerDay = 864_000_000_000L;
    private const string VoteCaseColumns = "id,corporation_id,creator_id,vote_case_text,description,vote_type,options_payload,option_count,parameter,parameter1,parameter2,start_filetime,end_filetime,sanction_in_effect,sanction_expires_filetime";

    public sealed record CorporationVoteCase(long VoteCaseId,long CorporationId,long CreatorId,string VoteCaseText,
        string Description,int VoteType,byte[] OptionsMarshal,int OptionCount,long? Parameter,long? Parameter1,long? Parameter2,
        long StartDateTime,long EndDateTime,bool SanctionInEffect,long SanctionExpires);
    public sealed record CorporationVoteBallot(long ShareholderId,long OptionId,long VotingPower,long CreatedMs);
    public sealed record CorporationVoteSanction(long VoteCaseId,long CorporationId,int VoteType,long? Parameter,
        long? Parameter1,long? Parameter2,bool InEffect,long Expires);

    public long VoteNowFiletime => checked(VoteFiletimeEpoch + _now() * 10_000L);

    public async Task SetVoteShareholderAsync(long corporationId,long shareholderId,long shares,CancellationToken token = default)
    {
        if (corporationId <= 0 || shareholderId <= 0 || shares < 0) throw new CorporationError("CORPORATION_INVALID_SHAREHOLDER");
        await WriteAsync(async (connection,tx) =>
        {
            if (await GetAsync(connection,tx,corporationId,true,token) is null) throw new CorporationError("CORPORATION_NOT_FOUND");
            if (shares == 0)
            {
                await using var delete = Command(connection,tx,$"DELETE FROM {_schema}.vote_shareholders WHERE corporation_id=$1 AND shareholder_id=$2",corporationId,shareholderId);
                await delete.ExecuteNonQueryAsync(token);
            }
            else
            {
                await using var upsert = Command(connection,tx,$"""
                    INSERT INTO {_schema}.vote_shareholders(corporation_id,shareholder_id,shares)
                    VALUES($1,$2,$3)
                    ON CONFLICT(corporation_id,shareholder_id) DO UPDATE SET shares=EXCLUDED.shares
                    """,corporationId,shareholderId,shares);
                await upsert.ExecuteNonQueryAsync(token);
            }
            return true;
        },token);
    }


    public async Task<IReadOnlyList<CorporationShareholding>> SharesByShareholderAsync(
        long ownerId,long? corporationFilter,CancellationToken token = default)
    {
        if (ownerId <= 0 || corporationFilter is <= 0)
            throw new CorporationError("CORPORATION_INVALID_SHAREHOLDER");

        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = corporationFilter is { } issuer
            ? Command(connection,null,$"""
                SELECT shareholder_id,corporation_id,shares
                FROM {_schema}.vote_shareholders
                WHERE shareholder_id=$1 AND corporation_id=$2
                ORDER BY corporation_id
                """,ownerId,issuer)
            : Command(connection,null,$"""
                SELECT shareholder_id,corporation_id,shares
                FROM {_schema}.vote_shareholders
                WHERE shareholder_id=$1
                ORDER BY corporation_id
                """,ownerId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<CorporationShareholding>();
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2)));
        return result;
    }

    public async Task<IReadOnlyList<CorporationShareholding>> ShareholdersAsync(
        long corporationId,CancellationToken token = default)
    {
        if (corporationId <= 0) throw new CorporationError("CORPORATION_INVALID_SHAREHOLDER");
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"""
            SELECT shareholder_id,corporation_id,shares
            FROM {_schema}.vote_shareholders
            WHERE corporation_id=$1
            ORDER BY shareholder_id
            """,corporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<CorporationShareholding>();
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2)));
        return result;
    }

    public Task MoveCompanySharesAsync(long corporationId,long actorId,long toOwnerId,long amount,string? operationKey,
        CancellationToken token = default)
    {
        if (corporationId <= 0 || actorId <= 0 || toOwnerId <= 0 || amount <= 0)
            throw new CorporationError("CORPORATION_INVALID_SHARE_TRANSFER");
        var stableKey = ShareOperationKey(operationKey);
        var fingerprint = ShareOperationFingerprint("companyShares",corporationId,actorId,new { toOwnerId,amount });
        return WriteAsync(async (connection,tx) =>
        {
            if (await ShareOperationWasAppliedAsync(connection,tx,stableKey,fingerprint,token)) return true;
            var actor = await _characters.LockAndGetAsync(connection,tx,actorId,token)
                ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var corporation = await GetAsync(connection,tx,corporationId,true,token)
                ?? throw new CorporationError("CORPORATION_NOT_FOUND");
            if (corporation.IsNpc || corporation.Deleted || actor.CorporationId != corporationId)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var member = await MemberAsync(connection,tx,actorId,token);
            if (member is null) throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var roles = CorporationRoles.Session(corporation,member,actor.StationId,
                await TitlesAsync(connection,tx,corporationId,token)).AtAll;
            var administrator = corporation.CeoId == actorId && !corporation.CeoRolesSuspended;
            if (!administrator && (roles & CorporationRoles.Director) == 0)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            await ValidateShareOwnerAsync(connection,tx,toOwnerId,token);
            await MoveShareBalanceAsync(connection,tx,corporationId,corporationId,toOwnerId,amount,token);
            await RecordShareOperationAsync(connection,tx,stableKey,fingerprint,token);
            return true;
        },token);
    }

    public Task MovePrivateSharesAsync(long corporationId,long actorId,long toOwnerId,long amount,string? operationKey,
        CancellationToken token = default)
    {
        if (corporationId <= 0 || actorId <= 0 || toOwnerId <= 0 || amount <= 0 || actorId == toOwnerId)
            throw new CorporationError("CORPORATION_INVALID_SHARE_TRANSFER");
        var stableKey = ShareOperationKey(operationKey);
        var fingerprint = ShareOperationFingerprint("privateShares",corporationId,actorId,new { toOwnerId,amount });
        return WriteAsync(async (connection,tx) =>
        {
            if (await ShareOperationWasAppliedAsync(connection,tx,stableKey,fingerprint,token)) return true;
            _ = await _characters.LockAndGetAsync(connection,tx,actorId,token)
                ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var corporation = await GetAsync(connection,tx,corporationId,true,token)
                ?? throw new CorporationError("CORPORATION_NOT_FOUND");
            if (corporation.IsNpc || corporation.Deleted)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            await ValidateShareOwnerAsync(connection,tx,toOwnerId,token);
            await MoveShareBalanceAsync(connection,tx,corporationId,actorId,toOwnerId,amount,token);
            await RecordShareOperationAsync(connection,tx,stableKey,fingerprint,token);
            return true;
        },token);
    }

    public Task ExecuteMemberGrantsAsync(long corporationId,long actorId,IReadOnlyList<long> targetIds,
        IReadOnlyList<CorporationMemberGrant> grants,int walletAccountKey,string? operationKey,
        CancellationToken token = default)
    {
        if (targetIds.Count == 0 || grants.Count == 0) throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
        if (corporationId <= 0 || actorId <= 0 ||
            targetIds.Any(id => id <= 0) || targetIds.Distinct().Count() != targetIds.Count)
            throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
        var hasCash = grants.Any(grant => grant.Property == 6);
        if (hasCash && walletAccountKey is < 1000 or > 1006)
            throw new CorporationError("CORPORATION_INVALID_WALLET_ACCOUNT");
        foreach (var grant in grants)
        {
            if (grant.Property == 6)
            {
                if (grant.Amount <= 0 || decimal.Round(grant.Amount,2) != grant.Amount)
                    throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
            }
            else if (grant.Property == 7)
            {
                if (grant.Amount <= 0 || decimal.Truncate(grant.Amount) != grant.Amount || grant.Amount > long.MaxValue)
                    throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
            }
            else
                throw new CorporationError("CORPORATION_INVALID_MEMBER_GRANT");
        }

        var stableKey = ShareOperationKey(operationKey);
        var fingerprint = ShareOperationFingerprint("memberGrants",corporationId,actorId,
            new { targetIds,grants,walletAccountKey });
        return WriteAsync(async (connection,tx) =>
        {
            if (await ShareOperationWasAppliedAsync(connection,tx,stableKey,fingerprint,token)) return true;
            var actor = await _characters.LockAndGetAsync(connection,tx,actorId,token)
                ?? throw new CorporationError("CHARACTER_NOT_FOUND");
            var corporation = await GetAsync(connection,tx,corporationId,true,token)
                ?? throw new CorporationError("CORPORATION_NOT_FOUND");
            if (corporation.IsNpc || corporation.Deleted || actor.CorporationId != corporationId)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var actorMember = await MemberAsync(connection,tx,actorId,token);
            if (actorMember is null) throw new CorporationError("CORPORATION_ACCESS_DENIED");
            var roles = CorporationRoles.Session(corporation,actorMember,actor.StationId,
                await TitlesAsync(connection,tx,corporationId,token)).AtAll;
            var ceoAdministrator = corporation.CeoId == actorId && !corporation.CeoRolesSuspended;
            var administrator = ceoAdministrator || (roles & CorporationRoles.Director) != 0;
            if (grants.Any(grant => grant.Property == 7) && !administrator)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
            if (hasCash && !administrator &&
                (roles & CorporationRoles.WalletTakeRole(walletAccountKey)) == 0)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");

            foreach (var targetId in targetIds.Order())
            {
                var target = await _characters.LockAndGetAsync(connection,tx,targetId,token)
                    ?? throw new CorporationError("CHARACTER_NOT_FOUND");
                if (target.CorporationId != corporationId ||
                    await MemberAsync(connection,tx,targetId,token) is null)
                    throw new CorporationError("CORPORATION_ACCESS_DENIED");
            }

            var now = _now();
            for (var targetIndex = 0; targetIndex < targetIds.Count; targetIndex++)
            {
                var targetId = targetIds[targetIndex];
                for (var grantIndex = 0; grantIndex < grants.Count; grantIndex++)
                {
                    var grant = grants[grantIndex];
                    if (grant.Property == 6)
                    {
                        var key = $"{stableKey}:cash:{targetId}:{grantIndex}";
                        await _wallets.TransferAsync(connection,tx,new WalletAccount(corporationId,walletAccountKey),
                            new WalletAccount(targetId),grant.Amount,CorporationMemberCashGrantRefTypeId,"Corporation member grant",now,key,
                            $"member-grant:{corporationId}:{actorId}:{targetId}:{grantIndex}",token);
                    }
                    else
                    {
                        await MoveShareBalanceAsync(connection,tx,corporationId,corporationId,targetId,
                            checked((long)grant.Amount),token);
                    }
                }
            }
            await RecordShareOperationAsync(connection,tx,stableKey,fingerprint,token);
            return true;
        },token);
    }

private static string ShareOperationKey(string? operationKey) =>
        string.IsNullOrWhiteSpace(operationKey) ? Guid.NewGuid().ToString("N") : operationKey;

    private static string ShareOperationFingerprint(string kind,long corporationId,long actorId,object request) =>
        JsonSerializer.Serialize(new { kind,corporationId,actorId,request });

    private async Task<bool> ShareOperationWasAppliedAsync(NpgsqlConnection connection,NpgsqlTransaction tx,
        string operationKey,string fingerprint,CancellationToken token)
    {
        await using (var advisory = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))",connection,tx))
        {
            advisory.Parameters.Add(new NpgsqlParameter { Value = "corporation-share:" + _schema + ":" + operationKey });
            await advisory.ExecuteNonQueryAsync(token);
        }
        await using var command = Command(connection,tx,$"""
            SELECT request_fingerprint FROM {_schema}.share_operation_receipts
            WHERE operation_key=$1 FOR UPDATE
            """,operationKey);
        var previous = await command.ExecuteScalarAsync(token);
        if (previous is null or DBNull) return false;
        if (!string.Equals((string)previous,fingerprint,StringComparison.Ordinal))
            throw new CorporationError("CORPORATION_OPERATION_CONFLICT");
        return true;
    }

    private async Task RecordShareOperationAsync(NpgsqlConnection connection,NpgsqlTransaction tx,
        string operationKey,string fingerprint,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"""
            INSERT INTO {_schema}.share_operation_receipts(operation_key,request_fingerprint,created_ms)
            VALUES($1,$2,$3)
            """,operationKey,fingerprint,_now());
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task ValidateShareOwnerAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long ownerId,CancellationToken token)
    {
        if (await _characters.LockAndGetAsync(connection,tx,ownerId,token) is not null) return;
        var corporation = await GetAsync(connection,tx,ownerId,true,token);
        if (corporation is null || corporation.Deleted)
            throw new CorporationError("CORPORATION_SHAREHOLDER_NOT_FOUND");
    }

    private async Task<long> ShareCountAsync(NpgsqlConnection connection,NpgsqlTransaction? tx,long corporationId,long ownerId,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"""
            SELECT COALESCE((SELECT shares FROM {_schema}.vote_shareholders
                WHERE corporation_id=$1 AND shareholder_id=$2),0)::bigint
            """,corporationId,ownerId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token),System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task MoveShareBalanceAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long corporationId,
        long fromOwnerId,long toOwnerId,long amount,CancellationToken token)
    {
        if (corporationId <= 0 || fromOwnerId <= 0 || toOwnerId <= 0 || amount <= 0 || fromOwnerId == toOwnerId)
            throw new CorporationError("CORPORATION_INVALID_SHARE_TRANSFER");

        long fromShares = 0;
        long toShares = 0;
        await using (var command = Command(connection,tx,$"""
            SELECT shareholder_id,shares
            FROM {_schema}.vote_shareholders
            WHERE corporation_id=$1 AND shareholder_id IN ($2,$3)
            ORDER BY shareholder_id
            FOR UPDATE
            """,corporationId,fromOwnerId,toOwnerId))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                if (reader.GetInt64(0) == fromOwnerId) fromShares = reader.GetInt64(1);
                else toShares = reader.GetInt64(1);
            }
        }
        if (fromShares < amount) throw new CorporationError("CORPORATION_INSUFFICIENT_SHARES");
        if (toShares > long.MaxValue - amount) throw new CorporationError("CORPORATION_SHARE_LIMIT");

        if (fromShares == amount)
        {
            await using var remove = Command(connection,tx,$"""
                DELETE FROM {_schema}.vote_shareholders
                WHERE corporation_id=$1 AND shareholder_id=$2
                """,corporationId,fromOwnerId);
            await remove.ExecuteNonQueryAsync(token);
        }
        else
        {
            await using var debit = Command(connection,tx,$"""
                UPDATE {_schema}.vote_shareholders SET shares=shares-$3
                WHERE corporation_id=$1 AND shareholder_id=$2 AND shares >= $3
                """,corporationId,fromOwnerId,amount);
            if (await debit.ExecuteNonQueryAsync(token) != 1)
                throw new CorporationError("CORPORATION_INSUFFICIENT_SHARES");
        }

        await using var credit = Command(connection,tx,$"""
            INSERT INTO {_schema}.vote_shareholders(corporation_id,shareholder_id,shares)
            VALUES($1,$2,$3)
            ON CONFLICT (corporation_id,shareholder_id)
            DO UPDATE SET shares={_schema}.vote_shareholders.shares+EXCLUDED.shares
            """,corporationId,toOwnerId,amount);
        await credit.ExecuteNonQueryAsync(token);
    }

public async Task<bool> CanViewVotesAsync(long corporationId,long actorId,CancellationToken token = default)
    {
        await using var connection = await _source.OpenConnectionAsync(token);
        return await CanViewVotesAsync(connection,null,corporationId,actorId,token);
    }

    private async Task<bool> CanViewVotesAsync(NpgsqlConnection connection,NpgsqlTransaction? tx,long corporationId,long actorId,CancellationToken token)
    {
        if (corporationId <= 0 || actorId <= 0) return false;
        var corporation = await GetAsync(connection,tx,corporationId,false,token);
        if (corporation is null) return false;
        if (corporation.CeoId == actorId && !corporation.CeoRolesSuspended) return true;
        await using var access = Command(connection,tx,$"""
            SELECT EXISTS(
                SELECT 1 FROM {_schema}.members
                WHERE character_id=$1 AND corporation_id=$2 AND roles<>0)
               OR EXISTS(
                SELECT 1 FROM {_schema}.vote_shareholders
                WHERE corporation_id=$2 AND shareholder_id=$1 AND shares>0)
               OR EXISTS(
                SELECT 1
                FROM {_schema}.members m
                JOIN {_schema}.corporations owned ON owned.id=m.corporation_id AND owned.ceo_id=$1 AND NOT owned.ceo_roles_suspended
                JOIN {_schema}.vote_shareholders s ON s.corporation_id=$2 AND s.shareholder_id=owned.id AND s.shares>0
                WHERE m.character_id=$1)
            """,actorId,corporationId);
        return (bool)(await access.ExecuteScalarAsync(token) ?? false);
    }

    public async Task<long> InsertVoteCaseAsync(long corporationId,long creatorId,string voteCaseText,string description,
        int voteType,byte[] optionsMarshal,int optionCount,long? parameter,long? parameter1,long? parameter2,
        long durationDays,CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(voteCaseText) || voteType is < VoteRunForCeo or > VoteUnlockBlueprint ||
            optionsMarshal is null || optionsMarshal.Length == 0 ||
            (voteType == VoteGeneral ? optionCount is < 2 or > 50 : optionCount != 2))
            throw new CorporationError("CORPORATION_INVALID_VOTE_CASE");

        durationDays = Math.Clamp(durationDays,1,30);
        return await WriteAsync(async (connection,tx) =>
        {
            await ResolveExpiredVotesInTransactionAsync(connection,tx,corporationId,token);
            var corporation = await GetAsync(connection,tx,corporationId,true,token)
                ?? throw new CorporationError("CORPORATION_NOT_FOUND");
            if (corporation.IsNpc || corporation.Deleted ||
                !await CanViewVotesAsync(connection,tx,corporationId,creatorId,token))
                throw new CorporationError("CORPORATION_ACCESS_DENIED");

            long? suspendedCharacterId = null;
            long? priorCeoId = null;
            switch (voteType)
            {
                case VoteRunForCeo:
                    if (parameter != creatorId || parameter1 is not null || parameter2 is not null ||
                        !await CanRunForCEOInTransactionAsync(connection,tx,corporation,creatorId,token))
                        throw new CorporationError("CORPORATION_CEO_CANDIDATE_INELIGIBLE");
                    await using (var activeCeoVote = Command(connection,tx,$"""
                        SELECT EXISTS(
                            SELECT 1 FROM {_schema}.vote_cases v
                            WHERE v.corporation_id=$1 AND v.vote_type=$2 AND v.end_filetime>$3
                              AND NOT EXISTS(
                                  SELECT 1 FROM {_schema}.vote_case_resolutions r WHERE r.vote_case_id=v.id
                              )
                        )
                        """,corporationId,VoteRunForCeo,VoteNowFiletime))
                        if ((bool)(await activeCeoVote.ExecuteScalarAsync(token) ?? false))
                            throw new CorporationError("CORPORATION_CEO_VOTE_ALREADY_PENDING");
                    suspendedCharacterId = corporation.CeoId;
                    priorCeoId = corporation.CeoId;
                    break;
                case VoteCreateShares:
                    if (parameter is null or <= 0 || parameter1 is not null || parameter2 is not null)
                        throw new CorporationError("CORPORATION_INVALID_VOTE_CASE");
                    break;
                case VoteExpelMember:
                    if (parameter is null or <= 0 || parameter1 is not null || parameter2 is not null ||
                        parameter == corporation.CeoId ||
                        !await IsAboveAverageShareholderAsync(connection,tx,corporationId,parameter.Value,token))
                        throw new CorporationError("CORPORATION_MEMBER_NOT_EXPULSION_ELIGIBLE");
                    var expelled = await MemberAsync(connection,tx,parameter.Value,token);
                    if (expelled is null || expelled.CorporationId != corporationId)
                        throw new CorporationError("CORPORATION_MEMBER_NOT_FOUND");
                    suspendedCharacterId = parameter.Value;
                    break;
                case VoteLockBlueprint:
                case VoteUnlockBlueprint:
                    if (parameter is null or <= 0 || parameter1 is null or <= 0 || parameter2 is null or <= 0)
                        throw new CorporationError("CORPORATION_INVALID_VOTE_CASE");
                    if (voteType == VoteLockBlueprint && _setBlueprintLock is null)
                        throw new InvalidOperationException("CorporationStore requires setBlueprintLock for blueprint votes.");
                    break;
                case VoteWar:
                case VoteGeneral:
                    break;
            }

            if (suspendedCharacterId is { } characterId)
            {
                await using var existingSnapshot = Command(connection,tx,$"""
                    SELECT EXISTS(
                        SELECT 1 FROM {_schema}.vote_member_role_snapshots
                        WHERE corporation_id=$1 AND character_id=$2
                    )
                    """,corporationId,characterId);
                if ((bool)(await existingSnapshot.ExecuteScalarAsync(token) ?? false))
                    throw new CorporationError("CORPORATION_MEMBER_VOTE_ALREADY_PENDING");
            }

            var start = checked(VoteFiletimeEpoch + _now() * 10_000L);
            var end = checked(start + durationDays * VoteTicksPerDay);
            var expires = checked(end + 3650L * VoteTicksPerDay);
            var storedParameter = voteType == VoteRunForCeo ? creatorId : parameter;
            long voteCaseId;
            await using (var insert = Command(connection,tx,$"""
                INSERT INTO {_schema}.vote_cases
                    (corporation_id,creator_id,vote_case_text,description,vote_type,options_payload,option_count,
                     parameter,parameter1,parameter2,start_filetime,end_filetime,sanction_in_effect,sanction_expires_filetime)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,false,$13)
                RETURNING id
                """,corporationId,creatorId,voteCaseText,description,voteType,optionsMarshal,optionCount,
                    storedParameter,parameter1,parameter2,start,end,expires))
                voteCaseId = Convert.ToInt64(await insert.ExecuteScalarAsync(token),System.Globalization.CultureInfo.InvariantCulture);

            if (suspendedCharacterId is { } suspended)
            {
                await SnapshotAndSuspendVoteRolesAsync(connection,tx,voteCaseId,corporationId,suspended,priorCeoId,token);
                if (voteType == VoteRunForCeo)
                {
                    await using var suspendCeoRoles = Command(connection,tx,$"""
                        UPDATE {_schema}.corporations SET ceo_roles_suspended=true
                        WHERE id=$1 AND ceo_id=$2 AND NOT ceo_roles_suspended
                        """,corporationId,priorCeoId!.Value);
                    if (await suspendCeoRoles.ExecuteNonQueryAsync(token) != 1)
                        throw new CorporationError("CORPORATION_CEO_CHANGED");
                }
            }

            if (voteType == VoteLockBlueprint)
                await _setBlueprintLock!(connection,tx,corporationId,parameter!.Value,parameter1!.Value,parameter2!.Value,true,token);

            return voteCaseId;
        },token);
    }

    public async Task<IReadOnlyList<CorporationVoteCase>> VoteCasesAsync(long corporationId,long actorId,CancellationToken token = default)
    {
        await ResolveExpiredVotesAsync(corporationId,token);
        if (!await CanViewVotesAsync(corporationId,actorId,token)) return [];
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"SELECT {VoteCaseColumns} FROM {_schema}.vote_cases WHERE corporation_id=$1 ORDER BY id DESC",corporationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var cases = new List<CorporationVoteCase>();
        while (await reader.ReadAsync(token)) cases.Add(ReadVoteCase(reader));
        return cases;
    }

    public async Task<CorporationVoteCase?> VoteCaseAsync(long corporationId,long voteCaseId,long actorId,CancellationToken token = default)
    {
        await ResolveExpiredVotesAsync(corporationId,token);
        if (!await CanViewVotesAsync(corporationId,actorId,token)) return null;
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"SELECT {VoteCaseColumns} FROM {_schema}.vote_cases WHERE corporation_id=$1 AND id=$2",corporationId,voteCaseId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadVoteCase(reader) : null;
    }

    public Task<CorporationVoteBallot?> VoteBallotAsync(long corporationId,long voteCaseId,long actorId,CancellationToken token = default) =>
        VoteBallotAsyncCore(corporationId,voteCaseId,actorId,token);

    public Task<CorporationVoteBallot?> VoteBallotAsync(long voteCaseId,long actorId,CancellationToken token = default) =>
        VoteBallotAsyncCore(null,voteCaseId,actorId,token);

    private async Task<CorporationVoteBallot?> VoteBallotAsyncCore(long? expectedCorporationId,long voteCaseId,long actorId,CancellationToken token)
    {
        if (voteCaseId <= 0 || actorId <= 0) return null;

        long issuerCorporationId;
        await using (var lookupConnection = await _source.OpenConnectionAsync(token))
        {
            await using var lookup = Command(lookupConnection,null,
                $"SELECT corporation_id FROM {_schema}.vote_cases WHERE id=$1",voteCaseId);
            var found = await lookup.ExecuteScalarAsync(token);
            if (found is null || found is DBNull) return null;
            issuerCorporationId = Convert.ToInt64(found,System.Globalization.CultureInfo.InvariantCulture);
        }
        if (expectedCorporationId is { } expected && expected != issuerCorporationId) return null;

        await ResolveExpiredVotesAsync(issuerCorporationId,token);
        if (!await CanViewVotesAsync(issuerCorporationId,actorId,token)) return null;

        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,$"""
            SELECT option_id,created_ms FROM {_schema}.vote_ballots
            WHERE vote_case_id=$1 AND shareholder_id=$2
            """,voteCaseId,actorId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var optionId = reader.GetInt64(0);
        var createdMs = reader.GetInt64(1);
        await reader.CloseAsync();
        var votingPower = await VoteWeightAsync(connection,null,issuerCorporationId,actorId,token);
        return new CorporationVoteBallot(actorId,optionId,votingPower,createdMs);
    }

    public async Task InsertVoteAsync(long corporationId,long voteCaseId,long actorId,long optionId,CancellationToken token = default)
    {
        if (optionId < 0) throw new CorporationError("CORPORATION_INVALID_VOTE_OPTION");
        await WriteAsync(async (connection,tx) =>
        {
            await ResolveExpiredVotesInTransactionAsync(connection,tx,corporationId,token);
            var corporation = await GetAsync(connection,tx,corporationId,true,token) ??
                throw new CorporationError("CORPORATION_NOT_FOUND");
            var character = await _characters.LockAndGetAsync(connection,tx,actorId,token);
            if (character is null || corporation.IsNpc || corporation.Deleted ||
                !await CanViewVotesAsync(connection,tx,corporationId,actorId,token))
                throw new CorporationError("CORPORATION_ACCESS_DENIED");

            long end;
            int optionCount;
            await using (var vote = Command(connection,tx,$"""
                SELECT end_filetime,option_count FROM {_schema}.vote_cases
                WHERE corporation_id=$1 AND id=$2 FOR UPDATE
                """,corporationId,voteCaseId))
            await using (var reader = await vote.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw new CorporationError("CORPORATION_VOTE_CASE_NOT_FOUND");
                end = reader.GetInt64(0);
                optionCount = reader.GetInt32(1);
            }

            if (end <= checked(VoteFiletimeEpoch + _now() * 10_000L))
                throw new CorporationError("CORPORATION_VOTE_CLOSED");
            if (optionId >= optionCount) throw new CorporationError("CORPORATION_INVALID_VOTE_OPTION");

            var votingPower = await VoteWeightAsync(connection,tx,corporationId,actorId,token);
            if (votingPower <= 0) throw new CorporationError("CORPORATION_NOT_A_SHAREHOLDER");

            await using var insert = Command(connection,tx,$"""
                INSERT INTO {_schema}.vote_ballots(vote_case_id,shareholder_id,option_id,voting_power,created_ms)
                VALUES($1,$2,$3,$4,$5)
                ON CONFLICT(vote_case_id,shareholder_id) DO UPDATE SET
                    option_id=EXCLUDED.option_id,
                    voting_power=EXCLUDED.voting_power,
                    created_ms=EXCLUDED.created_ms
                """,voteCaseId,actorId,optionId,votingPower,_now());
            await insert.ExecuteNonQueryAsync(token);
            return true;
        },token);
    }

    public async Task<IReadOnlyList<CorporationVoteSanction>> VoteSanctionedActionsAsync(long corporationId,long actorId,CancellationToken token = default)
    {
        var cases = await VoteCasesAsync(corporationId,actorId,token);
        var now = VoteNowFiletime;
        var actions = new List<CorporationVoteSanction>();
        await using var connection = await _source.OpenConnectionAsync(token);
        foreach (var vote in cases.Where(vote => vote.EndDateTime <= now &&
                     vote.VoteType is VoteCreateShares or VoteExpelMember or VoteUnlockBlueprint))
        {
            await using var resolution = Command(connection,null,$"""
                SELECT winning_option_id,passed
                FROM {_schema}.vote_case_resolutions
                WHERE vote_case_id=$1 AND corporation_id=$2
                """,vote.VoteCaseId,corporationId);
            await using var reader = await resolution.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || reader.GetInt64(0) != 0 || !reader.GetBoolean(1))
                continue;
            actions.Add(new(vote.VoteCaseId,vote.CorporationId,vote.VoteType,vote.Parameter ?? 0L,
                vote.Parameter1 ?? 0L,vote.Parameter2 ?? 0L,vote.SanctionInEffect,vote.SanctionExpires));
        }
        return actions;
    }

private async Task<(long OptionId,decimal WinningWeight,decimal TotalCastWeight)> WinningVoteOptionAsync(
        NpgsqlConnection connection,NpgsqlTransaction? tx,long voteCaseId,CancellationToken token = default)
    {
        await using var command = Command(connection,tx,$"""
            WITH ballot_weights AS (
                SELECT b.option_id,
                    COALESCE(personal.shares,0)+COALESCE(corporate.shares,0) AS weight
                FROM {_schema}.vote_ballots b
                JOIN {_schema}.vote_cases vote ON vote.id=b.vote_case_id
                JOIN {_schema}.corporations issuer ON issuer.id=vote.corporation_id
                LEFT JOIN {_schema}.vote_shareholders personal
                    ON personal.corporation_id=vote.corporation_id
                   AND personal.shareholder_id=b.shareholder_id
                LEFT JOIN LATERAL (
                    SELECT owner_shares.shares
                    FROM {_schema}.vote_shareholders owner_shares
                    WHERE owner_shares.corporation_id=vote.corporation_id
                      AND owner_shares.shareholder_id=CASE
                          WHEN issuer.ceo_id=b.shareholder_id AND NOT issuer.ceo_roles_suspended THEN issuer.id
                          ELSE (
                              SELECT member_corporation.id
                              FROM {_schema}.members member
                              JOIN {_schema}.corporations member_corporation
                                ON member_corporation.id=member.corporation_id
                               AND member_corporation.ceo_id=b.shareholder_id AND NOT member_corporation.ceo_roles_suspended
                              WHERE member.character_id=b.shareholder_id
                              LIMIT 1
                          )
                      END
                ) corporate ON true
                WHERE b.vote_case_id=$1
            ),
            option_weights AS (
                SELECT option_id,SUM(weight) AS weight
                FROM ballot_weights
                WHERE weight>0
                GROUP BY option_id
            )
            SELECT option_id,weight,SUM(weight) OVER ()
            FROM option_weights
            ORDER BY weight DESC,option_id ASC
            LIMIT 1
            """,voteCaseId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? (reader.GetInt64(0),reader.GetDecimal(1),reader.GetDecimal(2))
            : (0L,0m,0m);
    }

    public async Task ActivateSanctionedVoteAsync(long corporationId,long actorId,long voteCaseId,CancellationToken token = default)
    {
        await WriteAsync<bool>(async (connection,tx) =>
        {
            await ResolveExpiredVotesInTransactionAsync(connection,tx,corporationId,token);
            var corporation = await GetAsync(connection,tx,corporationId,true,token) ??
                throw new CorporationError("CORPORATION_NOT_FOUND");
            if (corporation.CeoId != actorId || corporation.CeoRolesSuspended)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");

            int voteType;
            long endFiletime;
            long sanctionExpires;
            long? parameter;
            long? parameter1;
            long? parameter2;
            bool inEffect;
            await using (var query = Command(connection,tx,$"""
                SELECT vote_type,end_filetime,sanction_expires_filetime,sanction_in_effect,
                       parameter,parameter1,parameter2
                FROM {_schema}.vote_cases
                WHERE corporation_id=$1 AND id=$2 FOR UPDATE
                """,corporationId,voteCaseId))
            await using (var reader = await query.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token))
                    throw new CorporationError("CORPORATION_VOTE_CASE_NOT_FOUND");
                voteType = reader.GetInt32(0);
                endFiletime = reader.GetInt64(1);
                sanctionExpires = reader.GetInt64(2);
                inEffect = reader.GetBoolean(3);
                parameter = reader.IsDBNull(4) ? null : reader.GetInt64(4);
                parameter1 = reader.IsDBNull(5) ? null : reader.GetInt64(5);
                parameter2 = reader.IsDBNull(6) ? null : reader.GetInt64(6);
            }

            if (voteType is not (VoteCreateShares or VoteExpelMember or VoteUnlockBlueprint))
                throw new CorporationError("CORPORATION_VOTE_NOT_SANCTIONABLE");
            var now = VoteNowFiletime;
            if (endFiletime > now)
                throw new CorporationError("CORPORATION_SANCTION_NOT_AVAILABLE");
            if (sanctionExpires <= now)
                throw new CorporationError("CORPORATION_SANCTION_EXPIRED");
            if (inEffect)
                throw new CorporationError("CORPORATION_SANCTION_ALREADY_APPLIED");

            long winningOption;
            bool passed;
            await using (var result = Command(connection,tx,$"""
                SELECT winning_option_id,passed FROM {_schema}.vote_case_resolutions
                WHERE vote_case_id=$1 AND corporation_id=$2
                """,voteCaseId,corporationId))
            await using (var reader = await result.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token))
                    throw new CorporationError("CORPORATION_VOTE_NOT_PASSED");
                winningOption = reader.GetInt64(0);
                passed = reader.GetBoolean(1);
            }
            if (!passed || winningOption != 0)
                throw new CorporationError("CORPORATION_VOTE_NOT_PASSED");

            switch (voteType)
            {
                case VoteCreateShares:
                    if (parameter is null or <= 0)
                        throw new CorporationError("CORPORATION_INVALID_VOTE_CASE");
                    await CreditSharesInTransactionAsync(connection,tx,corporationId,corporationId,parameter.Value,token);
                    break;
                case VoteExpelMember:
                    if (parameter is null or <= 0 || parameter == corporation.CeoId)
                        throw new CorporationError("CORPORATION_MEMBER_NOT_EXPULSION_ELIGIBLE");
                    var character = await _characters.LockAndGetAsync(connection,tx,parameter.Value,token)
                        ?? throw new CorporationError("CHARACTER_NOT_FOUND");
                    if (character.CorporationId != corporationId ||
                        await MemberAsync(connection,tx,parameter.Value,token) is null)
                        throw new CorporationError("CORPORATION_MEMBER_NOT_FOUND");
                    var npcCorporationId = Eve.Characters.CreationCatalog.ResolveSchool(
                        character.SchoolId,character.RaceId)?.CorporationId ?? 1000004;
                    var npcCorporation = await GetAsync(connection,tx,npcCorporationId,true,token);
                    if (npcCorporation is null || !npcCorporation.IsNpc || npcCorporation.Deleted)
                        throw new CorporationError("NPC_CORPORATION_REQUIRED");
                    await MoveAsync(connection,tx,character,npcCorporation.Id,null,_now(),false,token);
                    await DeleteVoteMemberRoleSnapshotAsync(connection,tx,voteCaseId,parameter.Value,token);
                    break;
                case VoteUnlockBlueprint:
                    if (parameter is null or <= 0 || parameter1 is null or <= 0 || parameter2 is null or <= 0 || _setBlueprintLock is null)
                        throw new InvalidOperationException("CorporationStore requires setBlueprintLock for blueprint votes.");
                    await _setBlueprintLock(connection,tx,corporationId,parameter.GetValueOrDefault(),parameter1.GetValueOrDefault(),parameter2.GetValueOrDefault(),false,token);
                    break;
            }

            await using var update = Command(connection,tx,$"""
                UPDATE {_schema}.vote_cases
                SET sanction_in_effect=true,sanctioned_by=$3,sanctioned_filetime=$4
                WHERE corporation_id=$1 AND id=$2 AND NOT sanction_in_effect
                """,corporationId,voteCaseId,actorId,now);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new CorporationError("CORPORATION_SANCTION_ALREADY_APPLIED");
            return true;
        },token);
    }

public async Task ResolveAllExpiredVotesAsync(CancellationToken token = default)
    {
        var now = VoteNowFiletime;
        var corporationIds = new List<long>();
        await using (var command = _source.CreateCommand($"""
            SELECT DISTINCT vote.corporation_id
            FROM {_schema}.vote_cases vote
            WHERE vote.end_filetime<=$1
              AND NOT EXISTS(
                  SELECT 1 FROM {_schema}.vote_case_resolutions resolved
                  WHERE resolved.vote_case_id=vote.id
              )
            ORDER BY vote.corporation_id
            """))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = now });
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) corporationIds.Add(reader.GetInt64(0));
        }
        foreach (var corporationId in corporationIds)
            await ResolveExpiredVotesAsync(corporationId,token);
    }

private Task<bool> ResolveExpiredVotesAsync(long corporationId,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            await ResolveExpiredVotesInTransactionAsync(connection,tx,corporationId,token);
            return true;
        },token);

    private async Task ResolveExpiredVotesInTransactionAsync(NpgsqlConnection connection,NpgsqlTransaction tx,
        long corporationId,CancellationToken token)
    {
        if (corporationId <= 0 || await GetAsync(connection,tx,corporationId,true,token) is null) return;
        var now = checked(VoteFiletimeEpoch + _now() * 10_000L);
        var due = new List<(long Id,int Type,long Creator,long? Parameter,long? Parameter1,long? Parameter2,long End)>();
        await using (var command = Command(connection,tx,$"""
            SELECT v.id,v.vote_type,v.creator_id,v.parameter,v.parameter1,v.parameter2,v.end_filetime
            FROM {_schema}.vote_cases v
            WHERE v.corporation_id=$1 AND v.end_filetime<=$2
              AND NOT EXISTS(
                  SELECT 1 FROM {_schema}.vote_case_resolutions r WHERE r.vote_case_id=v.id
              )
            ORDER BY v.id
            FOR UPDATE
            """,corporationId,now))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
                due.Add((reader.GetInt64(0),reader.GetInt32(1),reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.GetInt64(6)));
        }

        foreach (var vote in due)
        {
            var outcome = await WinningVoteOptionAsync(connection,tx,vote.Id,token);
            var passed = outcome.OptionId == 0 && outcome.TotalCastWeight > 0m &&
                outcome.WinningWeight * 2m > outcome.TotalCastWeight;

            switch (vote.Type)
            {
                case VoteRunForCeo:
                {
                    var oldCeoId = await PriorVoteCeoAsync(connection,tx,vote.Id,token)
                        ?? throw new CorporationError("CORPORATION_VOTE_ROLE_SNAPSHOT_MISSING");
                    var current = await GetAsync(connection,tx,corporationId,false,token);
                    if (vote.Parameter != vote.Creator || current is null ||
                        current.CeoId != oldCeoId || !current.CeoRolesSuspended)
                        throw new CorporationError("CORPORATION_CEO_CHANGED");
                    if (passed)
                    {
                        var candidate = await _characters.LockAndGetAsync(connection,tx,vote.Creator,token);
                        var member = await MemberAsync(connection,tx,vote.Creator,token);
                        if (candidate is null || candidate.CorporationId != corporationId ||
                            member is null || member.CorporationId != corporationId)
                            passed = false;
                    }
                    if (passed)
                    {
                        await using (var leadership = Command(connection,tx,$"""
                            UPDATE {_schema}.corporations
                            SET ceo_id=$3,ceo_roles_suspended=false
                            WHERE id=$1 AND ceo_id=$2 AND ceo_roles_suspended
                            """,corporationId,oldCeoId,vote.Creator))
                            if (await leadership.ExecuteNonQueryAsync(token) != 1)
                                throw new CorporationError("CORPORATION_CEO_CHANGED");
                        await using (var director = Command(connection,tx,$"""
                            UPDATE {_schema}.members
                            SET roles=CASE WHEN character_id=$2 THEN roles | $4 ELSE roles & ~$4 END
                            WHERE corporation_id=$1 AND character_id IN($2,$3)
                            """,corporationId,vote.Creator,oldCeoId,CorporationRoles.Director))
                            if (await director.ExecuteNonQueryAsync(token) != 2)
                                throw new CorporationError("CORPORATION_MEMBER_NOT_FOUND");
                        await DeleteVoteMemberRoleSnapshotAsync(connection,tx,vote.Id,oldCeoId,token);
                    }
                    else
                    {
                        await using (var restoreCeo = Command(connection,tx,$"""
                            UPDATE {_schema}.corporations SET ceo_roles_suspended=false
                            WHERE id=$1 AND ceo_id=$2 AND ceo_roles_suspended
                            """,corporationId,oldCeoId))
                            if (await restoreCeo.ExecuteNonQueryAsync(token) != 1)
                                throw new CorporationError("CORPORATION_CEO_CHANGED");
                        await RestoreVoteMemberRoleSnapshotAsync(connection,tx,vote.Id,corporationId,oldCeoId,token);
                    }
                    break;
                }
                case VoteExpelMember:
                {
                    if (vote.Parameter is not { } expelCharacterId)
                    {
                        passed = false;
                        break;
                    }
                    var member = await MemberAsync(connection,tx,expelCharacterId,token);
                    if (passed && (member is null || member.CorporationId != corporationId))
                    {
                        passed = false;
                        await DeleteVoteMemberRoleSnapshotAsync(connection,tx,vote.Id,expelCharacterId,token);
                    }
                    else if (!passed)
                    {
                        if (member?.CorporationId == corporationId)
                            await RestoreVoteMemberRoleSnapshotAsync(connection,tx,vote.Id,corporationId,expelCharacterId,token);
                        else
                            await DeleteVoteMemberRoleSnapshotAsync(connection,tx,vote.Id,expelCharacterId,token);
                    }
                    break;
                }
                case VoteLockBlueprint:
                    if (!passed)
                    {
                        if (_setBlueprintLock is null)
                            throw new InvalidOperationException("CorporationStore requires setBlueprintLock for blueprint votes.");
                        if (vote.Parameter is not { } itemId ||
                            vote.Parameter1 is not { } typeId || vote.Parameter2 is not { } locationId)
                            throw new CorporationError("CORPORATION_INVALID_VOTE_CASE");
                        await _setBlueprintLock(connection,tx,corporationId,itemId,typeId,locationId,false,token);
                    }
                    break;
            }

            await using var resolved = Command(connection,tx,$"""
                INSERT INTO {_schema}.vote_case_resolutions
                    (vote_case_id,corporation_id,winning_option_id,passed,resolved_filetime)
                VALUES($1,$2,$3,$4,$5)
                """,vote.Id,corporationId,outcome.OptionId,passed,now);
            await resolved.ExecuteNonQueryAsync(token);
        }
    }

    private async Task<bool> CanRunForCEOInTransactionAsync(NpgsqlConnection connection,NpgsqlTransaction tx,
        Corporation corporation,long actorId,CancellationToken token)
    {
        if (actorId <= 0 || corporation.IsNpc || corporation.Deleted ||
            corporation.CeoId <= 0 || corporation.CeoRolesSuspended || corporation.CeoId == actorId)
            return false;
        var character = await _characters.LockAndGetAsync(connection,tx,actorId,token);
        var member = await MemberAsync(connection,tx,actorId,token);
        if (character?.CorporationId != corporation.Id || member?.CorporationId != corporation.Id)
            return false;

        decimal ownShares;
        decimal totalShares;
        await using (var command = Command(connection,tx,$"""
            SELECT COALESCE(SUM(shares) FILTER (WHERE shareholder_id=$2),0)::numeric,
                   COALESCE(SUM(shares),0)::numeric
            FROM {_schema}.vote_shareholders
            WHERE corporation_id=$1
            """,corporation.Id,actorId))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) return false;
            ownShares = reader.GetDecimal(0);
            totalShares = reader.GetDecimal(1);
        }
        if (totalShares <= 0m || ownShares * 20m <= totalShares) return false;
        const int corporationManagementSkillTypeId = 3363;
        return await _skills.LevelAsync(connection,tx,actorId,corporationManagementSkillTypeId,token) >= 1;
    }

    private async Task<bool> IsAboveAverageShareholderAsync(NpgsqlConnection connection,NpgsqlTransaction tx,
        long corporationId,long shareholderId,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"""
            SELECT COALESCE((
                SELECT shareholder.shares::numeric*totals.holder_count>totals.total_shares
                FROM {_schema}.vote_shareholders shareholder
                CROSS JOIN (
                    SELECT COALESCE(SUM(shares),0)::numeric AS total_shares,COUNT(*)::numeric AS holder_count
                    FROM {_schema}.vote_shareholders WHERE corporation_id=$1
                ) totals
                WHERE shareholder.corporation_id=$1 AND shareholder.shareholder_id=$2
                  AND totals.holder_count>0
            ),false)
            """,corporationId,shareholderId);
        return (bool)(await command.ExecuteScalarAsync(token) ?? false);
    }

    private async Task SnapshotAndSuspendVoteRolesAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long voteCaseId,
        long corporationId,long characterId,long? priorCeoId,CancellationToken token)
    {
        await using (var snapshot = Command(connection,tx,$"""
            INSERT INTO {_schema}.vote_member_role_snapshots
                (vote_case_id,corporation_id,character_id,prior_ceo_id,roles,grantable_roles,
                 roles_hq,grantable_hq,roles_base,grantable_base,roles_other,grantable_other,title_mask,role_stasis_ms)
            SELECT $1,$2,character_id,$4,roles,grantable_roles,
                   roles_hq,grantable_hq,roles_base,grantable_base,roles_other,grantable_other,title_mask,role_stasis_ms
            FROM {_schema}.members
            WHERE corporation_id=$2 AND character_id=$3
            """,voteCaseId,corporationId,characterId,priorCeoId))
            if (await snapshot.ExecuteNonQueryAsync(token) != 1)
                throw new CorporationError("CORPORATION_MEMBER_NOT_FOUND");

        await using var suspend = Command(connection,tx,$"""
            UPDATE {_schema}.members
            SET roles=0,grantable_roles=0,roles_hq=0,grantable_hq=0,
                roles_base=0,grantable_base=0,roles_other=0,grantable_other=0,title_mask=0
            WHERE corporation_id=$1 AND character_id=$2
            """,corporationId,characterId);
        if (await suspend.ExecuteNonQueryAsync(token) != 1)
            throw new CorporationError("CORPORATION_MEMBER_NOT_FOUND");
    }

    private async Task<long?> PriorVoteCeoAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long voteCaseId,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"""
            SELECT prior_ceo_id FROM {_schema}.vote_member_role_snapshots
            WHERE vote_case_id=$1
            """,voteCaseId);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : Convert.ToInt64(value,System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task RestoreVoteMemberRoleSnapshotAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long voteCaseId,
        long corporationId,long characterId,CancellationToken token)
    {
        await using (var restore = Command(connection,tx,$"""
            UPDATE {_schema}.members m
            SET roles=s.roles,grantable_roles=s.grantable_roles,
                roles_hq=s.roles_hq,grantable_hq=s.grantable_hq,
                roles_base=s.roles_base,grantable_base=s.grantable_base,
                roles_other=s.roles_other,grantable_other=s.grantable_other,
                title_mask=s.title_mask,role_stasis_ms=s.role_stasis_ms
            FROM {_schema}.vote_member_role_snapshots s
            WHERE s.vote_case_id=$1 AND s.corporation_id=$2 AND s.character_id=$3
              AND m.corporation_id=s.corporation_id AND m.character_id=s.character_id
            """,voteCaseId,corporationId,characterId))
            await restore.ExecuteNonQueryAsync(token);
        await DeleteVoteMemberRoleSnapshotAsync(connection,tx,voteCaseId,characterId,token);
    }

    private async Task DeleteVoteMemberRoleSnapshotAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long voteCaseId,
        long characterId,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"""
            DELETE FROM {_schema}.vote_member_role_snapshots
            WHERE vote_case_id=$1 AND character_id=$2
            """,voteCaseId,characterId);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<long> VoteWeightAsync(NpgsqlConnection connection,NpgsqlTransaction? tx,long corporationId,
        long actorId,CancellationToken token)
    {
        await using var command = Command(connection,tx,$"""
            WITH issuer AS (
                SELECT id,ceo_id,ceo_roles_suspended FROM {_schema}.corporations WHERE id=$1
            ),
            corporate_owner AS (
                SELECT CASE WHEN issuer.ceo_id=$2 AND NOT issuer.ceo_roles_suspended THEN issuer.id ELSE (
                    SELECT owned.id
                    FROM {_schema}.members member
                    JOIN {_schema}.corporations owned
                      ON owned.id=member.corporation_id AND owned.ceo_id=$2 AND NOT owned.ceo_roles_suspended
                    WHERE member.character_id=$2
                    LIMIT 1
                ) END AS owner_id
                FROM issuer
            )
            SELECT COALESCE(personal.shares,0)+COALESCE(corporate.shares,0)
            FROM issuer
            LEFT JOIN {_schema}.vote_shareholders personal
              ON personal.corporation_id=issuer.id AND personal.shareholder_id=$2
            LEFT JOIN corporate_owner ON true
            LEFT JOIN {_schema}.vote_shareholders corporate
              ON corporate.corporation_id=issuer.id AND corporate.shareholder_id=corporate_owner.owner_id
            """,corporationId,actorId);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? 0L : Convert.ToInt64(value,System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task CreditSharesInTransactionAsync(NpgsqlConnection connection,NpgsqlTransaction tx,long corporationId,
        long shareholderId,long amount,CancellationToken token)
    {
        if (amount <= 0) throw new CorporationError("CORPORATION_INVALID_SHARE_TRANSFER");
        var current = await ShareCountAsync(connection,tx,corporationId,shareholderId,token);
        if (current > long.MaxValue-amount) throw new CorporationError("CORPORATION_SHARE_LIMIT");
        await using var command = Command(connection,tx,$"""
            INSERT INTO {_schema}.vote_shareholders(corporation_id,shareholder_id,shares)
            VALUES($1,$2,$3)
            ON CONFLICT(corporation_id,shareholder_id)
            DO UPDATE SET shares={_schema}.vote_shareholders.shares+EXCLUDED.shares
            """,corporationId,shareholderId,amount);
        await command.ExecuteNonQueryAsync(token);
    }

    public Task<bool> CanRunForCEOAsync(long corporationId,long actorId,CancellationToken token = default)
    {
        if (corporationId <= 0 || actorId <= 0) return Task.FromResult(false);
        return WriteAsync(async (connection,tx) =>
        {
            var corporation = await GetAsync(connection,tx,corporationId,false,token);
            return corporation is not null &&
                await CanRunForCEOInTransactionAsync(connection,tx,corporation,actorId,token);
        },token);
    }

    private static CorporationVoteCase ReadVoteCase(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetString(3),reader.GetString(4),
        reader.GetInt32(5),reader.GetFieldValue<byte[]>(6),reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetInt64(8),reader.IsDBNull(9) ? null : reader.GetInt64(9),
        reader.IsDBNull(10) ? null : reader.GetInt64(10),reader.GetInt64(11),reader.GetInt64(12),
        reader.GetBoolean(13),reader.GetInt64(14));
}


/// <summary>A rented corporation office and its current 30-day term.</summary>
public sealed record CorporationOffice(long OfficeId, long OfficeFolderId, long CorporationId, long StationId, long SolarSystemId,
    decimal RentIsk, long RentedAtMs, long ExpiresAtMs);

/// <summary>Static station facts used to seed deterministic office pricing.</summary>
public sealed record CorporationOfficeStation(long Id, int TypeId, long OwnerId, long SolarSystemId,
    double Security, int OfficeCapacity = 24);

/// <summary>Durable corporation office rentals, daily rental quotes, and the next-cycle bill.</summary>
public sealed record CorporationOfficeRentBill(
    long BillId,
    long OfficeId,
    long CorporationId,
    long StationId,
    decimal AmountIsk,
    long IssuedAtMs,
    long DueAtMs,
    int Status,
    long? PaidAtMs = null,
    long? PaidByOwnerId = null);

public sealed record CorporationBillAutomaticPaySettings(
    int DivisionId,
    IReadOnlyDictionary<int, bool> EnabledBillTypes);

public sealed class CorporationOfficeStore
{
    public const long RentalCycleMs = 30L * 24 * 60 * 60 * 1000;
    public const decimal MinimumRentIsk = 10_000m;
    public const int ImpoundedFlag = 50;
    public const int OfficeFolderTypeId = 26;

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public CorporationOfficeStore(NpgsqlDataSource source, string schema = CorporationStore.DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        _source = source;
        _schema = schema;
    }

    public static IReadOnlyList<Migration> MigrationSet() =>
    [
        new Migration("008_corporation_offices", """
            CREATE TABLE office_rentals (
                office_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                office_folder_id bigint NOT NULL CHECK (office_folder_id > 0),
                corporation_id bigint NOT NULL CHECK (corporation_id > 0),
                station_id bigint NOT NULL CHECK (station_id > 0),
                solar_system_id bigint NOT NULL CHECK (solar_system_id > 0),
                rent_isk numeric(38,2) NOT NULL CHECK (rent_isk >= 10000),
                rented_at_ms bigint NOT NULL CHECK (rented_at_ms >= 0),
                expires_at_ms bigint NOT NULL CHECK (expires_at_ms > rented_at_ms),
                active boolean NOT NULL DEFAULT true,
                unrented_at_ms bigint
            );
            CREATE UNIQUE INDEX office_rentals_folder_id ON office_rentals(office_folder_id);
            CREATE UNIQUE INDEX office_rentals_active_corporation_station
                ON office_rentals(corporation_id,station_id) WHERE active;
            CREATE INDEX office_rentals_station ON office_rentals(station_id) WHERE active;
            CREATE INDEX office_rentals_corporation ON office_rentals(corporation_id) WHERE active;

            CREATE TABLE office_rent_bills (
                bill_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                office_id bigint NOT NULL,
                corporation_id bigint NOT NULL CHECK (corporation_id > 0),
                station_id bigint NOT NULL CHECK (station_id > 0),
                amount_isk numeric(38,2) NOT NULL CHECK (amount_isk >= 10000),
                issued_at_ms bigint NOT NULL CHECK (issued_at_ms >= 0),
                due_at_ms bigint NOT NULL CHECK (due_at_ms > issued_at_ms),
                status smallint NOT NULL DEFAULT 0 CHECK (status BETWEEN 0 AND 2),
                operation_key text NOT NULL UNIQUE
            );
            CREATE INDEX office_rent_bills_corporation_due ON office_rent_bills(corporation_id,due_at_ms)
                WHERE status = 0;

            CREATE TABLE office_station_prices (
                station_id bigint PRIMARY KEY CHECK (station_id > 0),
                owner_id bigint NOT NULL CHECK (owner_id > 0),
                solar_system_id bigint NOT NULL CHECK (solar_system_id > 0),
                type_id integer NOT NULL CHECK (type_id > 0),
                office_capacity integer NOT NULL CHECK (office_capacity > 0),
                price_isk numeric(38,2) NOT NULL CHECK (price_isk >= 10000),
                pricing_day bigint NOT NULL CHECK (pricing_day >= 0)
            );
            """),
        new Migration("009_bill_manager", """
            ALTER TABLE office_rent_bills ADD COLUMN paid_at_ms bigint;
            ALTER TABLE office_rent_bills ADD COLUMN paid_by_owner_id bigint;

            CREATE TABLE corporation_bill_auto_pay_settings (
                owner_id bigint PRIMARY KEY CHECK (owner_id > 0),
                division_id integer NOT NULL DEFAULT 1000 CHECK (division_id BETWEEN 1000 AND 1006),
                enabled_bill_types jsonb NOT NULL DEFAULT '{}'::jsonb
                    CHECK (jsonb_typeof(enabled_bill_types) = 'object')
            );
            """)
    ];

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source,
        string schema = CorporationStore.DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    public async Task<T> WriteAsync<T>(Func<NpgsqlConnection,NpgsqlTransaction,Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var result = await action(connection, transaction);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task LockStationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long stationId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0))", $"office:{_schema}:{stationId}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CorporationOffice?> GetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long corporationId, long stationId, bool forUpdate = false, CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT office_id,office_folder_id,corporation_id,station_id,solar_system_id,rent_isk,rented_at_ms,expires_at_ms
            FROM {_schema}.office_rentals
            WHERE corporation_id=$1 AND station_id=$2 AND active
            {(forUpdate ? "FOR UPDATE" : "")}
            """, corporationId, stationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadOffice(reader) : null;
    }

    public async Task<IReadOnlyList<CorporationOffice>> ForCorporationAsync(long corporationId,
        CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"""
            SELECT office_id,office_folder_id,corporation_id,station_id,solar_system_id,rent_isk,rented_at_ms,expires_at_ms
            FROM {_schema}.office_rentals WHERE corporation_id=$1 AND active ORDER BY station_id
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = corporationId });
        var offices = new List<CorporationOffice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) offices.Add(ReadOffice(reader));
        return offices;
    }

    public async Task<IReadOnlyList<CorporationOfficeRentBill>> BillsForCorporationAsync(
        long corporationId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"""
            SELECT bill_id,office_id,corporation_id,station_id,amount_isk,issued_at_ms,due_at_ms,status,
                   paid_at_ms,paid_by_owner_id
            FROM {_schema}.office_rent_bills
            WHERE corporation_id=$1 AND status=0
            ORDER BY due_at_ms,bill_id
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = corporationId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var bills = new List<CorporationOfficeRentBill>();
        while (await reader.ReadAsync(cancellationToken)) bills.Add(ReadRentBill(reader));
        return bills;
    }


    public async Task<IReadOnlyList<CorporationOfficeRentBill>> BillsForCreditorAsync(
        long creditorId, Func<long, long?> stationOwnerOf, CancellationToken cancellationToken = default)
    {
        if (creditorId <= 0) return [];
        await using var command = _source.CreateCommand($"""
            SELECT bill_id,office_id,corporation_id,station_id,amount_isk,issued_at_ms,due_at_ms,status,
                   paid_at_ms,paid_by_owner_id
            FROM {_schema}.office_rent_bills
            WHERE status=0
            ORDER BY due_at_ms,bill_id
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var bills = new List<CorporationOfficeRentBill>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var bill = ReadRentBill(reader);
            if (stationOwnerOf(bill.StationId) == creditorId) bills.Add(bill);
        }
        return bills;
    }

    public async Task<IReadOnlyDictionary<long, CorporationBillAutomaticPaySettings>> AutomaticPaySettingsAsync(
        IEnumerable<long> ownerIds, CancellationToken cancellationToken = default)
    {
        var result = ownerIds.Where(ownerId => ownerId > 0).Distinct().ToDictionary(
            ownerId => ownerId,
            _ => new CorporationBillAutomaticPaySettings(1000, new Dictionary<int, bool>()));
        foreach (var ownerId in result.Keys.ToArray())
        {
            await using var command = _source.CreateCommand($"""
                SELECT division_id,enabled_bill_types::text
                FROM {_schema}.corporation_bill_auto_pay_settings
                WHERE owner_id=$1
                """);
            command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) continue;
            var divisionId = reader.GetInt32(0);
            var persisted = JsonSerializer.Deserialize<Dictionary<string, bool>>(reader.GetString(1)) ?? [];
            var billTypes = new Dictionary<int, bool>();
            foreach (var (key, enabled) in persisted)
                if (int.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var billTypeId))
                    billTypes[billTypeId] = enabled;
            result[ownerId] = new CorporationBillAutomaticPaySettings(divisionId, billTypes);
        }
        return result;
    }

    public async Task SetAutomaticPaySettingsAsync(
        IReadOnlyDictionary<long, CorporationBillAutomaticPaySettings> settings,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (ownerId, value) in settings)
        {
            if (ownerId <= 0) throw new ArgumentOutOfRangeException(nameof(settings));
            var divisionId = value.DivisionId is >= 1000 and <= 1006 ? value.DivisionId : 1000;
            var billTypes = value.EnabledBillTypes.ToDictionary(
                entry => entry.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry => entry.Value);
            var json = JsonSerializer.Serialize(billTypes);
            await using var command = Command(connection, transaction, $"""
                INSERT INTO {_schema}.corporation_bill_auto_pay_settings
                    (owner_id,division_id,enabled_bill_types)
                VALUES ($1,$2,$3::jsonb)
                ON CONFLICT (owner_id) DO UPDATE SET
                    division_id=EXCLUDED.division_id,
                    enabled_bill_types=EXCLUDED.enabled_bill_types
                """, ownerId, divisionId, json);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> PayBillAsync(
        long billId,
        long corporationId,
        long paidByOwnerId,
        long nowMs,
        Func<NpgsqlConnection, NpgsqlTransaction, CorporationOfficeRentBill, Task> settle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settle);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var billCommand = Command(connection, transaction, $"""
            SELECT bill_id,office_id,corporation_id,station_id,amount_isk,issued_at_ms,due_at_ms,status,
                   paid_at_ms,paid_by_owner_id
            FROM {_schema}.office_rent_bills
            WHERE bill_id=$1 AND corporation_id=$2 AND status=0
            FOR UPDATE
            """, billId, corporationId);
        await using var billReader = await billCommand.ExecuteReaderAsync(cancellationToken);
        if (!await billReader.ReadAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        var bill = ReadRentBill(billReader);
        await billReader.CloseAsync();

        await using var officeCommand = Command(connection, transaction, $"""
            SELECT expires_at_ms
            FROM {_schema}.office_rentals
            WHERE office_id=$1 AND corporation_id=$2 AND active
            FOR UPDATE
            """, bill.OfficeId, corporationId);
        if (await officeCommand.ExecuteScalarAsync(cancellationToken) is not long)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await settle(connection, transaction, bill);
        var nextDueAtMs = checked(Math.Max(nowMs, bill.DueAtMs) + RentalCycleMs);
        await using (var updateOffice = Command(connection, transaction, $"""
            UPDATE {_schema}.office_rentals SET expires_at_ms=$2
            WHERE office_id=$1 AND corporation_id=$3 AND active
            """, bill.OfficeId, nextDueAtMs, corporationId))
            if (await updateOffice.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new CorporationError("OFFICE_RENTAL_FAILED");
        await using (var markPaid = Command(connection, transaction, $"""
            UPDATE {_schema}.office_rent_bills SET status=1,paid_at_ms=$2,paid_by_owner_id=$3
            WHERE bill_id=$1 AND status=0
            """, bill.BillId, nowMs, paidByOwnerId))
            if (await markPaid.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new CorporationError("BILL_NOT_PAYABLE");
        await using (var nextBill = Command(connection, transaction, $"""
            INSERT INTO {_schema}.office_rent_bills
                (office_id,corporation_id,station_id,amount_isk,issued_at_ms,due_at_ms,status,operation_key)
            VALUES ($1,$2,$3,$4,$5,$6,0,$7)
            """, bill.OfficeId, corporationId, bill.StationId, bill.AmountIsk, nowMs, nextDueAtMs,
            $"office-rent:{bill.OfficeId}:renewal:{bill.BillId}"))
            await nextBill.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static CorporationOfficeRentBill ReadRentBill(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
        reader.GetDecimal(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt16(7),
        reader.IsDBNull(8) ? null : reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetInt64(9));

    public async Task<IReadOnlyList<long>> CorporationsAtStationAsync(long stationId,
        CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"""
            SELECT corporation_id FROM {_schema}.office_rentals
            WHERE station_id=$1 AND active ORDER BY corporation_id
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = stationId });
        var corporations = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) corporations.Add(reader.GetInt64(0));
        return corporations;
    }

    public async Task<int> CountAtStationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long stationId, CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection, transaction,
            $"SELECT count(*) FROM {_schema}.office_rentals WHERE station_id=$1 AND active", stationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<decimal> PriceQuoteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CorporationOfficeStation station, int occupiedOffices, long nowMs,
        CancellationToken cancellationToken = default)
    {
        if (station.Id <= 0 || station.TypeId <= 0 || station.OwnerId <= 0 || station.SolarSystemId <= 0 ||
            station.OfficeCapacity <= 0)
            throw new CorporationError("OFFICE_STATION_DATA_INVALID");

        var day = Math.Max(0, nowMs / (24L * 60 * 60 * 1000));
        var seed = SeedPrice(station);
        await using (var insert = Command(connection, transaction, $"""
            INSERT INTO {_schema}.office_station_prices
                (station_id,owner_id,solar_system_id,type_id,office_capacity,price_isk,pricing_day)
            VALUES ($1,$2,$3,$4,$5,$6,$7)
            ON CONFLICT (station_id) DO NOTHING
            """, station.Id, station.OwnerId, station.SolarSystemId, station.TypeId,
            station.OfficeCapacity, seed, day))
            await insert.ExecuteNonQueryAsync(cancellationToken);

        decimal price;
        long priorDay;
        await using (var read = Command(connection, transaction, $"""
            SELECT price_isk,pricing_day FROM {_schema}.office_station_prices
            WHERE station_id=$1 FOR UPDATE
            """, station.Id))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new CorporationError("OFFICE_PRICE_STATE_MISSING");
            price = reader.GetDecimal(0);
            priorDay = reader.GetInt64(1);
        }

        var elapsed = Math.Max(0, day - priorDay);
        if (elapsed > 0)
        {
            var free = Math.Max(0, station.OfficeCapacity - occupiedOffices);
            var multiplier = free == 0 ? 1.05m : free == 1 ? 1m :
                Math.Max(0.95m, 1m - Math.Min(0.05m, (free - 1) * 0.005m));
            for (long i = 0; i < Math.Min(elapsed, 5000); i++)
            {
                price = multiplier > 1 && price > decimal.MaxValue / multiplier
                    ? decimal.MaxValue
                    : Math.Max(MinimumRentIsk, decimal.Round(price * multiplier, 2, MidpointRounding.AwayFromZero));
            }
            await using var update = Command(connection, transaction, $"""
                UPDATE {_schema}.office_station_prices
                SET owner_id=$2,solar_system_id=$3,type_id=$4,office_capacity=$5,price_isk=$6,pricing_day=$7
                WHERE station_id=$1
                """, station.Id, station.OwnerId, station.SolarSystemId, station.TypeId,
                station.OfficeCapacity, price, day);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        return Math.Max(MinimumRentIsk, price);
    }

    public async Task<CorporationOffice> RentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long corporationId, long officeFolderId, CorporationOfficeStation station, decimal price, long nowMs,
        CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection, transaction, $"""
            INSERT INTO {_schema}.office_rentals
                (corporation_id,office_folder_id,station_id,solar_system_id,rent_isk,rented_at_ms,expires_at_ms,active)
            VALUES ($1,$2,$3,$4,$5,$6,$7,true)
            RETURNING office_id,office_folder_id,corporation_id,station_id,solar_system_id,rent_isk,rented_at_ms,expires_at_ms
            """, corporationId, officeFolderId, station.Id, station.SolarSystemId, price, nowMs, nowMs + RentalCycleMs);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new CorporationError("OFFICE_RENTAL_FAILED");
        var office = ReadOffice(reader);
        await reader.CloseAsync();

        await using var bill = Command(connection, transaction, $"""
            INSERT INTO {_schema}.office_rent_bills
                (office_id,corporation_id,station_id,amount_isk,issued_at_ms,due_at_ms,status,operation_key)
            VALUES ($1,$2,$3,$4,$5,$6,0,$7)
            """, office.OfficeId, corporationId, station.Id, price, nowMs, nowMs + RentalCycleMs,
            $"office-rent:{office.OfficeId}:next");
        await bill.ExecuteNonQueryAsync(cancellationToken);
        return office;
    }

    public async Task UnrentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CorporationOffice office, long nowMs, CancellationToken cancellationToken = default)
    {
        await using (var command = Command(connection, transaction, $"""
            UPDATE {_schema}.office_rentals SET active=false,unrented_at_ms=$2
            WHERE office_id=$1 AND active
            """, office.OfficeId, nowMs))
            await command.ExecuteNonQueryAsync(cancellationToken);
        await using (var bills = Command(connection, transaction, $"""
            UPDATE {_schema}.office_rent_bills SET status=2
            WHERE office_id=$1 AND status=0
            """, office.OfficeId))
            await bills.ExecuteNonQueryAsync(cancellationToken);
    }

    private static decimal SeedPrice(CorporationOfficeStation station)
    {
        // ponytail: the SDE has station identity but no live rent history; seed a repeatable 10k-110k price from those static IDs until a price feed exists.
        var seed = unchecked((ulong)station.Id * 0x9E3779B185EBCA87UL ^
            (ulong)station.TypeId * 0xC2B2AE3D27D4EB4FUL ^
            (ulong)station.SolarSystemId * 0x165667B19E3779F9UL ^ (ulong)station.OwnerId);
        return MinimumRentIsk + seed % 1001UL * 100UL;
    }

    private static CorporationOffice ReadOffice(NpgsqlDataReader reader) =>
        new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
            reader.GetDecimal(5), reader.GetInt64(6), reader.GetInt64(7));

    private NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        return command;
    }
}
