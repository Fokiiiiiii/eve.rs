using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

public sealed class SetShipPasswordHostTests
{
    [PgFact]
    public async Task SetShipPassword_uses_the_registered_ship_route_and_updates_the_active_ship_harmonic()
    {
        await using var rig = await Rig.CreateAsync();
        rig.UseShipInventoryService();
        var character = await rig.NewCharacterAsync("Password pilot", shipTypeId: 587);
        var session = await rig.SessionAsync(character);
        var bound = await rig.BindShipAsync(session);
        Assert.Same(PyNone.Instance, await rig.ShipCallAsync(session, "Undock", bound,
            new PyLong(session.GetInt("shipid")!.Value), PyBool.False));
        await rig.BindBeyonceAsync(session);
        bound = await rig.BindShipInSpaceAsync(session);
        var shipId = session.GetInt("shipid")!.Value;
        var characterId = session.CharacterId!.Value;
        var systemId = checked((int)session.GetInt("solarsystemid")!.Value);
        var persistedCharacter = await rig.Characters.GetAsync(characterId) ?? throw new InvalidOperationException("Test pilot is missing.");
        var towerType = (await rig.Statics.TypesInGroupsAsync(["Control Tower"]))
            .First(type => type.GroupId == 365);
        const long towerId = 987_654_321;
        const string salt = "0123456789abcdef0123456789abcdef";
        const string hash = "fcde9c0e47c5627c4c53aeb350f933859bf577396213ac5968ef3e1533c5b930";
        rig.Space.DeployedRecords = rig.Deployed;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Deployed.SaveAsync(connection, transaction, new DeployedRecord(
                towerId, "control_tower", systemId, towerType.Name ?? "Control Tower", towerType.TypeId, towerType.GroupId,
                [0, 0, 0], 10_000, characterId, checked((int)persistedCharacter.CorporationId), checked((int)(persistedCharacter.AllianceId ?? 0)),
                shipId, rig.Now + 86_400_000));
            await rig.Deployed.SetTowerStateAsync(connection, transaction, towerId, "online");
            Assert.True(await rig.Deployed.SetTowerConfigurationAsync(connection, transaction, towerId,
                $"{{\"passwordSalt\":\"{salt}\",\"passwordHash\":\"{hash}\"}}"));
            await transaction.CommitAsync();
        }

        Assert.Same(PyNone.Instance, await rig.RegisteredCallAsync(session, bound, "SetShipPassword", new PyStr("field-secret")));

        Assert.Equal(new SimSetSlimField(shipId, "harmonic", new SimSlimInt(towerId)),
            Assert.Single(rig.Sim.Heard.OfType<SimSetSlimField>()));
        // the simulation is told the same match the one harmonic write came from: the tower matches the password
        var sessionId = (ulong)session.SessionId;
        Assert.Equal(new SimShipForceFieldPassword(sessionId, towerId, systemId, true),
            Assert.Single(rig.Sim.Heard.OfType<SimShipForceFieldPassword>()));
        Assert.Same(PyNone.Instance, await rig.RegisteredCallAsync(session, bound, "SetShipPassword", new PyStr("not-the-tower-password")));
        Assert.Equal(new SimSetSlimField(shipId, "harmonic", new SimSlimInt(-1)), rig.Sim.Heard.OfType<SimSetSlimField>().Last());
        // one harmonic write per call (two calls, two writes), and the wrong password is told to the simulation as a refused match
        Assert.Equal(2, rig.Sim.Heard.OfType<SimSetSlimField>().Count(field => field.FieldName == "harmonic"));
        Assert.Equal(new SimShipForceFieldPassword(sessionId, towerId, systemId, false), rig.Sim.Heard.OfType<SimShipForceFieldPassword>().Last());
        Assert.Equal(2, rig.Sim.Heard.OfType<SimShipForceFieldPassword>().Count());
        var beforeInvalid = rig.Sim.Heard.OfType<SimSetSlimField>().Count();
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.RegisteredCallAsync(
            session, bound, "SetShipPassword", new PyStr(new string('x', 51))));

        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        Assert.Equal(beforeInvalid, rig.Sim.Heard.OfType<SimSetSlimField>().Count());
        Assert.Equal(2, rig.Sim.Heard.OfType<SimShipForceFieldPassword>().Count());
    }
}
