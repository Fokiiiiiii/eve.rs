using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Space;
using Eve.Universe;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A cynosural field generator (Cynosural Field Generator I, group 658): a pilot lights it only as a fleet member, and never in high-security space
/// (EVE University wiki, Cynosural Field Generator). The rig's station is in Manifest (security 1.0), so both refusals are reached in the rig.
/// </summary>
public class CynoFieldHostTests
{
    private const int CynosuralFieldGeneratorI = 21096;
    private const int HighSlot = 27;

    private static async Task<(Rig Rig, ClientSession Session, long Module)> GeneratorAsync(bool inFleet)
    {
        var rig = await Rig.CreateAsync();
        rig.Space.IsInFleetAsync = (_, _) => Task.FromResult(inFleet);
        var (session, _, module) = await RepairHostTests.InSpaceWithAsync(rig, CynosuralFieldGeneratorI, HighSlot);
        return (rig, session, module);
    }

    [PgFact]
    public async Task A_pilot_outside_a_fleet_is_refused_to_light_a_cynosural_field()
    {
        var (rig, session, module) = await GeneratorAsync(inFleet: false);
        await using (rig)
        {
            var answer = await rig.Space.ActivateAsync(session, module, "cynosuralGeneration", 0, 1000, CancellationToken.None);
            Assert.Equal(ModuleResult.Refusal("CYNO_NOT_IN_FLEET", CynosuralFieldGeneratorI, "Cynosural Field Generator I"), answer);
            Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
        }
    }

    [PgFact]
    public async Task A_fleet_member_is_refused_a_cynosural_field_in_high_security_space()
    {
        var (rig, session, module) = await GeneratorAsync(inFleet: true);
        await using (rig)
        {
            var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.ActivateAsync(session, module, "cynosuralGeneration", 0, 1000, CancellationToken.None));
            var arguments = ((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items;
            Assert.Equal("CantInHighSecSpace", ((PyStr)((PyTuple)arguments[1]).Items[0]).Value);
            Assert.Empty(rig.Sim.Heard.OfType<SimActivate>());
        }
    }
}
