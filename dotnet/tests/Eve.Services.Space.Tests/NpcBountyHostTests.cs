using Eve.Macho;
using Eve.Marshal;
using Eve.Npc;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>An NPC that a pilot destroys pays the pilot its bounty: the client is told at once what was added and when it will be paid, and the wallet is credited when it comes due.</summary>
public class NpcBountyHostTests
{
    private static async Task<(Rig Rig, Eve.Characters.Created Character, ClientSession Session)> InSpaceAsync()
    {
        var rig = await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync();
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        return (rig, character, session);
    }

    private static SimDestroyed Destroyed(SimSpawn spawn, long killerShip) =>
        new(1UL << 62, spawn.Ship.ShipId, 4_300_000_000_000_001, [0, 0, 0], [1, 0, 0], 47, killerShip);

    private static async Task<SimSpawn> SpawnAsync(Rig rig, ClientSession session, string profile)
    {
        var report = await rig.Space.SpawnNpcsAsync(session, profile, 1, CancellationToken.None);
        Assert.Null(report.Error);
        return rig.Sim.Heard.OfType<SimSpawn>().Last();
    }

    [PgFact]
    public async Task A_pilot_who_destroys_an_npc_is_told_what_was_added_and_when_it_will_be_paid()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var spawn = await SpawnAsync(rig, session, "Blood Visionary");

        rig.Sim.Say(Destroyed(spawn, c.ShipId));
        // the notification follows the bucket's commit by a moment: wait for the notification itself, then read the bucket
        // (the wait reads without clearing: Events.Of clears by default, and the read below needs the notification)
        await Rig.UntilAsync(() => Events.Of(session, clear: false).Any(e => e.Name == "OnBountyAddedToPayout") && rig.Bounties.PendingAsync().Result.Count == 1);

        var told = Events.Of(session).Single(e => e.Name == "OnBountyAddedToPayout");
        Assert.Equal("charid", told.IdType);
        var fields = ((PyDict)((PyObject)told.Args.Single()).Args).Entries.ToDictionary(pair => JsSemantics.StringValue(pair.Key)!, pair => pair.Value);
        Assert.Equal(23245, Events.Number(fields["enemyTypeID"]));
        Assert.Equal(15_000, Events.Number(fields["amount"]));
        var bucket = Assert.Single(await rig.Bounties.PendingAsync());
        Assert.Equal(bucket.PayoutTime, Events.Number(fields["payoutTime"]));
        Assert.Equal(PyBool.False, fields["isModified"]);
        Assert.Equal((c.CharacterId, session.GetInt("solarsystemid")!.Value, 15_000m, 1), (bucket.CharacterId, bucket.SolarSystemId, bucket.Amount, bucket.Kills));
        Assert.Equal(new Dictionary<int, int> { [23245] = 1 }, bucket.NpcTypes);
        Assert.Equal([spawn.Ship.ShipId], bucket.SourceReferences);
    }

    [PgFact]
    public async Task What_is_due_is_paid_into_the_wallet_and_raised_for_whoever_announces_wallets()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var paid = new List<BountyPaid>();
        rig.Space.BountyPaidOut += paid.Add;
        var before = (await rig.Wallets.GetAsync(c.CharacterId))!.Isk;
        var spawn = await SpawnAsync(rig, session, "Blood Visionary");
        rig.Sim.Say(Destroyed(spawn, c.ShipId));
        await Rig.UntilAsync(() => rig.Bounties.PendingAsync().Result.Count == 1);

        Assert.Empty(await rig.Space.PayBountiesAsync());
        Assert.Equal(before, (await rig.Wallets.GetAsync(c.CharacterId))!.Isk);
        // the window is twenty minutes from the first kill
        rig.Now += NpcBounty.PayoutIntervalMs - 1;
        Assert.Empty(await rig.Space.PayBountiesAsync());
        rig.Now += 1;
        var payments = await rig.Space.PayBountiesAsync();

        Assert.Equal(before + 15_000m, Assert.Single(payments).Balance);
        Assert.Equal(payments, paid);
        Assert.Equal(before + 15_000m, (await rig.Wallets.GetAsync(c.CharacterId))!.Isk);
        Assert.Empty(await rig.Bounties.PendingAsync());
        var entry = Assert.Single(await rig.WalletTransactions.JournalAsync(c.CharacterId), e => e.RefTypeId == NpcBounty.PrizeRefType);
        Assert.Equal(15_000m, entry.Amount);
    }

    [PgFact]
    public async Task A_destruction_nobody_dealt_or_an_npc_dealt_pays_nothing_and_a_ship_the_host_did_not_make_is_not_an_npc()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var first = await SpawnAsync(rig, session, "Blood Visionary");
        var second = await SpawnAsync(rig, session, "Blood Visionary");

        rig.Sim.Say(Destroyed(first, 0));
        rig.Sim.Say(Destroyed(second, first.Ship.ShipId));
        // a ship that was never spawned for /npc
        rig.Sim.Say(new SimDestroyed(1UL << 62, 4_200_000_000_000_001, 0, [0, 0, 0], [1, 0, 0], 47, c.ShipId));
        await Task.Delay(300);

        Assert.Empty(await rig.Bounties.PendingAsync());
        Assert.DoesNotContain(Events.Of(session), e => e.Name == "OnBountyAddedToPayout");
    }

    [PgFact]
    public async Task One_destruction_told_twice_is_paid_once()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var spawn = await SpawnAsync(rig, session, "Blood Visionary");

        rig.Sim.Say(Destroyed(spawn, c.ShipId));
        rig.Sim.Say(Destroyed(spawn, c.ShipId));
        await Rig.UntilAsync(() => rig.Bounties.PendingAsync().Result.Count == 1);
        await Task.Delay(300);

        Assert.Equal(15_000m, Assert.Single(await rig.Bounties.PendingAsync()).Amount);
        Assert.Single(Events.Of(session), e => e.Name == "OnBountyAddedToPayout");
    }

    [PgFact]
    public async Task An_npc_that_is_worth_nothing_pays_nothing()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        // a profile the reference authored with no bounty (and a hull that can be hurt, which it spawns)
        var spawn = await SpawnAsync(rig, session, "parity_pith_defender");
        Assert.Equal(0, NpcAuthority.Load().Profile("parity_pith_defender")!.Bounty);

        rig.Sim.Say(Destroyed(spawn, c.ShipId));
        await Task.Delay(300);

        Assert.Empty(await rig.Bounties.PendingAsync());
    }

    [PgFact]
    public async Task Kills_of_several_npcs_in_one_window_are_one_bucket_and_each_is_told()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var first = await SpawnAsync(rig, session, "Blood Visionary");
        var second = await SpawnAsync(rig, session, "Blood Visionary");

        rig.Sim.Say(Destroyed(first, c.ShipId));
        await Rig.UntilAsync(() => rig.Bounties.PendingAsync().Result.Count == 1);
        rig.Sim.Say(Destroyed(second, c.ShipId));
        await Rig.UntilAsync(() => rig.Bounties.PendingAsync().Result.Single().Kills == 2);
        // the pilot is told of each kill after its bucket is written, so both notices are waited for
        await Rig.UntilAsync(() => Events.Of(session, clear: false).Count(e => e.Name == "OnBountyAddedToPayout") == 2);

        Assert.Equal(30_000m, Assert.Single(await rig.Bounties.PendingAsync()).Amount);
        Assert.Equal(2, Events.Of(session).Count(e => e.Name == "OnBountyAddedToPayout"));
    }
}
