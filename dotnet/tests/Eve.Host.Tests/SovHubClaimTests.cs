using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Host;

namespace Eve.Host.Tests;

/// <summary>
/// ST-9 d: the sovereignty claim of a captured neutral Sovereignty Hub (<see cref="PersistenceSovFwWriteOperations.ClaimCapturedHubAsync"/>), against an in-memory
/// sovereignty table: the system, its hub's row and the hub record take the capturer; a system another alliance holds is refused, and a repeat is answered.
/// </summary>
public sealed class SovHubClaimTests
{
    private const long SystemId = 30_100_032;
    private const long HubId = 1_040_000_000_904;
    private const long Corporation = 98_000_010;
    private const long Alliance = 5;
    private const long FileTime = 133_000_000_000_000_000;
    private const string Key = "30100032";

    private static JsonObject Table(long? owner) => new()
    {
        ["systems"] = new JsonObject
        {
            [Key] = new JsonObject
            {
                ["solarSystemID"] = SystemId,
                ["infrastructureHubID"] = HubId,
                ["allianceID"] = owner is { } o ? JsonValue.Create(o) : null,
                ["corporationID"] = owner is { } ? JsonValue.Create(99_000_001L) : null,
                ["structures"] = new JsonArray(new JsonObject
                {
                    ["itemID"] = HubId, ["typeID"] = 32_458, ["corporationID"] = null, ["ownerID"] = null, ["allianceID"] = null,
                }),
            },
        },
        ["hubs"] = new JsonObject
        {
            [HubId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["hubID"] = HubId, ["corporationID"] = null, ["ownerID"] = null, ["allianceID"] = null,
            },
        },
    };

    private static (PersistenceSovFwWriteOperations Writer, Func<JsonObject> Saved) WriterOver(JsonObject table)
    {
        var tables = new Dictionary<string, JsonObject> { ["sovereignty"] = table };
        JsonObject Read(string name) => (JsonObject)tables[name].DeepClone();
        WriteResult Write(string name, string? path, JsonNode? data)
        {
            if (path is not null || data is not JsonObject root)
                return new WriteResult(false, "unexpected path write");
            tables[name] = (JsonObject)root.DeepClone();
            return new WriteResult(true, null);
        }
        var writer = new PersistenceSovFwWriteOperations(
            Read, Write, (_, _) => Task.FromResult(1),
            (name, _) => Task.FromResult(Read(name)),
            (_, _, _) => ValueTask.FromResult(false),
            new SemaphoreSlim(1, 1),
            (_, _) => ValueTask.FromResult(false),
            (_, _) => ValueTask.FromResult(false));
        return (writer, () => tables["sovereignty"]);
    }

    [Fact]
    public async Task A_neutral_hub_captured_by_an_alliance_is_claimed_by_its_capturer_in_the_system_its_row_and_the_hub()
    {
        var (writer, saved) = WriterOver(Table(owner: null));

        Assert.True(await writer.ClaimCapturedHubAsync(SystemId, HubId, Corporation, Alliance, FileTime));

        var system = saved()["systems"]![Key]!;
        Assert.Equal(Alliance, system["allianceID"]!.GetValue<long>());
        Assert.Equal(Corporation, system["corporationID"]!.GetValue<long>());
        Assert.Equal(FileTime, system["claimTime"]!.GetValue<long>());
        var row = system["structures"]![0]!;
        Assert.Equal((Corporation, Alliance), (row["corporationID"]!.GetValue<long>(), row["allianceID"]!.GetValue<long>()));
        var hub = saved()["hubs"]![HubId.ToString(System.Globalization.CultureInfo.InvariantCulture)]!;
        Assert.Equal((Corporation, Alliance), (hub["corporationID"]!.GetValue<long>(), hub["allianceID"]!.GetValue<long>()));
    }

    [Fact]
    public async Task A_repeat_by_the_same_alliance_is_answered_as_done_and_a_system_another_alliance_holds_is_refused()
    {
        var (writer, saved) = WriterOver(Table(owner: null));
        Assert.True(await writer.ClaimCapturedHubAsync(SystemId, HubId, Corporation, Alliance, FileTime));
        Assert.True(await writer.ClaimCapturedHubAsync(SystemId, HubId, Corporation, Alliance, FileTime + 1));
        Assert.Equal(FileTime, saved()["systems"]![Key]!["claimTime"]!.GetValue<long>());

        var (other, otherSaved) = WriterOver(Table(owner: 77));
        Assert.False(await other.ClaimCapturedHubAsync(SystemId, HubId, Corporation, Alliance, FileTime));
        Assert.Equal(77, otherSaved()["systems"]![Key]!["allianceID"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_claim_of_a_system_whose_infrastructure_hub_is_another_item_is_refused()
    {
        var (writer, saved) = WriterOver(Table(owner: null));

        Assert.False(await writer.ClaimCapturedHubAsync(SystemId, HubId + 1, Corporation, Alliance, FileTime));
        Assert.Null(saved()["systems"]![Key]!["allianceID"]);
    }

    [Fact]
    public async Task A_captured_hub_is_restored_from_the_saved_sovereignty_table()
    {
        var (writer, saved) = WriterOver(Table(owner: null));
        Assert.True(await writer.ClaimCapturedHubAsync(SystemId, HubId, Corporation, Alliance, FileTime));

        Task<JsonObject> Read(string name, CancellationToken _) =>
            Task.FromResult(name == "sovereignty" ? saved() : new JsonObject());
        var readModel = new PersistenceSovFwReadModel(
            Read, (_, _) => Task.FromResult(new Eve.Relations.Affiliation(Corporation, Alliance, null)));
        var session = new Eve.Services.ClientSession();
        session.Set("charid", new Eve.Marshal.PyInt(90000001));
        session.Set("solarsystemid2", new Eve.Marshal.PyInt(SystemId));
        var snapshot = await readModel.ReadSnapshotAsync(new Eve.Services.ServiceCall
        {
            Method = "GetSystemSovereigntyInfo",
            Args = [],
            Session = session,
        });

        Assert.Equal(new global::Eve.Services.Space.SovHubBall(HubId, checked((int)SystemId), Corporation, Alliance),
            global::Eve.Services.Space.SpaceSovHubs.HubOf(snapshot, checked((int)SystemId)));
    }

    /// <summary>
    /// ST-17 a: a hub destroyed by its command node stage leaves the sovereignty records (EVE.js commitSovereigntyHubCampaignOutcome, attacker result): the system
    /// loses its owner and hub pointer, the hub's row and record leave. A repeat, or a system the destroyed hub no longer holds, is answered as done and changes nothing.
    /// </summary>
    [Fact]
    public async Task A_hub_destroyed_by_a_command_node_stage_leaves_the_system_and_a_repeat_changes_nothing()
    {
        var (writer, saved) = WriterOver(Table(owner: Alliance));

        Assert.True(await writer.DestroyLostHubAsync(SystemId, HubId, Alliance));
        var system = saved()["systems"]![Key]!;
        Assert.Null(system["allianceID"]);
        Assert.Null(system["infrastructureHubID"]);
        Assert.Null(system["corporationID"]);
        Assert.Empty(system["structures"]!.AsArray());
        Assert.False(saved()["hubs"]!.AsObject().ContainsKey(HubId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Assert.True(await writer.DestroyLostHubAsync(SystemId, HubId, Alliance), "a repeat is answered as done");
        Assert.Null(saved()["systems"]![Key]!["allianceID"]);
    }
}
