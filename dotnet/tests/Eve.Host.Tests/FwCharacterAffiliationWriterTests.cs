using Eve.Characters;
using Eve.Data;
using Eve.Events;
using Eve.FactionWarfare;
using Eve.Host;
using Eve.Relations;
using Eve.Scheduler;
using Eve.Services.Crimewatch;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class FwAffiliationPgFactAttribute : FactAttribute
{
    public FwAffiliationPgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null)
            Skip = "EVE_PG_URL is not set";
    }
}

public sealed class FwCharacterAffiliationWriterTests
{
    private const long Amarr = 500_003;
    private const long Minmatar = 500_002;
    private const long Caldari = 500_001;
    private const long Corporation = 98_000_001;
    private const long OtherCorporation = 98_000_002;

    [FwAffiliationPgFact]
    public async Task Direct_affiliation_persists_reconciles_after_restart_and_updates_legal_war_resolution()
    {
        await using var rig = await FwAffiliationRig.CreateAsync();
        var amarrPilot = await rig.AddCharacterAsync("Militia Amarr", Corporation);
        var minmatarPilot = await rig.AddCharacterAsync("Militia Minmatar", Corporation);
        var caldariPilot = await rig.AddCharacterAsync("Militia Caldari", Corporation);
        var unaffiliatedPilot = await rig.AddCharacterAsync("Militia None", Corporation);

        Assert.True(await rig.Writer.SaveAsync(amarrPilot, Amarr, 0, CancellationToken.None));
        Assert.True(await rig.Writer.SaveAsync(minmatarPilot, Minmatar, 0, CancellationToken.None));
        Assert.True(await rig.Writer.SaveAsync(caldariPilot, Caldari, 0, CancellationToken.None));
        Assert.False(await rig.Writer.SaveAsync(unaffiliatedPilot + 1, Minmatar, 0, CancellationToken.None));

        var persistedMinmatar = (await rig.Characters.GetAsync(minmatarPilot))!;
        Assert.Equal(Minmatar, persistedMinmatar.DirectFactionId);
        var minmatarAffiliation = await rig.Relations.AffiliationOfAsync(minmatarPilot);
        Assert.Equal(Corporation, minmatarAffiliation.Corporation);
        Assert.Equal(Minmatar, minmatarAffiliation.Faction);

        var wars = new RelationWars(rig.Relations);
        Assert.True(await wars.AreAtWarAsync(amarrPilot, minmatarPilot, 1_700_000_000_000));
        Assert.False(await wars.AreAtWarAsync(amarrPilot, caldariPilot, 1_700_000_000_000));
        Assert.False(await wars.AreAtWarAsync(amarrPilot, unaffiliatedPilot, 1_700_000_000_000));

        await rig.EndDirectFactionAsync(minmatarPilot, Minmatar);
        var restarted = rig.Reopen();
        await restarted.Writer.ReconcileAsync(minmatarPilot, persistedMinmatar.DirectFactionId, CancellationToken.None);
        Assert.Equal(Minmatar, (await restarted.Relations.AffiliationOfAsync(minmatarPilot)).Faction);
        Assert.True(await new RelationWars(restarted.Relations).AreAtWarAsync(amarrPilot, minmatarPilot, 1_700_000_000_000));

        await restarted.Relations.SetAsync(RelationKinds.FactionMember.Name, Corporation, Amarr, 1, "corporation enlistment");
        const long cooldown = 133_000_000_000_000_000;
        Assert.True(await restarted.Writer.SaveAsync(minmatarPilot, null, cooldown, CancellationToken.None));
        var removed = (await restarted.Characters.GetAsync(minmatarPilot))!;
        Assert.Null(removed.DirectFactionId);
        Assert.Equal(cooldown, removed.DirectEnlistmentCooldownFileTime);
        Assert.Equal(Amarr, (await restarted.Relations.AffiliationOfAsync(minmatarPilot)).Faction);
        Assert.False(await new RelationWars(restarted.Relations).AreAtWarAsync(amarrPilot, minmatarPilot, 1_700_000_000_000));
    }

    [FwAffiliationPgFact]
    public async Task A_due_corporation_enlistment_is_projected_before_crimewatch_resolves_legality()
    {
        await using var rig = await FwAffiliationRig.CreateAsync();
        var corporationPilot = await rig.AddCharacterAsync("Corporation Militia", Corporation);
        var directPilot = await rig.AddCharacterAsync("Direct Militia", OtherCorporation);
        Assert.True(await rig.Writer.SaveAsync(directPilot, Minmatar, 0, CancellationToken.None));
        Assert.Equal(EnlistOutcome.Accepted, await rig.FactionWarfare.JoinAsync(
            FwStore.CorporationKind, Corporation, Amarr, 0.5, rig.Now));
        var effectiveAt = FwRules.NextDowntimeMs(rig.Now);
        Assert.Empty(await rig.Relations.FromAsync(RelationKinds.FactionMember.Name, Corporation));

        rig.Now = effectiveAt;
        var wars = new RelationWars(rig.Relations, rig.ResolveWarFactionAsync);
        Assert.True(await wars.AreAtWarAsync(corporationPilot, directPilot, effectiveAt));
        Assert.Equal(Amarr, (await rig.Relations.AffiliationOfAsync(corporationPilot)).Faction);
    }

    [FwAffiliationPgFact]
    public async Task Direct_affiliation_relation_failure_rolls_back_the_character_state_change()
    {
        await using var rig = await FwAffiliationRig.CreateAsync();
        var characterId = await rig.AddCharacterAsync("Rollback Pilot", Corporation);
        Assert.True(await rig.Writer.SaveAsync(characterId, Amarr, 0, CancellationToken.None));

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"DROP TABLE {rig.RelationSchema}.relation_history", connection))
            await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<PostgresException>(() => rig.Writer.SaveAsync(
            characterId, null, 133_000_000_000_000_000, CancellationToken.None));

        var character = (await rig.Characters.GetAsync(characterId))!;
        Assert.Equal(Amarr, character.DirectFactionId);
        Assert.Equal(0, character.DirectEnlistmentCooldownFileTime);
        Assert.Equal(Amarr, (await rig.Relations.AffiliationOfAsync(characterId)).Faction);
    }

    private sealed class FwAffiliationRig : IAsyncDisposable
    {
        private readonly string _schedulerSchema;
        private readonly string _eventSchema;
        private readonly string _characterSchema;
        private readonly string _relationSchema;
        private readonly string _fwSchema;

        private FwAffiliationRig(NpgsqlDataSource source, string id)
        {
            Source = source;
            _schedulerSchema = "sch_fw_" + id;
            _eventSchema = "evt_fw_" + id;
            _characterSchema = "char_fw_" + id;
            _relationSchema = "rel_fw_" + id;
            _fwSchema = "war_fw_" + id;
            Characters = new PgCharacterStore(source, _characterSchema);
            var scheduler = new DurableScheduler(source, () => Now, new SchedulerOptions { Concurrency = 1 }, _schedulerSchema);
            var bus = new EventBus(source, scheduler, null, _eventSchema);
            Relations = new RelationStore(source, scheduler, bus, _relationSchema);
            Writer = new FwCharacterAffiliationWriter(source, Characters, Relations);
            FactionWarfare = new FwService(new FwStore(source, _fwSchema), new LoyaltyPointStore(source, "wal_fw_" + id), Relations);
        }

        public NpgsqlDataSource Source { get; }
        public PgCharacterStore Characters { get; }
        public RelationStore Relations { get; }
        public FwCharacterAffiliationWriter Writer { get; }
        public FwService FactionWarfare { get; }
        public string RelationSchema => _relationSchema;
        public long Now { get; set; } = 1_699_956_000_000;

        public static async Task<FwAffiliationRig> CreateAsync()
        {
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            var rig = new FwAffiliationRig(source, Guid.NewGuid().ToString("N")[..12]);
            try
            {
                await PgCharacterStore.MigrateAsync(source, rig._characterSchema);
                await DurableScheduler.MigrateAsync(source, rig._schedulerSchema);
                await EventBus.MigrateAsync(source, rig._eventSchema);
                await RelationStore.MigrateAsync(source, rig._relationSchema);
                await FwStore.MigrateAsync(source, rig._fwSchema);
                return rig;
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }
        }

        public async Task<long> AddCharacterAsync(string name, long corporationId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var characterId = await Characters.InsertAsync(connection, transaction,
                new NewCharacter(1, name, 1, 1, 1, 10, 35, 1373, corporationId, null,
                    60_000_610, 30_002_775, 2_000_040_6, 10_000_033, null, null));
            await Relations.SetAsync(connection, transaction, RelationKinds.CorporationMember.Name,
                characterId, corporationId, 1, "test corporation membership");
            await transaction.CommitAsync();
            return characterId;
        }

        public async Task EndDirectFactionAsync(long characterId, long factionId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Relations.EndAsync(connection, transaction, RelationKinds.FactionMember.Name,
                characterId, factionId, "restart reconciliation test");
            await transaction.CommitAsync();
        }

        public async ValueTask<long?> ResolveWarFactionAsync(long characterId, CancellationToken cancellationToken)
        {
            var character = await Characters.GetAsync(characterId, cancellationToken);
            if (character is null)
                return null;
            var directFactionId = character.DirectFactionId is > 0 ? character.DirectFactionId : null;
            await Writer.ReconcileAsync(characterId, directFactionId, cancellationToken);
            var corporationFactionId = await FactionWarfare.FactionOfAsync(
                FwStore.CorporationKind, character.CorporationId, Now, cancellationToken);
            return directFactionId ?? (corporationFactionId > 0 ? corporationFactionId : null);
        }

        public (PgCharacterStore Characters, RelationStore Relations, FwCharacterAffiliationWriter Writer, FwService FactionWarfare) Reopen()
        {
            var characters = new PgCharacterStore(Source, _characterSchema);
            var scheduler = new DurableScheduler(Source, () => Now,
                new SchedulerOptions { Concurrency = 1 }, _schedulerSchema);
            var bus = new EventBus(Source, scheduler, null, _eventSchema);
            var relations = new RelationStore(Source, scheduler, bus, _relationSchema);
            var warfare = new FwService(new FwStore(Source, _fwSchema), new LoyaltyPointStore(Source, "wal_fw_" + _fwSchema[7..]), relations);
            return (characters, relations, new FwCharacterAffiliationWriter(Source, characters, relations), warfare);
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS {_characterSchema} CASCADE; DROP SCHEMA IF EXISTS {_relationSchema} CASCADE; " +
                $"DROP SCHEMA IF EXISTS {_schedulerSchema} CASCADE; DROP SCHEMA IF EXISTS {_eventSchema} CASCADE; " +
                $"DROP SCHEMA IF EXISTS {_fwSchema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
            await Source.DisposeAsync();
        }
    }
}
