using System.Text.Json.Nodes;
using Eve.Crimewatch;
using Eve.Host;
using Eve.Relations;

namespace Eve.Host.Tests;

public sealed class StructureWeaponTargetContextTests
{
    [Fact]
    public async Task Structure_war_authority_uses_the_owner_corporation_not_the_acl_controller_corporation()
    {
        const long ownerCorporationId = 98_000_002;
        const long controllerCorporationId = 98_000_001;
        const long targetCorporationId = 98_000_003;
        const long targetCharacterId = 20;
        long activeWarAttacker = 0;

        var readModel = new PersistenceWeaponTargetContextReadModel(
            (characterId, _) => Task.FromResult(new Affiliation(
                characterId == targetCharacterId ? targetCorporationId : controllerCorporationId,
                null,
                null)),
            (attackerId, _, _, _) =>
            {
                activeWarAttacker = attackerId;
                return Task.FromResult(true);
            },
            (_, _) => Task.FromResult(CombatState.Idle),
            (_, _) => Task.FromResult(0d),
            (_, _) => Task.FromResult(new JsonObject()),
            () => 1_700_000_000_000);

        var context = await readModel.ReadStructureAsync(ownerCorporationId, targetCharacterId, CancellationToken.None);

        Assert.Equal(ownerCorporationId, activeWarAttacker);
        Assert.True(context.AttackerIsPlayer);
        Assert.True(context.TargetIsPlayerProperty);
        Assert.True(context.ActiveWar);
        Assert.False(context.LegalFriendlyFire);
        Assert.False(context.ActiveLimitedEngagement);
    }
}
