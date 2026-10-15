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

    /// <summary>Jita with the hub (0 for none) and the upgrades it holds, as the sovereignty table stores them.</summary>
    private static JsonObject Sovereignty(long hubId, params (int TypeId, bool Online)[] upgrades) => new()
    {
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
}
