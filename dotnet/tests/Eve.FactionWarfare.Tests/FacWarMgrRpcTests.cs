using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.FactionWarfare.Tests;

public sealed class FacWarMgrRpcTests
{
    private const long Character = 42;
    private const long Corporation = 99;
    private const long Amarr = FwFactions.AmarrEmpire;
    private const long DirectorRole = 1;

    /// <summary>The service over a PostgreSQL store, with a character whose standing and direct enlistment the test sets.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public FwRig Rig { get; private init; } = null!;
        public FacWarMgrService Service { get; private set; } = null!;
        public double Standing { get; set; } = 0.5;
        public FwCharacterWarfareState? State { get; set; } = new(null, 0);
        public bool CorporationDirector { get; set; } = true;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { Rig = await FwRig.CreateAsync() };
            var callbacks = new FacWarMgrCallbacks(
                ReadCharacterAsync: (characterId, _) => ValueTask.FromResult(characterId == Character ? fixture.State : null),
                SaveCharacterAsync: (characterId, factionId, cooldown, _) =>
                {
                    if (characterId == Character)
                        fixture.State = new FwCharacterWarfareState(factionId, cooldown);
                    return ValueTask.FromResult(characterId == Character);
                },
                ReadStandingAsync: (_, _, _) => ValueTask.FromResult(fixture.Standing),
                NowMs: () => fixture.Rig.Now,
                CanActAsCorporationDirectorAsync: (_, _, _) => ValueTask.FromResult(fixture.CorporationDirector));
            fixture.Service = new FacWarMgrService(fixture.Rig.Service, callbacks);
            return fixture;
        }

        public async ValueTask DisposeAsync() => await Rig.DisposeAsync();
    }

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session,
    };

    private static ClientSession Pilot(long factionId = 0, long system = 0, long role = DirectorRole)
    {
        var session = new ClientSession();
        session.Set("charid", new PyInt(Character));
        session.Set("corpid", new PyInt(Corporation));
        session.Set("corprole", new PyInt(role));
        session.Set("warfactionid", new PyInt(factionId));
        session.Set("solarsystemid2", new PyInt(system));
        return session;
    }

    private static long FileTimeOf(long unixMs) => unixMs * 10_000 + 116_444_736_000_000_000L;

    [PgFact]
    public async Task Corporation_enlistment_needs_the_director_role_and_is_pending_until_the_downtime()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rig = fixture.Rig;
        var service = fixture.Service;

        await Assert.ThrowsAnyAsync<Exception>(() => service.CallAsync(Call("JoinFactionAsCorporation", Pilot(role: 0), new PyInt(Amarr))).AsTask());
        Assert.Null(await rig.Service.StatusAsync(FwStore.CorporationKind, Corporation, rig.Now));

        var director = Pilot();
        Assert.Same(PyNone.Instance, await service.CallAsync(Call("GetCorpFactionalWarStatus", director)));
        Assert.Same(PyNone.Instance, await service.CallAsync(Call("JoinFactionAsCorporation", director, new PyInt(Amarr))));
        Assert.Equal(
            Py.KeyVal(("status", new PyInt(FwService.JoiningStatus)), ("factionID", new PyInt(Amarr)), ("startDate", new PyLong(FileTimeOf(rig.Now)))),
            await service.CallAsync(Call("GetCorpFactionalWarStatus", director)));
        Assert.Equal(new PyList([]), await service.CallAsync(Call("GetFactionCorporations", director, new PyInt(Amarr))));

        rig.Now = FwRules.NextDowntimeMs(rig.Now);
        Assert.Equal(new PyInt(Amarr), await service.CallAsync(Call("GetCorporationWarFactionID", director, new PyInt(Corporation))));
        Assert.Equal(new PyList([new PyInt(Corporation)]), await service.CallAsync(Call("GetFactionCorporations", director, new PyInt(Amarr))));
        Assert.Equal(
            Py.KeyVal(("status", new PyInt(FwService.ActiveStatus)), ("factionID", new PyInt(Amarr)), ("startDate", new PyLong(FileTimeOf(rig.Now)))),
            await service.CallAsync(Call("GetCorpFactionalWarStatus", director)));

        fixture.CorporationDirector = false;
        await Assert.ThrowsAnyAsync<Exception>(() => service.CallAsync(Call("LeaveFactionAsCorporation", director)).AsTask());
        Assert.Equal(
            Py.KeyVal(("status", new PyInt(FwService.ActiveStatus)), ("factionID", new PyInt(Amarr)), ("startDate", new PyLong(FileTimeOf(rig.Now)))),
            await service.CallAsync(Call("GetCorpFactionalWarStatus", director)));
    }

    [PgFact]
    public async Task Character_enlistment_sets_the_session_faction_and_the_rank_follows_the_standing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service;
        var session = Pilot();
        Assert.Same(PyNone.Instance, await service.CallAsync(Call("JoinFactionAsCharacter", session, new PyInt(Amarr))));
        Assert.Equal(new PyInt(Amarr), session.Get("warfactionid"));
        Assert.Equal(new FwCharacterWarfareState(Amarr, 0), fixture.State);

        // The rank is the whole part of the standing toward the militia: 4.7 is rank 4, and 12 is capped at 9.
        fixture.Standing = 4.7;
        Assert.Equal(Py.KeyVal(("currentRank", new PyInt(4))), await service.CallAsync(Call("GetMyCharacterRankInfo", session)));
        fixture.Standing = 12;
        Assert.Equal(Py.KeyVal(("currentRank", new PyInt(9))), await service.CallAsync(Call("GetCharacterRankInfo", session, new PyInt(Character))));
        Assert.Same(PyNone.Instance, await service.CallAsync(Call("GetMyCharacterRankInfo", Pilot(factionId: 0))));
    }

    [PgFact]
    public async Task Donation_is_debited_and_refused_with_the_tax_now_when_the_quote_is_stale()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rig = fixture.Rig;
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, Amarr);
        await rig.CreditAsync(Character, FwFactions.MilitiaCorporationOf(Amarr), 1000);
        var system = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0];
        var session = Pilot(Amarr, system);

        // At the 83 percent hub tax (every system of warzone 1 is held) 100 LP enter the pool as 17 LP: a stale quote of 100 is refused.
        Assert.Equal(DonateOutcome.TaxChanged, (await rig.Service.DonateAsync(system, Character, Corporation, Amarr, 100, 100, rig.Now)).Outcome);
        Assert.Equal(1000, await rig.BalanceAsync(Character, FwFactions.MilitiaCorporationOf(Amarr)));

        Assert.Same(PyNone.Instance, await DonateAsync(fixture, session, 100, 17));
        Assert.Equal(new PyInt(17), await fixture.Service.CallAsync(Call("GetSolarSystemLPs", session, new PyInt(system))));
        Assert.Equal(900, await rig.BalanceAsync(Character, FwFactions.MilitiaCorporationOf(Amarr)));
    }

    [PgFact]
    public async Task Stats_calls_return_the_six_client_keys_for_the_pilot_and_the_corporation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Rig.Service.RecordActivityAsync(Character, Corporation, 2, 30, fixture.Rig.Now - 86_400_000L);
        var session = Pilot();
        PyDict Expected(long kills, long points) => new(
        [
            new KeyValuePair<PyValue, PyValue>(new PyStr("killsY"), new PyInt(kills)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("killsLW"), new PyInt(kills)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("killsTotal"), new PyInt(kills)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("vpY"), new PyInt(points)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("vpLW"), new PyInt(points)),
            new KeyValuePair<PyValue, PyValue>(new PyStr("vpTotal"), new PyInt(points)),
        ]);
        Assert.Equal(Expected(2, 30), await fixture.Service.CallAsync(Call("GetStats_Character", session)));
        Assert.Equal(Expected(2, 30), await fixture.Service.CallAsync(Call("GetStats_Corp", session)));
    }

    private static ValueTask<PyValue> DonateAsync(Fixture fixture, ClientSession session, long donated, long hub) =>
        fixture.Service.CallAsync(Call("DonateLPsToSolarSystem", session, new PyInt(donated), new PyInt(hub)));
}
