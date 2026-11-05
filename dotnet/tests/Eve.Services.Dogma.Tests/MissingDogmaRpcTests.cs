using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Dogma;
using Xunit;

namespace Eve.Services.Dogma.Tests;

public sealed class MissingDogmaRpcTests
{
    [Fact]
    public async Task GetRequiredSkillLevels_returns_sorted_skill_ids_and_highest_duplicate_level()
    {
        const int typeId = 900001;
        var type = new TypeDef(typeId, 1, 1, new Dictionary<int, double>
        {
            [182] = 3300, [277] = 2,
            [183] = 3300, [278] = 5,
            [1285] = 500, [1286] = 3,
        }, []);
        var service = new DogmaIMService(new DogmaStatic([], [], [type]), null!, () => 0, null!);

        var result = await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "GetRequiredSkillLevels",
            Args = [new PyInt(typeId)],
            Session = new ClientSession(),
        });

        var skills = Assert.IsType<PyDict>(result);
        Assert.Equal(2, skills.Entries.Count);
        Assert.Contains(skills.Entries, pair => pair.Key == new PyInt(500) && pair.Value == new PyInt(3));
        Assert.Contains(skills.Entries, pair => pair.Key == new PyInt(3300) && pair.Value == new PyInt(5));
        Assert.True(((IService)service).HasMethod("GetRequiredSkillLevels"));
    }

    [Fact]
    public async Task CreateNewbieShip_uses_the_session_callback_and_returns_none()
    {
        var session = new ClientSession();
        session.Set("charid", new PyInt(20));
        session.Set("stationid", new PyInt(600));
        var callbackCalls = 0;
        var service = new DogmaIMService(new DogmaStatic([], [], []), null!, () => 0, null!,
            createNewbieShip: (actualSession, _) =>
            {
                Assert.Same(session, actualSession);
                Assert.Equal(20, actualSession.CharacterId);
                Assert.Equal(600, actualSession.GetInt("stationid"));
                callbackCalls++;
                return ValueTask.CompletedTask;
            });

        var result = await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "CreateNewbieShip",
            Args = [new PyInt(999), new PyInt(888)],
            Session = session,
        });

        Assert.IsType<PyNone>(result);
        Assert.Equal(1, callbackCalls);
        Assert.True(((IService)service).HasMethod("CreateNewbieShip"));
    }
}
