using Eve.Characters;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Universe;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Population_cap_load_slots_keep_the_system_to_slot_key_dictionary_shape()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Timeout=1");
        var service = new PopulationCapService(
            new PgCharacterStore(source),
            new SystemPopulation(new CharacterSessions(), new SolarSystemCatalog([])));

        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.UserId = null,
            strictRpc: true);
        var (front, open) = await OpenFrontAsync();

        var slots = Assert.IsType<PyDict>(
            await HostRpcAsync(front, open, 1, "populationCap", "GetCharacterLoadSlot", new PyLong(42)));

        Assert.Empty(slots.Entries);
    }
}
