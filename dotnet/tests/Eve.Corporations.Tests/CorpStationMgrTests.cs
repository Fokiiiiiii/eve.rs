using System.Text.Json;
using Eve.Corporations;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;

namespace Eve.Corporations.Tests;

/// <summary>The corporation door of an NPC station: the standing check a station service makes, and moving the headquarters.</summary>
public sealed class CorpStationMgrTests
{
    private const long StationId = 60000001;
    private const long OwnerId = 500026;
    private const int SolarSystemId = 30000142;
    private const long OtherStationId = 60000002;

    private static readonly Dictionary<long, double> Rules = new() { [5] = 3.0, [13] = -0.05 };

    [PgFact]
    public async Task The_standing_check_refuses_a_service_the_standing_is_below_and_admits_the_rest()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.CharacterAsync("Standing Pilot");
        var session = Session(character);
        var service = CreateService(rig, standing: 0.0);
        Assert.True(((IService)service).HasMethod("MachoResolveObject"));
        Assert.Equal(new PyInt(Eve.Macho.MachoMessage.ProxyNodeId), await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "MachoResolveObject", Args = [new PyLong(StationId)], Session = session,
        }));

        // service 5 needs 3.0: a standing of 0 is refused, with the reference's notice
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => Nested(service, session, "DoStandingCheckForStationService", new PyInt(5)));
        Assert.Equal("CustomNotify", refused.Error.UserErrorMessage);
        // service 13 needs -0.05: 0 is above it; a service with no rule has none
        Assert.Equal(PyNone.Instance, await Nested(service, session, "DoStandingCheckForStationService", new PyInt(13)));
        Assert.Equal(PyNone.Instance, await Nested(service, session, "DoStandingCheckForStationService", new PyInt(99)));

        var standing3 = CreateService(rig, standing: 3.0);
        Assert.Equal(PyNone.Instance, await Nested(standing3, session, "DoStandingCheckForStationService", new PyInt(5)));
    }

    [PgFact]
    public async Task The_standing_check_cannot_use_another_pilots_standing()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.CharacterAsync("Standing Pilot");
        var otherCharacter = await rig.CharacterAsync("Other Standing Pilot");
        var session = Session(character);
        var service = CreateService(rig, standing: 0.0, standingForCharacter: id => id == otherCharacter ? 3.0 : 0.0);

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            Nested(service, session, "DoStandingCheckForStationService", new PyInt(5), new PyLong(otherCharacter)));

        Assert.Equal("CustomNotify", refused.Error.UserErrorMessage);
        Assert.Equal(PyNone.Instance, await Nested(service, session, "DoStandingCheckForStationService", new PyInt(13), new PyLong(character)));
    }

    [PgFact]
    public async Task A_station_without_the_bind_is_refused_and_an_unknown_station_cannot_be_bound()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.CharacterAsync("Bind Pilot");
        var service = CreateService(rig, standing: 0.0);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "MachoBindObject", Args = [new PyLong(OtherStationId + 9)], Session = Session(character),
        }));
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task Releasing_a_station_moniker_removes_its_service_context()
    {
        await using var rig = await Rig.CreateAsync();
        var character = await rig.CharacterAsync("Release Pilot");
        var session = Session(character);
        IService service = CreateService(rig, standing: 10.0);
        var bind = Assert.IsType<PyTuple>(await service.CallAsync(new ServiceCall
        {
            Method = "MachoBindObject", Args = [new PyLong(StationId), PyNone.Instance], Session = session,
        }));
        var stream = Assert.IsType<PySubStream>(Assert.IsType<PySubStruct>(bind.Items[0]).Value);
        var oid = Assert.IsType<PyStr>(Assert.IsType<PyTuple>(stream.Decoded).Items[0]).Value;
        var registry = new ServiceRegistry();
        registry.Register(service);
        registry.RegisterBoundObject(oid, service);
        var call = new ServiceCall
        {
            Method = "DoStandingCheckForStationService", Args = [new PyInt(5)], Session = session, BoundObjectId = oid,
        };
        Assert.Equal(PyNone.Instance, await service.CallAsync(call));
        Assert.True(registry.UnregisterBoundObject(oid, session));
        Assert.Null(registry.Lookup(oid));
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(call));
        Assert.Equal("CustomNotify", refused.Error.UserErrorMessage);
        Assert.False(registry.UnregisterBoundObject(oid, session));
    }

    [PgFact]
    public async Task The_ceo_moves_the_headquarters_to_the_station_it_is_docked_in()
    {
        await using var rig = await Rig.CreateAsync();
        var (ceo, corporation) = await CreateCorporationAsync(rig);
        await SetHeadquartersAsync(rig, corporation.Id, OtherStationId);
        var service = CreateService(rig, standing: 0.0);
        var session = Session(ceo, stationId: StationId, corporationId: corporation.Id);

        Assert.Equal(PyNone.Instance, await Nested(service, session, "MoveCorpHQHere"));

        var moved = await rig.Store.GetAsync(corporation.Id);
        Assert.Equal(StationId, moved!.StationId);
        Assert.Equal(SolarSystemId, moved.SolarSystemId);
        // the same headquarters again changes nothing and is answered the same way
        Assert.Equal(PyNone.Instance, await Nested(service, session, "MoveCorpHQHere"));
    }

    [PgFact]
    public async Task A_bound_method_sent_as_a_byte_string_is_dispatched_as_the_method()
    {
        // the client sends the method name as ASCII text, which the decoder reads as PyBytes (LongString), not PyStr
        await using var rig = await Rig.CreateAsync();
        var (ceo, corporation) = await CreateCorporationAsync(rig);
        await SetHeadquartersAsync(rig, corporation.Id, OtherStationId);
        var service = CreateService(rig, standing: 0.0);

        var bind = await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "MachoBindObject",
            Args = [new PyLong(StationId), new PyTuple([new PyBytes("MoveCorpHQHere"u8.ToArray()), new PyTuple([]), PyNone.Instance])],
            Session = Session(ceo, stationId: StationId, corporationId: corporation.Id),
        });
        Assert.Equal(PyNone.Instance, Assert.IsType<PyTuple>(bind).Items[1]);
        Assert.Equal(StationId, (await rig.Store.GetAsync(corporation.Id))!.StationId);
    }

    [PgFact]
    public async Task A_headquarters_move_is_refused_outside_the_station_for_a_non_director_and_for_a_station_that_is_not_the_current_one()
    {
        await using var rig = await Rig.CreateAsync();
        var (ceo, corporation) = await CreateCorporationAsync(rig);
        await SetHeadquartersAsync(rig, corporation.Id, OtherStationId);
        var service = CreateService(rig, standing: 0.0);

        var notDocked = Session(ceo, stationId: null, corporationId: corporation.Id);
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => Nested(service, notDocked, "MoveCorpHQHere"));
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);

        var otherStation = Session(ceo, stationId: StationId, corporationId: corporation.Id);
        error = await Assert.ThrowsAsync<MachoWrappedException>(() => Nested(service, otherStation, "MoveCorpHQHere", new PyLong(OtherStationId)));
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);

        // the CEO's office is suspended and the member has no director role: no move
        await using (var roles = rig.Source.CreateCommand($"UPDATE {rig.CorporationSchema}.members SET roles=0 WHERE character_id=$1"))
        {
            roles.Parameters.Add(new Npgsql.NpgsqlParameter { Value = ceo });
            await roles.ExecuteNonQueryAsync();
        }
        await using (var suspended = rig.Source.CreateCommand($"UPDATE {rig.CorporationSchema}.corporations SET ceo_roles_suspended=true WHERE id=$1"))
        {
            suspended.Parameters.Add(new Npgsql.NpgsqlParameter { Value = corporation.Id });
            await suspended.ExecuteNonQueryAsync();
        }
        error = await Assert.ThrowsAsync<MachoWrappedException>(() => Nested(service, Session(ceo, StationId, corporation.Id), "MoveCorpHQHere"));
        Assert.Equal("CrpAccessDenied", error.Error.UserErrorMessage);
        Assert.Equal(OtherStationId, (await rig.Store.GetAsync(corporation.Id))!.StationId);
    }

    private static CorpStationMgrService CreateService(Rig rig, double standing, Func<long, double>? standingForCharacter = null) => new(
        rig.Store,
        null,
        (character, owner, token) => ValueTask.FromResult(standingForCharacter?.Invoke(character) ?? standing),
        id => id == StationId ? new CorporationStationPlace(StationId, OwnerId, SolarSystemId) : null,
        (owner, token) => ValueTask.FromResult<IReadOnlyDictionary<long, double>>(owner == OwnerId ? Rules : new Dictionary<long, double>()),
        () => 1_000L);

    /// <summary>The wire shape of a bound call: <c>MachoBindObject(stationID, (method, args, kwargs))</c>, answered as <c>(bound, result)</c>.</summary>
    private static async Task<PyValue> Nested(IService service, ClientSession session, string method, params PyValue[] args)
    {
        var bind = await service.CallAsync(new ServiceCall
        {
            Method = "MachoBindObject",
            Args = [new PyLong(StationId), new PyTuple([new PyStr(method), new PyTuple(args), PyNone.Instance])],
            Session = session,
        });
        return Assert.IsType<PyTuple>(bind).Items[1];
    }

    private static ClientSession Session(long characterId, long? stationId = null, long? corporationId = null)
    {
        var session = new ClientSession { CharacterId = characterId };
        if (stationId is { } station) session.Set("stationid", new PyInt(station));
        if (corporationId is { } corporation) session.Set("corpid", new PyInt(corporation));
        return session;
    }

    /// <summary>Founding puts the headquarters where the founder is; a test starts from another station.</summary>
    private static async Task SetHeadquartersAsync(Rig rig, long corporationId, long stationId)
    {
        await using var command = rig.Source.CreateCommand($"UPDATE {rig.CorporationSchema}.corporations SET station_id=$2, solar_system_id=0 WHERE id=$1");
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = corporationId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = stationId });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(long Ceo, Corporation Corporation)> CreateCorporationAsync(Rig rig)
    {
        var ceo = await rig.CharacterAsync("Door CEO");
        var corporation = await rig.Store.CreateAsync(ceo,
            new NewCorporation("Door Works", "DOORW", "Door test.", "https://example.invalid", 0.05m,
                Settings: JsonSerializer.SerializeToElement(new { shape1 = 1, color1 = 2 })),
            "station-door-founding");
        return (ceo, corporation);
    }
}
