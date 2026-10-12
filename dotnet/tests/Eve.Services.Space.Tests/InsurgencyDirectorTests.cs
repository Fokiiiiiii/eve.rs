using System.Text.Json.Nodes;
using Eve.Content;
using Eve.Data;
using Eve.Dungeons;
using Eve.Events;
using Eve.Npc;
using Eve.Scheduler;
using Eve.Services.Space.Insurgency;
using Eve.Services.Space.Waves;
using Eve.Structures;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Ins = Eve.Insurgency;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The pirate insurgencies on the space runtime (slice 2): a campaign starts in each warzone, its sites start when it becomes active, a site scores its system once,
/// a kill in a covered system contributes, and a pirate win pays the contributors and ends the sites. The clock is the test's and the NPCs are the test's fake spawner's.
/// </summary>
public sealed class InsurgencyDirectorTests
{
    private const long Start = 1_700_000_000_000;
    private static readonly TimeSpan Forecast = TimeSpan.FromHours(48);
    private const long Contributor = 880_000_001;

    [PgFact]
    public async Task Each_warzone_with_systems_starts_one_campaign_in_forecasting_covering_its_hub_and_its_nearest_low_systems()
    {
        await using var rig = await InsurgencyRig.CreateAsync();
        await rig.RunDueAsync();

        var campaigns = await rig.CampaignsAsync();
        Assert.Equal(2, campaigns.Count);
        Assert.All(campaigns, campaign =>
        {
            Assert.Equal(Ins.CampaignStates.Forecasting, campaign.State);
            Assert.Equal(5, campaign.CoveredSolarSystemIds.Count);
            Assert.Contains(campaign.OriginSolarSystemId, campaign.CoveredSolarSystemIds);
            Assert.Equal(5, campaign.PiratePointsRequired);
        });
        Assert.Equal(2, campaigns.Select(c => c.WarzoneId).Distinct().Count());
        Assert.Empty(await rig.SitesAsync());
    }

    [PgFact]
    public async Task A_campaign_starts_its_sites_when_it_becomes_active_and_a_completed_site_scores_its_system_once()
    {
        await using var rig = await InsurgencyRig.CreateAsync();
        await rig.RunDueAsync();
        Assert.Empty(await rig.SitesAsync());

        rig.Advance(Forecast + TimeSpan.FromMinutes(1));
        await rig.RunDueAsync();
        var campaign = (await rig.CampaignsAsync()).First();
        Assert.Equal(Ins.CampaignStates.Active, campaign.State);
        var sites = await rig.SitesAsync();
        Assert.Equal(10, sites.Count);
        Assert.All(sites, site => Assert.Contains(site.Dungeon, InsurgencyTemplates()));

        var mine = sites.Where(site => site.CampaignId == campaign.CampaignId).ToList();
        Assert.Equal(5, mine.Count);
        await rig.CompleteAsync(mine[0].Instance);
        await rig.CompleteAsync(mine[0].Instance);
        var after = await rig.CampaignAsync(campaign.CampaignId);
        Assert.Equal(1, after.PiratePointsScored);
        Assert.Equal(Ins.CampaignStates.Active, after.State);
    }

    [PgFact]
    public async Task A_campaign_starts_with_its_fob_invulnerable_and_the_empire_side_at_its_count_exposes_it_and_destroying_it_ends_the_campaign()
    {
        await using var rig = await InsurgencyRig.CreateAsync();
        await rig.RunDueAsync();
        var campaign = (await rig.CampaignsAsync()).First();
        var fob = await rig.FobAsync(campaign.StructureId);
        Assert.Equal(StructureState.FobInvulnerable, fob.Snapshot.State);
        Assert.Equal(campaign.OriginSolarSystemId, fob.SolarSystemId);
        Assert.Equal(campaign.WarzoneId == Ins.InsurgencyWarzones.Angel.WarzoneId ? 78260 : 79172, fob.TypeId);

        rig.Advance(Forecast + TimeSpan.FromMinutes(1));
        await rig.RunDueAsync();
        for (var i = 0; i < 5; i++)
            await rig.ScoreAsync(campaign.CampaignId, Ins.Side.Antipirate, campaign.CoveredSolarSystemIds[i]);
        await rig.RunDueAsync();
        Assert.Equal(StructureState.ShieldVulnerable, (await rig.FobAsync(campaign.StructureId)).Snapshot.State);
        Assert.Equal(Ins.CampaignStates.Active, (await rig.CampaignAsync(campaign.CampaignId)).State);

        // the shield, then the one reinforcement (3 hours at the middle roll), the armor and the hull: the FOB is destroyed
        await rig.HitAsync(campaign.StructureId, new Damaged(StructureLayer.Shield, true, false));
        rig.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));
        await rig.RunDueAsync();
        await rig.HitAsync(campaign.StructureId, new Damaged(StructureLayer.Armor, true, false));
        await rig.HitAsync(campaign.StructureId, new Damaged(StructureLayer.Hull, true, false));

        Assert.True((await rig.FobAsync(campaign.StructureId)).Snapshot.DestroyedMs > 0);
        var ended = await rig.CampaignAsync(campaign.CampaignId);
        Assert.Equal(Ins.CampaignStates.AntipirateWin, ended.State);
        foreach (var site in (await rig.SitesAsync()).Where(site => site.CampaignId == campaign.CampaignId))
            Assert.Equal("abandoned", (await rig.Content.GetAsync(site.Instance))!.State);
    }

    [PgFact]
    public async Task A_pirate_win_pays_each_contributor_the_victory_points_and_ends_the_other_sites()
    {
        await using var rig = await InsurgencyRig.CreateAsync();
        await rig.RunDueAsync();
        rig.Advance(Forecast + TimeSpan.FromMinutes(1));
        await rig.RunDueAsync();
        var campaign = (await rig.CampaignsAsync()).First();
        var mine = (await rig.SitesAsync()).Where(site => site.CampaignId == campaign.CampaignId).ToList();

        // a final blow in a covered system while the campaign is active: the pilot contributes
        await rig.KillAsync(Contributor, campaign.CoveredSolarSystemIds[0]);
        foreach (var site in mine)
        {
            await rig.CompleteAsync(site.Instance);
        }

        var won = await rig.CampaignAsync(campaign.CampaignId);
        Assert.Equal(Ins.CampaignStates.PirateWin, won.State);
        Assert.Equal(5, won.PiratePointsScored);
        var balance = Assert.Single(await rig.Loyalty.ReadAsync(new LoyaltyHolder(LoyaltyHolderKind.Character, Contributor)));
        Assert.Equal(Ins.InsurgencyRules.VictoryLp, balance.Amount);
        Assert.Equal(InsurgencyWarzoneCorporation(campaign.WarzoneId), balance.IssuerCorpId);
        Assert.Empty(await rig.OpenSitesAsync(campaign.CampaignId));
        Assert.True((await rig.FobAsync(campaign.StructureId)).Snapshot.DestroyedMs > 0); // the FOB leaves space with the pirates' win
    }

    /// <summary>The campaign site templates of the owner catalog: archetypes 72 to 79 (the sites the start driver may use).</summary>
    private static IEnumerable<long> InsurgencyTemplates() =>
        DungeonCatalog.LoadEmbedded().Dungeons.Where(d => d.ArchetypeID is >= 72 and <= 79).Select(d => d.DungeonID).ToList();

    private static long InsurgencyWarzoneCorporation(long warzoneId) => warzoneId == Ins.InsurgencyWarzones.Angel.WarzoneId ? Ins.InsurgencyWarzones.Angel.PirateCorporationId : Ins.InsurgencyWarzones.Guristas.PirateCorporationId;

    /// <summary>Ten low-security systems of each warzone on the client's map (the first ten by id), linked as a chain: each warzone has the systems a campaign needs.</summary>
    private static readonly int[] Angels = [.. Ins.InsurgencyWarzones.SystemsOf(Ins.InsurgencyWarzones.Angel.WarzoneId).OrderBy(i => i).Take(10)];
    private static readonly int[] Gurista = [.. Ins.InsurgencyWarzones.SystemsOf(Ins.InsurgencyWarzones.Guristas.WarzoneId).OrderBy(i => i).Take(10)];

    private static SolarSystemCatalog Universe() => new(
        Angels.Select((id, i) => new SolarSystemInfo(id, $"Angel {i}", 1, 10000012, 0.3))
            .Concat(Gurista.Select((id, i) => new SolarSystemInfo(id, $"Guristas {i}", 2, 10000015, 0.2)))
            .Concat([new SolarSystemInfo(950_000, "Highsec", 3, 10000002, 0.9)]));

    private static StargateGraph Stargates()
    {
        static IEnumerable<(int From, int To)> Chain(int[] ids) => ids.Zip(ids.Skip(1), (from, to) => (from, to));
        return new StargateGraph(Chain(Angels).Concat(Chain(Gurista)));
    }

    private sealed class InsurgencyRig : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _source;
        private readonly string[] _schemas;
        private long _now = Start;

        private InsurgencyRig(NpgsqlDataSource source, string[] schemas)
        {
            _source = source;
            _schemas = schemas;
            Spawner = new FakeSpawner();
            Scheduler = new DurableScheduler(source, () => _now, null, schemas[0]);
            Bus = new EventBus(source, Scheduler, null, schemas[1]);
            Content = new ContentRuntime(source, Scheduler, Bus, schemas[2]);
            Waves = new DungeonWaveDirector(source, Content, Scheduler, Bus, DungeonCatalog.LoadEmbedded(), WaveRules.LoadEmbedded(),
                new NpcSelector(NpcAuthority.Load()), Spawner, schemas[3]);
            Store = new Ins.InsurgencyStore(source, schemas[4]);
            Loyalty = new LoyaltyPointStore(source, schemas[5]);
            Campaigns = new Ins.InsurgencyCampaigns(Store, Scheduler, Bus, Loyalty);
            StructureStore = new StructureStore(source, schemas[6]);
            Structures = new StructureTimers(StructureStore, Scheduler, Bus, new FobRules(), () => 0.5);
        }

        public FakeSpawner Spawner { get; }
        public StructureTimers Structures { get; private set; } = null!;
        public StructureStore StructureStore { get; private set; } = null!;
        public DurableScheduler Scheduler { get; private set; }
        public EventBus Bus { get; private set; }
        public ContentRuntime Content { get; private set; }
        public DungeonWaveDirector Waves { get; private set; }
        public Ins.InsurgencyStore Store { get; }
        public Ins.InsurgencyCampaigns Campaigns { get; }
        public LoyaltyPointStore Loyalty { get; }

        public static async Task<InsurgencyRig> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            var id = Guid.NewGuid().ToString("N")[..12];
            string[] schemas = ["ins_sch_" + id, "ins_evt_" + id, "ins_cnt_" + id, "ins_wav_" + id, "ins_camp_" + id, "ins_wal_" + id, "ins_str_" + id];
            await DurableScheduler.MigrateAsync(source, schemas[0]);
            await EventBus.MigrateAsync(source, schemas[1]);
            await ContentRuntime.MigrateAsync(source, schemas[2]);
            await DungeonWaveDirector.MigrateAsync(source, schemas[3]);
            await Ins.InsurgencyStore.MigrateAsync(source, schemas[4]);
            await PgWalletStore.MigrateAsync(source, schemas[5]);
            await StructureStore.MigrateAsync(source, schemas[6]);
            var rig = new InsurgencyRig(source, schemas);
            var catalog = DungeonCatalog.LoadEmbedded();
            await rig.Waves.AttachAsync();
            rig.Campaigns.Register();
            rig.Structures.Register();
            await new InsurgencyDirector(rig.Store, rig.Campaigns, rig.Scheduler, rig.Bus, rig.Content, rig.Waves, Universe(), Stargates(), catalog,
                rig.Structures, rig.StructureStore).AttachAsync();
            await rig.Content.AttachAsync();
            await rig.Bus.PumpAsync();
            return rig;
        }

        public void Advance(TimeSpan span) => _now += (long)span.TotalMilliseconds;

        /// <summary>Runs the due jobs and delivers what they publish, until nothing is left.</summary>
        public async Task RunDueAsync()
        {
            for (var round = 0; round < 10; round++)
            {
                var ran = await Scheduler.RunDueAsync();
                var delivered = 0;
                for (var i = 0; i < 20; i++)
                {
                    var pumped = (await Bus.PumpAsync()).Delivered;
                    if (pumped == 0)
                        break;
                    delivered += pumped;
                }
                if (ran == 0 && delivered == 0)
                    break;
            }
        }

        public async Task<StructureRecord> FobAsync(long structureId)
        {
            await using var connection = await _source.OpenConnectionAsync();
            return (await StructureStore.GetAsync(connection, null, structureId, false))!;
        }

        /// <summary>A point for a side, as the space scores it (the campaign's own transaction), then the announcements.</summary>
        public async Task ScoreAsync(long campaignId, Ins.Side side, long system)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Campaigns.ScorePointAsync(connection, transaction, campaignId, side, system);
            await transaction.CommitAsync();
            await RunDueAsync();
        }

        /// <summary>A hit on a FOB, as the space's damage notice applies it, then the timers and the announcements.</summary>
        public async Task HitAsync(long structureId, StructureEvent hit)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Structures.ApplyAsync(connection, transaction, structureId, hit);
            await transaction.CommitAsync();
            await RunDueAsync();
        }

        public async Task<IReadOnlyList<Ins.Campaign>> CampaignsAsync()
        {
            await using var connection = await _source.OpenConnectionAsync();
            return await Store.OpenAsync(connection, null);
        }

        public async Task<Ins.Campaign> CampaignAsync(long campaignId)
        {
            await using var connection = await _source.OpenConnectionAsync();
            return (await Store.GetAsync(connection, null, campaignId, false))!;
        }

        public async Task<IReadOnlyList<SiteRow>> SitesAsync()
        {
            var rows = new List<SiteRow>();
            await using var connection = await _source.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT campaign_id, solar_system_id, dungeon_id, instance_id FROM \"{_schemas[4]}\".insurgency_sites ORDER BY instance_id";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(new SiteRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
            return rows;
        }

        public async Task<IReadOnlyList<long>> OpenSitesAsync(long campaignId)
        {
            await using var connection = await _source.OpenConnectionAsync();
            return await Store.OpenSiteInstancesAsync(connection, null!, campaignId);
        }

        /// <summary>Completes a dungeon the way its waves would (each active objective gets the progress it still needs).</summary>
        public async Task CompleteAsync(long instanceId)
        {
            for (var round = 0; round < 20; round++)
            {
                var active = (await Content.ObjectivesOfAsync(instanceId)).Where(o => o.State == "active").ToList();
                if (active.Count == 0)
                    break;
                await using (var connection = await _source.OpenConnectionAsync())
                await using (var transaction = await connection.BeginTransactionAsync())
                {
                    foreach (var objective in active)
                        await Content.ProgressAsync(connection, transaction, instanceId, objective.Id, objective.Target - objective.Progress);
                    await transaction.CommitAsync();
                }
                await RunDueAsync();
            }
        }

        /// <summary>A pilot's final blow on an NPC in a system (the event the space publishes for every kill).</summary>
        public async Task KillAsync(long characterId, long solarSystemId)
        {
            await using var connection = await _source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Bus.PublishAsync(connection, transaction, "npc.killed",
                new { characterId, typeId = 1, solarSystemId, shipId = 0L }, $"system:{solarSystemId}");
            await transaction.CommitAsync();
            await RunDueAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var schema in _schemas)
            {
                try
                {
                    await using var drop = _source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                    await drop.ExecuteNonQueryAsync();
                }
                catch (NpgsqlException)
                {
                    // Leaving a scratch schema behind is better than failing the test.
                }
            }
            await _source.DisposeAsync();
        }
    }

    public sealed record SiteRow(long CampaignId, long SolarSystemId, long Dungeon, long Instance);

    /// <summary>Every structure is a pirate FOB here: the timeline's rules of a low-security FOB.</summary>
    private sealed class FobRules : IStructureRulesSource
    {
        public StructureRules RulesOf(int typeId, int solarSystemId) => StructureRules.Of(StructureSize.Fob, SecurityBand.LowSec, canBeAbandoned: false);
    }

    private sealed class FakeSpawner : IDungeonNpcSpawner
    {
        private long _next = 4_400_000_000_000_000;

        public Task<DungeonSpawnResult> SpawnAsync(int solarSystemId, double[] origin, NpcSelection selection, int amount, CancellationToken cancellationToken)
        {
            var ships = Enumerable.Range(0, amount).Select(_ => ++_next).ToList();
            return Task.FromResult(new DungeonSpawnResult(ships, null));
        }

        public bool IsPilotInSystem(long characterId, int solarSystemId) => false;

        public void Remove(IReadOnlyList<long> shipIds)
        {
        }
    }
}
