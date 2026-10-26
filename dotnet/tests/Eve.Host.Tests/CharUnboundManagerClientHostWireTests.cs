using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Character_count_is_the_accounts_integer_count_over_registered_host_wire()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        _ = await rig.CreateCharacterAsync();
        _ = await rig.CreateCharacterAsync();
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.UserId = 1);
        var (front, open) = await OpenFrontAsync();

        Assert.Equal(new PyInt(2),
            await HostRpcAsync(front, open, 1, "charUnboundMgr", "GetNumCharacters"));
    }

    [PgFact]
    public async Task Character_delete_cancel_clears_the_queue_over_registered_host_wire()
    {
        await using var rig = await CharacterHostTests.CharacterHostRig.CreateAsync();
        var characterId = await rig.CreateCharacterAsync();
        var service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        var session = new ClientSession { UserId = 1, CharacterId = characterId };

        _ = await service.CallAsync(new ServiceCall
        {
            Method = "PrepareCharacterForDelete",
            Args = [new PyLong(characterId)],
            Session = session,
        });
        Assert.True((await rig.Characters.GetAsync(characterId))!.IsQueuedForDeletion);

        await rig.RestartAsync();
        service = Assert.IsAssignableFrom<IService>(rig.Registry.Lookup("charUnboundMgr"));
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: openedSession =>
            {
                openedSession.UserId = 1;
                openedSession.CharacterId = characterId;
            });
        var (front, open) = await OpenFrontAsync();

        Assert.Same(PyNone.Instance, await HostRpcAsync(front, open, 1,
            "charUnboundMgr", "CancelCharacterDeletePrepare", new PyLong(characterId)));
        Assert.False((await rig.Characters.GetAsync(characterId))!.IsQueuedForDeletion);
    }
}
