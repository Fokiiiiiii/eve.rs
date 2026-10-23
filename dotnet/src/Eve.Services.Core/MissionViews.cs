using Eve.Agents;
using Eve.Marshal;

namespace Eve.Services.Core;


public sealed record HeldMissionView(MissionStatic Mission, AgentMissionRecord Record, long? BriefingMessageId, FetchObjectiveView? Fetch = null,
    CourierObjectiveView? Courier = null, long LoyaltyPoints = 0);



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

    public static PyDict JournalInfo(HeldMissionView view)
    {
        var mission = view.Mission;
        PyValue expires = view.Record.ExpiresAt is { } expiry
            ? new PyLong(expiry.UtcDateTime.ToFileTimeUtc())
            : PyNone.Instance;
        return new PyDict(
        [
            Field("missionNameID", IdOrNone(mission.NameId)),
            Field("contentID", new PyInt(mission.MissionId)),
            Field("briefingTextID", IdOrNone(view.BriefingMessageId)),
            Field("missionImage", PyNone.Instance),
            Field("expirationTime", expires),
            Field("missionState", new PyInt((int)view.Record.State)),
            Field("objectives", Objective(view)),
            Field("bookmarks", new PyList([])),
            Field("iconID", view.Courier is null ? PyNone.Instance : new PyInt(16)),
        ]);
    }

    public static PyDict Keywords(AgentMissionAgentData agent, HeldMissionView view)
    {
        var fields = new List<KeyValuePair<PyValue, PyValue>>
        {
            Field("missionName", new PyStr(view.Record.Content.Title.Length > 0
                ? view.Record.Content.Title
                : view.Mission.NameEn)),
            Field("missionTypeLabel", new PyStr(view.Record.Content.MissionTypeLabel)),
        };
        if (view.Courier is { } courier)
        {
            fields.Add(Field("objectiveLocationID", new PyInt(courier.PickupStationId)));
            fields.Add(Field("objectiveDestinationID", new PyInt(courier.DropoffStationId)));
            fields.Add(Field("objectiveQuantity", new PyInt(courier.Quantity)));
            fields.Add(Field("objectiveTypeID", new PyInt(courier.TypeId)));
        }
        else if (view.Fetch is { } fetch)
        {
            fields.Add(Field("objectiveLocationID", new PyInt(fetch.StationId)));
            fields.Add(Field("objectiveQuantity", new PyInt(fetch.Quantity)));
            fields.Add(Field("objectiveTypeID", new PyInt(fetch.TypeId)));
        }
        if (view.Mission.Reward is { } reward && reward.RewardTypeId is { } rewardType && reward.RewardQuantity > 0)
        {
            fields.Add(Field("rewardTypeID", new PyInt(rewardType)));
            fields.Add(Field("rewardQuantity", new PyInt(reward.RewardQuantity)));
        }
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
                Field("location", new PyDict([Field("typeID", new PyInt(1531)), Field("solarsystemID", new PyInt(0)), Field("locationID", new PyInt(0))])),
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
            Field("locations", new PyList([])),
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
}
