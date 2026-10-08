using Eve.Data;
using Eve.Dogma;
using Eve.Services.Space.Effects;
using Eve.Sde;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The cynosural field types (<see cref="CynoFieldTypes"/>): the group and the radius come from the static data of each field type, not from the EveJS fallbacks
/// (group 2020 or 897, radius 250).
/// </summary>
public class CynoFieldTypesTests
{
    [PgFact]
    public async Task Each_field_type_has_the_group_and_radius_of_its_static_data()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        await statics.EnsureTypesAsync(CynoFieldTypes.FieldTypes);

        // Cynosural Field 21094 (group 2020), Covert Cynosural Field I 28650 (group 897), Industrial Cynosural Field 52696 (group 2020); radius 250 each
        Assert.Equal(3, CynoFieldTypes.FieldTypes.Count);
        Assert.Equal(new CynoFieldTypes.Field(21094, 2020, 250), CynoFieldTypes.Of(statics, 21094));
        Assert.Equal(new CynoFieldTypes.Field(28650, 897, 250), CynoFieldTypes.Of(statics, 28650));
        Assert.Equal(new CynoFieldTypes.Field(52696, 2020, 250), CynoFieldTypes.Of(statics, 52696));
    }

    [PgFact]
    public async Task A_type_the_static_data_does_not_have_is_not_given_a_fallback_field()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));

        Assert.Null(CynoFieldTypes.Of(statics, 999_999_999));
    }
}
