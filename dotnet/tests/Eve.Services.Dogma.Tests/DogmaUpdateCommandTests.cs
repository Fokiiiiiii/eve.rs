using Eve.Dogma;
using Eve.Services.Slash;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// <c>/dogmaupdate</c>: the reload reads the SDE again and the tables stay usable (the rig's SDE is the host's SDE). Without an SDE attached it says so.
/// </summary>
public class DogmaUpdateCommandTests
{
    [PgFact]
    public async Task Dogma_update_reloads_the_sde_tables_and_the_types_still_resolve()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Statics.EnsureTypesAsync([Rig.Cockroach]);
        var attributesBefore = rig.Statics.AttributeCount;
        var effectsBefore = rig.Statics.EffectCount;
        Assert.NotNull(rig.Statics.Type(Rig.Cockroach));

        var reply = await rig.SlashAsync(Rig.Session(null), "/dogmaupdate");

        Assert.Equal($"Dogma reloaded from the SDE: {attributesBefore} attributes, {effectsBefore} effects.", reply);
        Assert.NotNull(rig.Statics.Type(Rig.Cockroach));
        Assert.Equal(attributesBefore, rig.Statics.AttributeCount);
        Assert.NotNull(rig.Statics.Attribute(4));
    }

    [Fact]
    public async Task Dogma_update_without_an_sde_says_so()
    {
        var reply = await new DogmaUpdateCommands(new DogmaStatic([], [], [])).RunAsync(Rig.Session(null), CancellationToken.None);

        Assert.Equal("Dogma cannot be reloaded: the dogma tables were not loaded from an SDE.", reply);
    }
}
