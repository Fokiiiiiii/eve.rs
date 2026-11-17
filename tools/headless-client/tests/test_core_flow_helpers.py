"""Regression checks for the captured core-flow comparison helpers."""

import copy

from evecli import observe
from evecli.describe import describe
from tests.test_jump import JUMP, compare_bound_events, milestones
from tests.test_undock import actions_of, compare_add_balls, compare_events, compare_state, keyval, number, online_modules_wire


def test_timed_events_uses_packet_arrival_time(monkeypatch):
    ticks = iter([100.0, 100.1])
    monkeypatch.setattr(observe.time, "monotonic", lambda: next(ticks))
    monkeypatch.setattr(
        observe,
        "events_of",
        lambda client, *, include_received_at: [
            {"name": "$sessionchange", "_received_at": 100.184}
        ],
    )

    events = observe.timed_events(object(), 1.0, until=lambda seen: bool(seen))

    assert events == [{"name": "$sessionchange", "at": 0.18}]


def test_jump_fixture_checks_sentries_when_station_props_share_the_add_balls_packet():
    race = JUMP["races"][0]
    steps = {step["label"]: step for step in race["steps"]}
    mapping = {
        race["characterID"]: race["characterID"],
        race["shipID"]: race["shipID"],
    }

    assert compare_events(steps["undock"]["events"], steps["undock"]["events"], "same undock", mapping) == []
    assert len(mapping["_sentry_slims"]) == 8
    assert compare_events(
        steps["bind the ballpark"]["events"],
        steps["bind the ballpark"]["events"],
        "same ballpark bind",
        mapping,
    ) == []
    assert compare_bound_events(
        steps["bind the ballpark of the next system"]["events"],
        steps["bind the ballpark of the next system"]["events"],
        race["shipID"],
        mapping,
        "same next-system bind",
    ) == []


def test_current_reference_warp_fixture_has_dropout_without_stop_milestone():
    warp = next(step for step in JUMP["races"][0]["steps"] if step["label"] == "warp to the stargate")
    marks = milestones(warp["events"])

    assert "dropout" in marks
    assert "rest" not in marks


def test_undock_online_modules_uses_the_clients_flag_to_long_id_map():
    described = describe(online_modules_wire([(27, 9_988_400_000_114), (28, 9_988_400_000_115)]))

    assert described == {
        "$dict": [[27, {"$long": 9_988_400_000_114}], [28, {"$long": 9_988_400_000_115}]],
    }


def test_add_balls_comparison_checks_the_complete_sentry_slim_row():
    race = JUMP["races"][0]
    undock = next(step for step in race["steps"] if step["label"] == "undock")
    event = next(event for event in undock["events"] if event["name"] == "DoDestinyUpdate")
    action = next(action for action in actions_of(event)[0] if action[1] == "AddBalls2")
    expected = action[2]
    actual = copy.deepcopy(expected)
    entries = actual["$tuple"][0]["$tuple"][1]
    sentry = next(
        entry
        for entry in entries
        if dict(entry["$dict"]).get("groupID") == 99
    )
    for pair in sentry["$dict"]:
        if pair[0] == "friendly_response_threshold":
            pair[1] += 1
            break
    else:
        raise AssertionError("captured sentry slim has no friendly_response_threshold field")

    differences = compare_add_balls(expected, actual, "mutated sentry", {})

    assert any("friendly_response_threshold" in difference for difference in differences), differences

    graphic_fallback = copy.deepcopy(expected)
    fallback_entries = graphic_fallback["$tuple"][0]["$tuple"][1]
    fallback_sentry = next(entry for entry in fallback_entries if dict(entry["$dict"]).get("groupID") == 99)
    fallback_sentry["$dict"] = [pair for pair in fallback_sentry["$dict"] if pair[0] != "graphicID"]

    fallback_differences = compare_add_balls(expected, graphic_fallback, "sentry graphic fallback", {})

    assert fallback_differences == [], fallback_differences

    intact_active_sentry = copy.deepcopy(expected)
    intact_entries = intact_active_sentry["$tuple"][0]["$tuple"][1]
    sentry_index = next(index for index, entry in enumerate(intact_entries) if dict(entry["$dict"]).get("groupID") == 99)
    sentry = intact_entries[sentry_index]
    intact_entries[sentry_index] = {
        "$tuple": [sentry, {"$tuple": [{"$tuple": [1.0, 100000.0, {"$long": 123}]}, 1.0, 1.0]}]
    }
    intact_differences = compare_add_balls(expected, intact_active_sentry, "intact active sentry", {})
    assert intact_differences == [], intact_differences

    damaged_active_sentry = copy.deepcopy(intact_active_sentry)
    damage_state = damaged_active_sentry["$tuple"][0]["$tuple"][1][sentry_index]["$tuple"][1]
    damage_state["$tuple"][0]["$tuple"][0] = 0.9
    damage_differences = compare_add_balls(expected, damaged_active_sentry, "damaged active sentry", {})
    assert any("damageState" in difference for difference in damage_differences), damage_differences

    rotation_mutation = copy.deepcopy(expected)
    rotation_entries = rotation_mutation["$tuple"][0]["$tuple"][1]
    rotation_sentry = next(entry for entry in rotation_entries if dict(entry["$dict"]).get("groupID") == 99)
    rotation = next(pair[1] for pair in rotation_sentry["$dict"] if pair[0] == "dunRotation")
    rotation["$tuple"][0] += 0.1

    rotation_differences = compare_add_balls(expected, rotation_mutation, "mutated sentry rotation", {})

    assert any("dunRotation" in difference for difference in rotation_differences), rotation_differences

    expected_with_damage = copy.deepcopy(expected)
    damage_entries = expected_with_damage["$tuple"][0]["$tuple"][1]
    sentry_index = None
    for index, entry in enumerate(damage_entries):
        if dict(entry["$dict"]).get("groupID") == 99:
            damage_entries[index] = {
                "$tuple": [entry, {"$tuple": [{"$tuple": [1.0, 0.0, {"$long": 123}]}, 1.0, 1.0]}]
            }
            sentry_index = index
            break
    if sentry_index is None:
        raise AssertionError("captured AddBalls2 has no sentry slim")
    actual_damage = copy.deepcopy(expected_with_damage)
    actual_damage["$tuple"][0]["$tuple"][1][sentry_index]["$tuple"][1] = None

    damage_differences = compare_add_balls(expected_with_damage, actual_damage, "mutated sentry damage", {})

    assert any("damageState" in difference for difference in damage_differences), damage_differences


def test_set_state_maps_runtime_sentry_ids_without_skipping_the_slim_comparison():
    race = JUMP["races"][0]
    state_step = next(step for step in race["steps"] if step["label"] == "look at the state")
    event = next(event for event in state_step["events"] if event["name"] == "DoDestinyUpdate")
    recorded = next(action for action in actions_of(event)[0] if action[1] == "SetState")[2]
    actual = copy.deepcopy(recorded)
    state_fields = actual["$tuple"][0]["args"]["$dict"]
    slims = next(value for key, value in state_fields if key == "slims")
    sentry_index = 0
    for slim in slims:
        fields = slim["args"]["$dict"]
        if dict(fields).get("groupID") != 99:
            continue
        item_id = next(pair for pair in fields if pair[0] == "itemID")
        item_id[1] = {"$long": 980_100_000_100 + sentry_index}
        sentry_index += 1

    assert sentry_index == 8
    mapping = {}
    differences = compare_state(recorded, actual, "runtime sentry ids", mapping)

    assert differences == [], differences
    assert len(mapping) >= sentry_index
    assert all(number(keyval(slim)["itemID"]) >= 980_100_000_100 for slim in mapping["_sentry_slims"].values())
