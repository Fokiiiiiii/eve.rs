using Eve.Data;
using Eve.Ships;
using Npgsql;

namespace Eve.Ships.Tests;

public class ShipConfigurationStoreTests
{
    [PgFact]
    public async Task Ship_configuration_defaults_false_and_patches_preserve_unchanged_settings()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Rig.Url!));
        var schema = $"ship_config_{Guid.NewGuid():N}";
        var store = new PgShipConfigurationStore(source, schema);
        await PgShipConfigurationStore.MigrateAsync(source, schema);

        try
        {
            Assert.Equal(ShipConfiguration.Default, await store.GetAsync(900001));
            await store.PatchAsync(900001, smbFleetAccess: true, smbCorpAccess: null, fleetHangarFleetAccess: false, fleetHangarCorpAccess: null);
            await store.PatchAsync(900001, smbFleetAccess: null, smbCorpAccess: true, fleetHangarFleetAccess: null, fleetHangarCorpAccess: true);
            Assert.Equal(new ShipConfiguration(true, true, false, true), await store.GetAsync(900001));
        }
        finally
        {
            await using var command = source.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
    }
}
