using Eve.Agents;
using Eve.Marshal;

namespace Eve.Services.Core;


public sealed record HeldMissionView(MissionStatic Mission, AgentMissionRecord Record, long? BriefingMessageId, FetchObjectiveView? Fetch = null,
    CourierObjectiveView? Courier = null, long LoyaltyPoints = 0, MissionDungeonLocation? DungeonLocation = null);

public sealed record MissionDungeonLocation(long SolarSystemId, double? X = null, double? Y = null, double? Z = null);



public sealed record CourierObjectiveView(int TypeId, long Quantity, double Volume, bool HasCargo, long PickupStationId, long DropoffStationId, long CorporationId);

/// <summary>

/// station and corporation (the drop-off, where the item is handed in).
/// </summary>
public sealed record FetchObjectiveView(int TypeId, long Quantity, double Volume, long HeldQuantity, long StationId, long CorporationId);

/// <summary>

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

    private static PyValue DungeonLocation(HeldMissionView view)
    {
        if (view.DungeonLocation is not { SolarSystemId: > 0 } location) return PyNone.Instance;
        var fields = new List<KeyValuePair<PyValue, PyValue>>
        {
            Field("typeID", new PyInt(5)),
            Field("locationID", new PyInt(location.SolarSystemId)),
            Field("shipTypeID", PyNone.Instance),
            Field("locationType", new PyStr("dungeon")),

            Field("solarsystemID", new PyInt(location.SolarSystemId)),
            Field("agentID", new PyInt(view.Record.AgentId)),
            Field("locationNumber", new PyInt(0)),
        };
        if (location.X is { } x && location.Y is { } y && location.Z is { } z)
            fields.Insert(1, Field("coords", new PyTuple([new PyReal(x), new PyReal(y), new PyReal(z)])));
        return new PyDict(fields);
    }

    public static PyDict Objective(HeldMissionView view)
    {
        var mission = view.Mission;
        var completed = view.Record.State == AgentMissionState.Completed;
        var dungeons = new List<PyValue>();
        if (mission.Kill?.DungeonId is { } dungeonId)
        {

            dungeons.Add(new PyDict(
            [
                Field("briefingMessage", IdOrNone(view.BriefingMessageId)),
                Field("dungeonID", new PyInt(dungeonId)),
                Field("location", DungeonLocation(view)),

                Field("objectiveCompleted", completed ? new PyInt(1) : PyNone.Instance),
                Field("ownerID", IdOrNone(mission.FactionId)),
                Field("optional", PyBool.False),
            ]));
        }
        var objectives = new List<PyValue>();
        if (view.Fetch is { } fetch)
        {

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
            Field("locations", view.DungeonLocation is { SolarSystemId: > 0 } location
                ? new PyList([new PyInt(location.SolarSystemId)])
                : new PyList([])),

            Field("importantStandings", new PyInt(0)),
            Field("missionExtra", PyNone.Instance),
            Field("contentID", PyNone.Instance),
            Field("agentGift", new PyList([])),
            Field("normalRewards", new PyList(rewards)),
            Field("bonusRewards", new PyList([])),
            Field("researchPoints", new PyInt(0)),
            Field("missionState", new PyInt((int)view.Record.State)),
            Field("loyaltyPoints", new PyInt(view.LoyaltyPoints)),
            Field("missionTitleID", IdOrNone(mission.NameId)),
        ]);
    }

    public static PyDict JournalInfo(HeldMissionView view) => new(
    [
        Field("missionNameID", IdOrNone(view.Mission.NameId)),
        Field("contentID", PyNone.Instance),
        Field("briefingTextID", IdOrNone(view.BriefingMessageId)),
        Field("missionImage", PyNone.Instance),
        Field("expirationTime", view.Record.ExpiresAt is { } expires ? new PyLong(expires.ToFileTime()) : PyNone.Instance),
        Field("missionState", new PyInt((int)view.Record.State)),
        Field("objectives", Objective(view)),
        Field("bookmarks", new PyList([])),
        Field("iconID", view.Courier is not null ? new PyInt(16) : PyNone.Instance),
    ]);

    public static PyDict Keywords(AgentMissionAgentData agent, HeldMissionView view)
    {
        var fields = new List<KeyValuePair<PyValue, PyValue>>
        {
            Field("missionName", new PyStr(view.Record.Content.Title.Length > 0 ? view.Record.Content.Title : view.Mission.NameEn)),
            Field("missionTypeLabel", new PyStr(agent.MissionTypeLabel)),
        };
        if (view.Courier is { } courier)
        {
            fields.Add(Field("location", new PyInt(courier.PickupStationId)));
            fields.Add(Field("objectiveDestinationID", new PyInt(courier.DropoffStationId)));
            fields.Add(Field("quantity", new PyInt(courier.Quantity)));
            fields.Add(Field("typeID", new PyInt(courier.TypeId)));
        }
        else if (view.Fetch is { } fetch)
        {
            fields.Add(Field("location", new PyInt(fetch.StationId)));
            fields.Add(Field("objectiveDestinationID", new PyInt(fetch.StationId)));
            fields.Add(Field("quantity", new PyInt(fetch.Quantity)));
            fields.Add(Field("typeID", new PyInt(fetch.TypeId)));
        }
        if (view.Mission.Reward is { RewardTypeId: > 0, RewardQuantity: > 0 } reward)
        {
            fields.Add(Field("rewardTypeID", new PyInt(reward.RewardTypeId!.Value)));
            fields.Add(Field("rewardQuantity", new PyInt(reward.RewardQuantity)));
        }
        return new PyDict(fields);
    }
}
