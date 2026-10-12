using Eve.Agents;
using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>A mission the character holds with an agent: the official record, the held state and the briefing text ID (null when not loaded).</summary>
public sealed record HeldMissionView(MissionStatic Mission, AgentMissionRecord Record, long? BriefingMessageId, FetchObjectiveView? Fetch = null,
    CourierObjectiveView? Courier = null);

/// <summary>The cargo of a courier mission: the item (SDE type, quantity and volume of one unit), whether the pilot holds it, the pickup (the agent's
/// station), the drop-off station and the agent's corporation (the owner of the stations' tuples, as the fetch objective's).</summary>
public sealed record CourierObjectiveView(int TypeId, long Quantity, double Volume, bool HasCargo, long PickupStationId, long DropoffStationId, long CorporationId);

/// <summary>
/// The objective item of a fetch step: the type and quantity from the SDE, the item's volume of one unit, the quantity the pilot holds, and the agent's
/// station and corporation (the drop-off, where the item is handed in).
/// </summary>
public sealed record FetchObjectiveView(int TypeId, long Quantity, double Volume, long HeldQuantity, long StationId, long CorporationId);

/// <summary>
/// The briefing and objective answers of a held mission (EveJS <c>buildMissionBriefingInfo</c>, <c>buildMissionObjectivePayload</c>), for the kill
/// missions: the dungeon objective, the fetch objective of an item and the ISK reward. The courier objective is not built yet.
/// </summary>
public static class MissionViews
{
    private static KeyValuePair<PyValue, PyValue> Field(string name, PyValue value) => new(new PyStr(name), value);

    private static PyValue IdOrNone(long? value) => value is > 0 ? new PyInt(value.Value) : PyNone.Instance;

    public static PyDict Briefing(HeldMissionView view) => new(
    [
        Field("Mission Keywords", new PyDict([])),
        Field("Mission Title ID", IdOrNone(view.Mission.NameId)),
        Field("ContentID", PyNone.Instance),
        Field("Mission Image", PyNone.Instance),
        Field("Decline Time", PyNone.Instance),
        Field("AcceptTimestamp", PyNone.Instance),
        Field("Expiration Time", PyNone.Instance),
        Field("Mission Briefing ID", IdOrNone(view.BriefingMessageId)),
    ]);

    public static PyDict Objective(HeldMissionView view)
    {
        var mission = view.Mission;
        var completed = view.Record.State == AgentMissionState.Completed;
        var dungeons = new List<PyValue>();
        if (mission.Kill?.DungeonId is { } dungeonId)
        {
            // ownerID is the mission's faction when the record has one: the EveJS enemy-owner rule is not ported (shortcut)
            dungeons.Add(new PyDict(
            [
                Field("briefingMessage", IdOrNone(view.BriefingMessageId)),
                Field("dungeonID", new PyInt(dungeonId)),
                Field("location", new PyDict([Field("typeID", new PyInt(1531)), Field("solarsystemID", new PyInt(0)), Field("locationID", new PyInt(0))])),
                Field("objectiveCompleted", completed ? new PyInt(1) : PyNone.Instance),
                Field("ownerID", IdOrNone(mission.FactionId)),
                Field("optional", PyBool.False),
            ]));
        }
        var objectives = new List<PyValue>();
        if (view.Fetch is { } fetch)
        {
            // the client's fetch objective (eve-logic evemissions/client/mission.py): the owner, the drop-off location and the cargo
            // shortcut: solarsystemID is 0 (the client reads only locationID of a fetch's drop-off)
            objectives.Add(new PyTuple([new PyStr("fetch"), new PyTuple(
            [
                new PyInt(fetch.CorporationId),
                new PyDict([Field("typeID", new PyInt(1531)), Field("solarsystemID", new PyInt(0)), Field("locationID", new PyInt(fetch.StationId))]),
                new PyDict(
                [
                    Field("volume", new PyReal(fetch.Volume * fetch.Quantity)),
                    Field("typeID", new PyInt(fetch.TypeId)),
                    Field("hasCargo", fetch.HeldQuantity >= fetch.Quantity ? PyBool.True : PyBool.False),
                    Field("quantity", new PyInt(fetch.Quantity)),
                ]),
            ])]));
        }
        if (view.Courier is { } courier)
        {
            // the client's transport objective (eve-logic evemissions/client/mission.py): pickup, drop-off and cargo, each location with its owner
            objectives.Add(new PyTuple([new PyStr("transport"), new PyTuple(
            [
                new PyInt(courier.CorporationId),
                new PyDict([Field("typeID", new PyInt(1531)), Field("solarsystemID", new PyInt(0)), Field("locationID", new PyInt(courier.PickupStationId))]),
                new PyInt(courier.CorporationId),
                new PyDict([Field("typeID", new PyInt(1531)), Field("solarsystemID", new PyInt(0)), Field("locationID", new PyInt(courier.DropoffStationId))]),
                new PyDict(
                [
                    Field("volume", new PyReal(courier.Volume * courier.Quantity)),
                    Field("typeID", new PyInt(courier.TypeId)),
                    Field("hasCargo", courier.HasCargo ? PyBool.True : PyBool.False),
                    Field("quantity", new PyInt(courier.Quantity)),
                ]),
            ])]));
        }
        var rewards = new List<PyValue>();
        if (mission.Reward?.RewardTypeId == 29 && mission.Reward.RewardQuantity > 0)
            rewards.Add(new PyTuple([new PyInt(29), new PyInt(mission.Reward.RewardQuantity), PyNone.Instance]));
        return new PyDict(
        [
            Field("completionStatus", new PyInt(completed ? 1 : 0)),
            Field("collateral", new PyList([])),
            Field("dungeons", new PyList(dungeons)),
            Field("objectives", new PyList(objectives)),
            Field("locations", new PyList([])),
            Field("importantStandings", new PyInt(0)),
            Field("missionExtra", PyNone.Instance),
            Field("contentID", PyNone.Instance),
            Field("agentGift", new PyList([])),
            Field("normalRewards", new PyList(rewards)),
            Field("bonusRewards", new PyList([])),
            Field("researchPoints", new PyInt(0)),
            Field("missionState", new PyInt((int)view.Record.State)),
            Field("loyaltyPoints", new PyInt(0)),
            Field("missionTitleID", IdOrNone(mission.NameId)),
        ]);
    }
}
