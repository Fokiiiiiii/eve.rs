using Eve.Data;
using Eve.Dogma;
using Eve.Sde;
using Eve.Services.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

public sealed class StructureDamageModuleDataTests
{
    [PgFact]
    public async Task Structure_weapons_use_module_category_and_not_the_structure_hull_category()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        await statics.EnsureTypesAsync([35926, 35928, 47068, 47069, 35832, 40634]);
        foreach (var typeId in new[] { 35926, 35928, 47068, 47069 })
            Assert.True(SpaceSessions.IsStructureDamageModuleType(statics, statics.Type(typeId)!));
        Assert.False(SpaceSessions.IsStructureDamageModuleType(statics, statics.Type(35832)!));
        Assert.False(SpaceSessions.IsStructureDamageModuleType(statics, statics.Type(40634)!));
        Assert.False(statics.Type(47068)!.Published);
        Assert.True(statics.Type(47069)!.Published);
        Assert.True(statics.Type(40634)!.Published);
    }
}
