using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Host;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// E-03: a system runs its jammer only with its Cynosural Suppression upgrade online (EveJS isCynoSuppressionUpgradeOnline), read from the sovereignty table
/// the host holds. The read model is built on the table reader alone, so these checks need no PostgreSQL.
/// </summary>
public sealed class StructureCynoSuppressionTests
{
    private const long Jita = 30_000_142, Hub = 1_000_200_001;
    private const int CynoSuppression = 81619, CynoNavigation = 81615;

    private static PersistenceSovFwReadModel Model(JsonObject sovereignty) => new(
        (name, _) => Task.FromResult(name == "sovereignty" ? sovereignty : new JsonObject()),
        (_, _) => Task.FromException<Eve.Relations.Affiliation>(new InvalidOperationException("affiliation is not read here")));

    /// <summary>
    /// A skyhook of Jita whose production is active (a workforce of 5 000, enough for the upgrade's 4 500): the supply the derived online state needs.
    /// </summary>
    private static JsonObject Skyhook(bool active = true, bool destroyed = false) => new()
    {
        ["1"] = new JsonObject
        {
            ["skyhookID"] = JsonValue.Create(1L), ["typeID"] = JsonValue.Create(81080L), ["solarSystemID"] = JsonValue.Create(Jita),
            ["planetID"] = JsonValue.Create(40_000_001L), ["corporationID"] = JsonValue.Create(98_000_010L),
            ["productionActive"] = JsonValue.Create(active), ["workforce"] = JsonValue.Create(5_000L), ["destroyed"] = JsonValue.Create(destroyed),
        },
    };

    /// <summary>Jita with the hub (0 for none) and the upgrades it holds, as the sovereignty table stores them, and an active skyhook.</summary>
    private static JsonObject Sovereignty(long hubId, params (int TypeId, bool Online)[] upgrades) => new()
    {
        ["skyhooks"] = Skyhook(),
        ["systems"] = new JsonObject
        {
            [Jita.ToString(CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["solarSystemID"] = JsonValue.Create(Jita),
                ["infrastructureHubID"] = hubId > 0 ? JsonValue.Create(hubId) : null,
                ["hubUpgradeConfiguration"] = new JsonArray(upgrades.Select((upgrade, index) => (JsonNode)new JsonObject
                {
                    ["itemID"] = JsonValue.Create(index + 1L),
                    ["typeID"] = JsonValue.Create(upgrade.TypeId),
                    ["online"] = JsonValue.Create(upgrade.Online),
                    ["priority"] = JsonValue.Create(index),
                }).ToArray()),
            },
        },
    };

    [Fact]
    public async Task An_online_cynosural_suppression_upgrade_at_the_hub_of_the_system_is_on()
    {
        Assert.True(await Model(Sovereignty(Hub, (CynoSuppression, true))).IsCynoSuppressionOnlineAsync(Jita, CancellationToken.None));
    }

    [Fact]
    public async Task An_installed_but_offline_suppression_upgrade_is_off()
    {
        Assert.False(await Model(Sovereignty(Hub, (CynoSuppression, false))).IsCynoSuppressionOnlineAsync(Jita, CancellationToken.None));
    }

    [Fact]
    public async Task An_online_suppression_upgrade_without_a_hub_is_off()
    {
        Assert.False(await Model(Sovereignty(0, (CynoSuppression, true))).IsCynoSuppressionOnlineAsync(Jita, CancellationToken.None));
    }

    [Fact]
    public async Task Another_online_upgrade_is_not_the_suppression_upgrade()
    {
        Assert.False(await Model(Sovereignty(Hub, (CynoNavigation, true))).IsCynoSuppressionOnlineAsync(Jita, CancellationToken.None));
    }

    [Fact]
    public async Task A_system_without_a_sovereignty_row_or_a_zero_id_is_off()
    {
        Assert.False(await Model(Sovereignty(Hub, (CynoSuppression, true))).IsCynoSuppressionOnlineAsync(Jita + 1, CancellationToken.None));
        Assert.False(await Model(new JsonObject { ["systems"] = new JsonObject() }).IsCynoSuppressionOnlineAsync(Jita, CancellationToken.None));
        Assert.False(await Model(Sovereignty(Hub, (CynoSuppression, true))).IsCynoSuppressionOnlineAsync(0, CancellationToken.None));
    }

    /// <summary>ST-17 c: without the skyhook's workforce the upgrade needs more than the system supplies, so it is LOW and the jammer is off (EVE.js budget).</summary>
    [Fact]
    public async Task Without_an_active_skyhook_the_upgrade_is_low_and_the_jammer_is_off()
    {
        var withoutSkyhook = Sovereignty(Hub, (CynoSuppression, true));
        withoutSkyhook.Remove("skyhooks");
        Assert.False(await Model(withoutSkyhook).IsCynoSuppressionOnlineAsync(Jita, CancellationToken.None));

        var inactive = Sovereignty(Hub, (CynoSuppression, true));
        inactive["skyhooks"] = Skyhook(active: false);
        Assert.False(await Model(inactive).IsCynoSuppressionOnlineAsync(Jita, CancellationToken.None));

        var destroyed = Sovereignty(Hub, (CynoSuppression, true));
        destroyed["skyhooks"] = Skyhook(destroyed: true);
        Assert.False(await Model(destroyed).IsCynoSuppressionOnlineAsync(Jita, CancellationToken.None));
    }

    /// <summary>
    /// ST-17 c: the upgrades take the power and the workforce in install order (EVE.js). Supply: 500 power, 5 000 workforce. Type ids modulo 4 give EVE.js's
    /// placeholders: 81616 needs 100 power, the cyno suppression 250 (the wiki); 81623 and 81627 need 400 power each.
    /// </summary>
    [Fact]
    public async Task The_install_order_decides_which_upgrade_gets_the_power_and_the_workforce()
    {
        // 100 power first, then 250 of the 400 left: online
        Assert.True(await Model(Sovereignty(Hub, (81616, true), (CynoSuppression, true))).IsCynoSuppressionOnlineAsync(Jita, CancellationToken.None));
        // two 400-power upgrades first: the second is LOW, and 100 power is left for the 250 of the cyno suppression: LOW
        Assert.False(await Model(Sovereignty(Hub, (81623, true), (81627, true), (CynoSuppression, true))).IsCynoSuppressionOnlineAsync(Jita, CancellationToken.None));
        // the cyno suppression installed first gets its 250; the two 400-power upgrades after it then fit neither
        Assert.True(await Model(Sovereignty(Hub, (CynoSuppression, true), (81623, true), (81627, true))).IsCynoSuppressionOnlineAsync(Jita, CancellationToken.None));
    }
}
