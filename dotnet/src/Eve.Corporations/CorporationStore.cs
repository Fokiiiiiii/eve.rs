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
        new Migration("008_corporation_recruitment_ads", """
            CREATE TABLE corporation_recruitment_ads (
                ad_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                corporation_id bigint NOT NULL REFERENCES corporations(id) ON DELETE CASCADE,
                channel_id bigint NOT NULL DEFAULT 0,
                type_mask bigint NOT NULL DEFAULT 0 CHECK (type_mask >= 0),
                lang_mask bigint NOT NULL DEFAULT 0 CHECK (lang_mask >= 0),
                description text NOT NULL DEFAULT '',
                title text NOT NULL DEFAULT '',
                create_filetime bigint NOT NULL CHECK (create_filetime > 0),
                expiry_filetime bigint NOT NULL CHECK (expiry_filetime >= create_filetime),
                hour_mask1 bigint NOT NULL DEFAULT 0 CHECK (hour_mask1 >= 0),
                min_sp bigint NOT NULL DEFAULT 0 CHECK (min_sp >= 0),
                other_mask bigint NOT NULL DEFAULT 0 CHECK (other_mask >= 0),
                recruiters bigint[] NOT NULL DEFAULT '{}',
                create_request_key text UNIQUE,
                create_request_fingerprint text
            );
            CREATE INDEX corporation_recruitment_ads_by_corp
                ON corporation_recruitment_ads(corporation_id,ad_id);
            CREATE INDEX corporation_recruitment_ads_by_expiry
                ON corporation_recruitment_ads(expiry_filetime);
            """),
        new Migration("009_corporation_social", """
            CREATE TABLE corporation_contacts (
                corporation_id bigint NOT NULL REFERENCES corporations(id) ON DELETE CASCADE,
                contact_id bigint NOT NULL CHECK (contact_id > 0),
                relationship_id integer NOT NULL CHECK (relationship_id BETWEEN -10 AND 10),
                label_mask bigint NOT NULL DEFAULT 0 CHECK (label_mask >= 0),
                in_watchlist boolean NOT NULL DEFAULT false,
                PRIMARY KEY (corporation_id,contact_id)
            );
            CREATE INDEX corporation_contacts_by_contact ON corporation_contacts(contact_id);

            CREATE TABLE corporation_contact_labels (
                corporation_id bigint NOT NULL REFERENCES corporations(id) ON DELETE CASCADE,
                label_id bigint NOT NULL CHECK (label_id BETWEEN 1 AND 4611686018427387904 AND (label_id & (label_id - 1)) = 0),
                name text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
                color integer NOT NULL CHECK (color BETWEEN 0 AND 16777215),
                PRIMARY KEY (corporation_id,label_id)
            );
            CREATE UNIQUE INDEX corporation_contact_labels_name ON corporation_contact_labels(corporation_id,lower(name));

            CREATE TABLE corporation_contact_label_counters (
                corporation_id bigint PRIMARY KEY REFERENCES corporations(id) ON DELETE CASCADE,
                next_label_id bigint NOT NULL CHECK (next_label_id = 0 OR (next_label_id BETWEEN 1 AND 4611686018427387904 AND (next_label_id & (next_label_id - 1)) = 0))
            );

            CREATE TABLE corporation_bulletins (
                corporation_id bigint NOT NULL REFERENCES corporations(id) ON DELETE CASCADE,
                bulletin_id bigint NOT NULL CHECK (bulletin_id > 0),
                create_character_id bigint NOT NULL CHECK (create_character_id > 0),
                create_filetime bigint NOT NULL CHECK (create_filetime > 0),
                edit_character_id bigint NOT NULL CHECK (edit_character_id > 0),
                edit_filetime bigint NOT NULL CHECK (edit_filetime > 0),
                title text NOT NULL,
                body text NOT NULL,
                sort_order integer NOT NULL CHECK (sort_order >= 0),
                PRIMARY KEY (corporation_id,bulletin_id)
            );
            CREATE INDEX corporation_bulletins_order ON corporation_bulletins(corporation_id,sort_order,bulletin_id);
            """),
        new Migration("010_corporation_war_bills", """
            CREATE TABLE corporation_bills (
                bill_id bigint GENERATED ALWAYS AS IDENTITY (START WITH 1000000000) PRIMARY KEY,
                bill_type_id integer NOT NULL,
                amount_isk numeric(38,2) NOT NULL CHECK (amount_isk > 0),
                interest numeric(38,2) NOT NULL DEFAULT 0 CHECK (interest >= 0),
                debtor_id bigint NOT NULL REFERENCES corporations(id) ON DELETE CASCADE,
                creditor_id bigint NOT NULL CHECK (creditor_id > 0),
                due_filetime bigint NOT NULL CHECK (due_filetime > 0),
                external_id bigint NOT NULL DEFAULT -1,
                external_id2 bigint NOT NULL DEFAULT -1,
                war_id bigint UNIQUE CHECK (war_id IS NULL OR war_id > 0),
                paid boolean NOT NULL DEFAULT false,
                paid_filetime bigint,
                paid_by_owner_id bigint,
                operation_key text NOT NULL UNIQUE CHECK (length(operation_key) > 0),
                request_fingerprint text NOT NULL
            );
            CREATE INDEX corporation_bills_debtor ON corporation_bills(debtor_id,due_filetime,bill_id);
            CREATE INDEX corporation_bills_creditor ON corporation_bills(creditor_id,due_filetime,bill_id);
            """),
        new Migration("011_alliance_registry_state", """
            ALTER TABLE alliance_employment ALTER COLUMN alliance_id DROP NOT NULL;
            CREATE TABLE alliance_departures (
                alliance_id bigint NOT NULL REFERENCES alliances(id) ON DELETE CASCADE,
                corporation_id bigint NOT NULL REFERENCES corporations(id) ON DELETE CASCADE,
                membership_started_ms bigint NOT NULL CHECK (membership_started_ms > 0),
                actor_id bigint NOT NULL CHECK (actor_id > 0),
                mode text NOT NULL CHECK (mode IN ('self-leave','executor-kick')),
                left_ms bigint NOT NULL CHECK (left_ms > 0),
                PRIMARY KEY (alliance_id,corporation_id,membership_started_ms)
            );
            CREATE INDEX alliance_departures_corporation
                ON alliance_departures(corporation_id,left_ms DESC);

            CREATE SEQUENCE alliance_bill_ids AS bigint START WITH 1000000000;
            SELECT setval('alliance_bill_ids',
                GREATEST(1000000000,COALESCE((SELECT MAX(bill_id)+1 FROM corporation_bills),1000000000)),
                false);
            ALTER TABLE corporation_bills ALTER COLUMN bill_id DROP IDENTITY;
            ALTER TABLE corporation_bills ALTER COLUMN bill_id SET DEFAULT nextval('alliance_bill_ids');

            CREATE TABLE alliance_bulletins (
                alliance_id bigint NOT NULL REFERENCES alliances(id) ON DELETE CASCADE,
                bulletin_id bigint NOT NULL CHECK (bulletin_id > 0),
                create_character_id bigint NOT NULL CHECK (create_character_id > 0),
                create_filetime bigint NOT NULL CHECK (create_filetime > 0),
                edit_character_id bigint NOT NULL CHECK (edit_character_id > 0),
                edit_filetime bigint NOT NULL CHECK (edit_filetime > 0),
                title text NOT NULL,
                body text NOT NULL,
                sort_order integer NOT NULL CHECK (sort_order >= 0),
                PRIMARY KEY (alliance_id,bulletin_id)
            );
            CREATE INDEX alliance_bulletins_order
                ON alliance_bulletins(alliance_id,sort_order,edit_filetime DESC,bulletin_id);

            CREATE TABLE alliance_bills (
                bill_id bigint PRIMARY KEY DEFAULT nextval('alliance_bill_ids'),
                bill_type_id integer NOT NULL,
                amount_isk numeric(38,2) NOT NULL CHECK (amount_isk > 0),
                interest numeric(38,2) NOT NULL DEFAULT 0 CHECK (interest >= 0),
                debtor_alliance_id bigint NOT NULL REFERENCES alliances(id) ON DELETE CASCADE,
                creditor_id bigint NOT NULL CHECK (creditor_id > 0),
                due_filetime bigint NOT NULL CHECK (due_filetime > 0),
                external_id bigint NOT NULL DEFAULT -1,
                external_id2 bigint NOT NULL DEFAULT -1,
                war_id bigint UNIQUE CHECK (war_id IS NULL OR war_id > 0),
                paid boolean NOT NULL DEFAULT false,
                paid_filetime bigint,
                paid_by_owner_id bigint,
                operation_key text NOT NULL UNIQUE CHECK (length(operation_key) > 0),
                request_fingerprint text NOT NULL
            );
            CREATE INDEX alliance_bills_debtor
                ON alliance_bills(debtor_alliance_id,due_filetime,bill_id);
            CREATE INDEX alliance_bills_creditor
                ON alliance_bills(creditor_id,due_filetime,bill_id);
            """),
                new Migration("012_alliance_bill_lifecycle", """
                    ALTER TABLE corporation_bills
                        ADD COLUMN processing_status text CHECK (
                            processing_status IS NULL OR processing_status IN ('renewed','defaulted','inactive','orphaned','superseded')
                        );
                    ALTER TABLE corporation_bills
                        ADD COLUMN renewed_bill_id bigint UNIQUE REFERENCES corporation_bills(bill_id);
                    ALTER TABLE alliance_bills
                        ADD COLUMN processing_status text CHECK (
                            processing_status IS NULL OR processing_status IN ('renewed','defaulted','inactive','orphaned','superseded')
                        );
                    ALTER TABLE alliance_bills
                        ADD COLUMN renewed_bill_id bigint UNIQUE REFERENCES alliance_bills(bill_id);
                    CREATE INDEX corporation_bills_due
                        ON corporation_bills(bill_type_id,due_filetime,bill_id)
                        WHERE processing_status IS NULL;
                    CREATE INDEX alliance_bills_due
                        ON alliance_bills(bill_type_id,due_filetime,bill_id)
                        WHERE processing_status IS NULL;
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

    /// <summary>
    /// The wallet read rule of the market transactions tab (EVE.js corporationAuthority.js, V24.01 ValidateCorpWalletDivisionReadAccess):
    /// an Accountant or a Junior Accountant reads every division; any other member reads only the division whose Account Take role it holds,
    /// so a null division (every division) needs the accountant role. Nothing is changed.
    /// </summary>
    public async Task<bool> CanReadWalletDivisionAsync(long characterId,int? accountKey,CancellationToken token = default)
    {
        var character = await _characters.GetAsync(characterId,token);
        var corp = character is null ? null : await GetAsync(character.CorporationId,token);
        var member = await MemberAsync(characterId,token);
        if (corp is null || corp.IsNpc || corp.Deleted || member?.CorporationId != corp.Id) return false;
        var roles = CorporationRoles.Session(corp,member,character!.StationId,await TitlesAsync(corp.Id,token)).AtAll;
        if ((roles & (CorporationRoles.Accountant | CorporationRoles.JuniorAccountant)) != 0) return true;
        return accountKey is { } key && key is >= 1000 and <= 1006 && (roles & CorporationRoles.WalletTakeRole(key)) != 0;
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

    public Task UpdateAggressionSettingsAsync(long actor,long corporation,string jsonPatch,bool friendlyFire,CancellationToken token = default) =>
        WriteAsync(async (connection,tx) =>
        {
            await RequireRoleAsync(connection,tx,corporation,actor,CorporationRoles.Director,token);
            await using var update = Command(connection,tx,$"UPDATE {_schema}.corporations SET settings=settings || $2::jsonb,friendly_fire=$3 WHERE id=$1",corporation,jsonPatch,friendlyFire);
            await update.ExecuteNonQueryAsync(token);
            return true;
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

    private const string RecruitmentAdColumns =
        "ad_id,corporation_id,channel_id,type_mask,lang_mask,description,title,create_filetime,expiry_filetime,hour_mask1,min_sp,other_mask,recruiters";

    private static CorporationRecruitmentAd ReadRecruitmentAd(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4),
        reader.GetString(5),reader.GetString(6),reader.GetInt64(7),reader.GetInt64(8),reader.GetInt64(9),
        reader.GetInt64(10),reader.GetInt64(11),reader.GetFieldValue<long[]>(12));

    public async Task<IReadOnlyList<CorporationRecruitmentAd>> RecruitmentAdsAsync(
        long? corporationId = null,CancellationToken token = default)
    {
        if (corporationId is <= 0) throw new CorporationError("CORPORATION_NOT_FOUND");
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = corporationId is { } corpId
            ? Command(connection,null,$"SELECT {RecruitmentAdColumns} FROM {_schema}.corporation_recruitment_ads WHERE corporation_id=$1 ORDER BY ad_id",corpId)
            : Command(connection,null,$"SELECT {RecruitmentAdColumns} FROM {_schema}.corporation_recruitment_ads ORDER BY corporation_id,ad_id");
        await using var reader = await command.ExecuteReaderAsync(token);
        var ads = new List<CorporationRecruitmentAd>();
        while (await reader.ReadAsync(token)) ads.Add(ReadRecruitmentAd(reader));
        return ads;
    }

    public async Task<CorporationRecruitmentAd?> RecruitmentAdAsync(long corporationId,long adId,CancellationToken token = default)
    {
        if (corporationId <= 0 || adId <= 0) return null;
        await using var connection = await _source.OpenConnectionAsync(token);
        return await RecruitmentAdAsync(connection,null,corporationId,adId,false,token);
    }

    public async Task<IReadOnlyDictionary<long,int>> RecruitmentMemberCountsAsync(
        IEnumerable<long> corporationIds,CancellationToken token = default)
    {
        var ids = corporationIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<long,int>();
        await using var connection = await _source.OpenConnectionAsync(token);
        await using var command = Command(connection,null,
            $"SELECT corporation_id,count(*) FROM {_schema}.members WHERE corporation_id=ANY($1) GROUP BY corporation_id",ids);
        await using var reader = await command.ExecuteReaderAsync(token);
        var counts = new Dictionary<long,int>();
        while (await reader.ReadAsync(token)) counts[reader.GetInt64(0)] = checked((int)reader.GetInt64(1));
        return counts;
    }

    public Task<long> CreateRecruitmentAdAsync(long corporationId,long actorId,long channelId,long typeMask,long langMask,
        string description,string title,long createFiletime,long expiryFiletime,long hourMask1,long minSP,long otherMask,
        IReadOnlyList<long> recruiters,string? requestKey,string? requestFingerprint,CancellationToken token = default)
    {
        if (corporationId <= 0 || actorId <= 0 || channelId < 0 || typeMask < 0 || langMask < 0 ||
            createFiletime <= 0 || expiryFiletime <= createFiletime || hourMask1 < 0 || minSP < 0 || otherMask < 0 ||
            recruiters.Any(id => id <= 0) || recruiters.Distinct().Count() != recruiters.Count ||
            (requestKey is null) != (requestFingerprint is null))
            throw new CorporationError("CORPORATION_INVALID_RECRUITMENT_AD");

        return WriteAsync(async (connection,tx) =>
        {
            await RequireRecruitmentManagerAsync(connection,tx,corporationId,actorId,token);
            await ValidateRecruitersAsync(connection,tx,corporationId,recruiters,token);
            await using var command = Command(connection,tx,$"""
                INSERT INTO {_schema}.corporation_recruitment_ads
                    (corporation_id,channel_id,type_mask,lang_mask,description,title,create_filetime,expiry_filetime,
                     hour_mask1,min_sp,other_mask,recruiters,create_request_key,create_request_fingerprint)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)
                ON CONFLICT(create_request_key) DO UPDATE SET create_request_key=EXCLUDED.create_request_key
                RETURNING ad_id,create_request_fingerprint
                """,corporationId,channelId,typeMask,langMask,description,title,createFiletime,expiryFiletime,
                hourMask1,minSP,otherMask,recruiters.ToArray(),requestKey,requestFingerprint);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new CorporationError("CORPORATION_RECRUITMENT_AD_CREATE_FAILED");
            var id = reader.GetInt64(0);
            var storedFingerprint = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (storedFingerprint != requestFingerprint)
                throw new CorporationError("CORPORATION_OPERATION_CONFLICT");
            return id;
        },token);
    }

    public Task<bool> UpdateRecruitmentAdAsync(long corporationId,long actorId,long adId,long typeMask,long langMask,
        string description,string title,long hourMask1,long minSP,long otherMask,long addedDays,long nowFiletime,
        IReadOnlyList<long> recruiters,CancellationToken token = default)
    {
        if (corporationId <= 0 || actorId <= 0 || adId <= 0 || typeMask < 0 || langMask < 0 || hourMask1 < 0 ||
            minSP < 0 || otherMask < 0 || addedDays < 0 || nowFiletime <= 0 ||
            recruiters.Any(id => id <= 0) || recruiters.Distinct().Count() != recruiters.Count)
            throw new CorporationError("CORPORATION_INVALID_RECRUITMENT_AD");

        return WriteAsync(async (connection,tx) =>
        {
            await RequireRecruitmentManagerAsync(connection,tx,corporationId,actorId,token);
            await ValidateRecruitersAsync(connection,tx,corporationId,recruiters,token);
            var current = await RecruitmentAdAsync(connection,tx,corporationId,adId,true,token);
            if (current is null) return false;
            var expiryFiletime = current.ExpiryFiletime;
            if (addedDays > 0)
                expiryFiletime = checked(Math.Max(expiryFiletime,nowFiletime) + addedDays*864_000_000_000L);
            await using var command = Command(connection,tx,$"""
                UPDATE {_schema}.corporation_recruitment_ads
                SET type_mask=$3,lang_mask=$4,description=$5,title=$6,expiry_filetime=$7,
                    hour_mask1=$8,min_sp=$9,other_mask=$10,recruiters=$11
                WHERE corporation_id=$1 AND ad_id=$2
                """,corporationId,adId,typeMask,langMask,description,title,expiryFiletime,hourMask1,minSP,otherMask,
                recruiters.ToArray());
            return await command.ExecuteNonQueryAsync(token) == 1;
        },token);
    }

    public Task<bool> DeleteRecruitmentAdAsync(long corporationId,long actorId,long adId,CancellationToken token = default)
    {
        if (corporationId <= 0 || actorId <= 0 || adId <= 0)
            throw new CorporationError("CORPORATION_INVALID_RECRUITMENT_AD");
        return WriteAsync(async (connection,tx) =>
        {
            await RequireRecruitmentManagerAsync(connection,tx,corporationId,actorId,token);
            await using var command = Command(connection,tx,
                $"DELETE FROM {_schema}.corporation_recruitment_ads WHERE corporation_id=$1 AND ad_id=$2",
                corporationId,adId);
            return await command.ExecuteNonQueryAsync(token) == 1;
        },token);
    }

    private async Task<CorporationRecruitmentAd?> RecruitmentAdAsync(NpgsqlConnection connection,NpgsqlTransaction? tx,
        long corporationId,long adId,bool locked,CancellationToken token)
    {
        await using var command = Command(connection,tx,
            $"SELECT {RecruitmentAdColumns} FROM {_schema}.corporation_recruitment_ads WHERE corporation_id=$1 AND ad_id=$2" +
                (locked ? " FOR UPDATE" : ""),corporationId,adId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadRecruitmentAd(reader) : null;
    }

    private async Task RequireRecruitmentManagerAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,
        long corporationId,long actorId,CancellationToken token)
    {
        var corporation = await GetAsync(connection,transaction,corporationId,true,token)
            ?? throw new CorporationError("CORPORATION_NOT_FOUND");
        var character = await _characters.LockAndGetAsync(connection,transaction,actorId,token)
            ?? throw new CorporationError("CHARACTER_NOT_FOUND");
        var member = await MemberAsync(connection,transaction,actorId,token);
        if (corporation.IsNpc || corporation.Deleted || character.CorporationId != corporationId || member is null)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
        if (corporation.CeoId == actorId && !corporation.CeoRolesSuspended) return;
        var roles = CorporationRoles.Session(corporation,member,character.StationId,
            await TitlesAsync(connection,transaction,corporationId,token)).AtAll;
        if ((roles & (CorporationRoles.Director|CorporationRoles.PersonnelManager)) == 0)
            throw new CorporationError("CORPORATION_ACCESS_DENIED");
    }

    private async Task ValidateRecruitersAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,
        long corporationId,IReadOnlyList<long> recruiterIds,CancellationToken token)
    {
        foreach (var recruiterId in recruiterIds)
            if (await MemberAsync(connection,transaction,recruiterId,token) is not { } member ||
                member.CorporationId != corporationId)
                throw new CorporationError("CORPORATION_ACCESS_DENIED");
    }

}
