"""The recordings of the reference under oracle/fixtures/scenarios are complete and well formed."""

import json
import os

import pytest

DIR = os.path.join(os.path.dirname(__file__), "..", "..", "..", "oracle", "fixtures", "scenarios")


def load(name):
    with open(os.path.join(DIR, name), encoding="utf-8") as handle:
        return json.load(handle)


def rows(described):
    return [{k: v for k, v in row["args"]["$dict"]} for row in described]


def test_the_walk_covers_login_names_creation_limits_and_selection():
    walk = load("login_to_character.json")
    assert walk["reference"] == "EVE.js" and walk["referenceCommit"]
    methods = [step["call"][1] for step in walk["steps"]]
    for wanted in ("GetNumCharacters", "GetCharactersToSelect", "GetCharacterSelectionData", "CreateCharacterWithDoll", "SelectCharacterID", "ValidateNameEx"):
        assert wanted in methods
    assert len(walk["names"]) >= 20
    assert {n["name"]: n.get("ok") for n in walk["names"]}["Bad@Name"] == -5
    assert len(walk["afterSelect"]) >= 1, "the session change is recorded"


def test_a_fresh_account_has_nothing_to_select():
    walk = load("login_to_character.json")
    first = {step["call"][1]: step for step in walk["steps"][:6]}
    assert first["GetNumCharacters"]["ok"] == 0
    assert first["GetCharactersToSelect"]["ok"] == []
    account, chars, _, _ = walk["steps"][2]["ok"]["$tuple"][0], walk["steps"][2]["ok"]["$tuple"][2], None, None
    assert chars == []
    assert rows(account)[0]["maxCharacterSlots"] == 3


def test_every_playable_bloodline_was_recorded_with_its_starter_kit():
    starters = load("starters.json")["starters"]
    assert [s["bloodlineID"] for s in starters] == [1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14]
    ships = {}
    for s in starters:
        row = rows(s["selection"]["ok"]["$tuple"][2])[0]
        assert row["bloodlineID"] == s["bloodlineID"] and row["raceID"] == s["raceID"] and row["schoolID"] == s["schoolID"]
        ships.setdefault(row["raceID"], set()).add((row["shipTypeID"], row["shipName"]))
    assert ships == {1: {(601, "Ibis")}, 2: {(588, "Reaper")}, 4: {(596, "Impairor")}, 8: {(606, "Velator")}}


@pytest.mark.parametrize("bloodline,type_id", [(1, 1373), (2, 1374), (3, 1375), (4, 1376), (5, 1377), (6, 1378), (7, 1379), (8, 1380), (11, 1383), (12, 1384), (13, 1385), (14, 1386)])
def test_the_character_type_follows_the_bloodline(bloodline, type_id):
    starter = next(s for s in load("starters.json")["starters"] if s["bloodlineID"] == bloodline)
    assert rows(starter["selection"]["ok"]["$tuple"][2])[0]["typeID"] == type_id
