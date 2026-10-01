#!/usr/bin/env python3
"""Record what the EVE.js reference does from login to a session with a character.

    scripts/record_character_scenarios.py <host:port> <sde-dir> <out-dir> [--reference-commit SHA] [--only walk|starters|skills|info|stations|inventory|dogma|fitting|training|training_fast|undock|approach|movement|warp|jump|others|target|module|weapon|ammo|cargo|reload|drones|health|death]

The recording is the contract for the same path in eve.rs: `tests/test_login_to_character.py` replays the calls
against any server and compares each answer with what is stored here, by shape, treating per-run values
(identifiers, times) by type only. Nothing here is copied from client code: the calls are the ones the client
makes, and the answers are what the reference sent.

It writes login_to_character.json (one account walked through creation and selection, plus name validation,
errors and limits), starters.json (a character per playable bloodline: what the reference gives each) and
skills.json (what the skill handler answers for a new character of each race) and character_info.json (what the
character manager and the wallet answer for one) and stations.json (the reference's record of a sample of stations,
for their names and places) and inventory.json (what the inventory broker and the bound inventories of the station
hangar, the station and the ship answer for a new character). `--only` records one of them and leaves the others as
they are.
"""

from __future__ import annotations

import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from evecli import marshal as m  # noqa: E402
from evecli import packet as p  # noqa: E402
from evecli.client import Client  # noqa: E402
from evecli.describe import describe  # noqa: E402
from evecli.observe import ego_state, events_of, hangar_and_ship, notifications_of, ship_of, timed_events, training_view, wait_for_events, wait_quietly  # noqa: E402

# The school the reference offers each playable race (its minimal map), as the client would send one.
SCHOOL_OF_RACE = {1: 35, 2: 33, 4: 31, 8: 37}
RACE_OF_BLOODLINE = {1: 1, 2: 1, 3: 2, 4: 2, 5: 4, 6: 4, 7: 8, 8: 8, 11: 1, 12: 8, 13: 4, 14: 2}


def load_jsonl(directory: str, name: str) -> list[dict]:
    with open(os.path.join(directory, name + ".jsonl"), encoding="utf-8") as handle:
        return [json.loads(line) for line in handle if line.strip()]


def first_ancestry(sde: str) -> dict[int, int]:
    found: dict[int, int] = {}
    for row in load_jsonl(sde, "ancestries"):
        found.setdefault(row["bloodlineID"], row["_key"])
    return found


class Recorder:
    def __init__(self, host: str, port: int, run: str) -> None:
        self.host, self.port, self.run = host, port, run
        self.clients: list[Client] = []

    def login(self, name: str) -> Client:
        client = Client(self.host, self.port, timeout=30)
        client.login(f"rec-{self.run}-{name}", "recorded-password")
        self.clients.append(client)
        return client

    def close(self) -> None:
        for client in self.clients:
            client.close()

    @staticmethod
    def call(client: Client, service: str, method: str, *args) -> dict:
        step = {"call": [service, method, [describe(a) for a in args]]}
        try:
            step["ok"] = describe(client.call(service, method, *args))
        except p.MachoError as error:
            step["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        return step

    @staticmethod
    def drain(client: Client) -> list:
        """Everything the server sent besides answers: a ping's answer proves earlier packets have arrived."""
        client.ping()
        seen = [describe(n.raw) for n in client.notifications if n.command in (p.SESSIONCHANGENOTIFICATION, p.NOTIFICATION)]
        client.notifications.clear()
        return seen


def create_args(name: str, bloodline: int, gender: int, ancestry: int) -> tuple:
    """The arguments of the client's call: (name, raceID, bloodlineID, genderID, ancestryID, charInfo, portraitInfo,
    schoolID, _, qaStarterSystemID). The two payloads stand for the paper-doll blobs the client sends; the reference
    tells this call apart from older shapes by their being present, so they must be."""
    race = RACE_OF_BLOODLINE[bloodline]
    doll = m.Dict([("recorded", 1)])
    portrait = m.Dict([("recorded", 2)])
    return (name, race, bloodline, gender, ancestry, doll, portrait, SCHOOL_OF_RACE[race], None, None)


def record_walk(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    # ── the walk: one new account, from nothing to a session with a character ──
    c = rec.login("walk")
    walk = {
        "login": {
            "serverVersion": describe(tuple(c.session.server.__dict__.values())),
            "sessionInit": describe(c.session.session_init) if not isinstance(c.session.session_init, dict) else {k: describe(v) for k, v in c.session.session_init.items()},
        },
        "steps": [],
    }
    steps = walk["steps"]
    add = lambda *a: steps.append(rec.call(c, *a))  # noqa: E731

    for method, args in [("GetNumCharacters", ()), ("GetCharactersToSelect", ()), ("GetCharacterSelectionData", ()),
                         ("GetCohortsForUser", ()), ("GetCharacterLockType", ()), ("ToggleValidation", (True,))]:
        add("charUnboundMgr", method, *args)
    for race in (1, 2, 4, 8):
        add("charUnboundMgr", "GetValidRandomName", race)

    names = ["Recorded Pilot", "ab", "abc", "a" * 37, "a" * 38, "Two  Spaces", "One Two Three Four", "Bad@Name", "Bad_Name",
             "CCP Someone", "GM Someone", "ISD Someone", "Elysian", "O'Brien-Smith", "Ünïcode Pilote", "Ölçü 123", "", "   ", " Recorded Pilot ", "recorded pilot"]
    walk["names"] = [{"name": n, **{k: v for k, v in rec.call(c, "charUnboundMgr", "ValidateNameEx", n).items() if k != "call"}} for n in names]

    created = rec.call(c, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Recorded Pilot {run}", 1, 1, ancestry[1]))
    steps.append(created)
    char_id = int(created["ok"]) if "ok" in created else None
    for method in ("GetNumCharacters", "GetCharactersToSelect", "GetCharacterSelectionData"):
        add("charUnboundMgr", method)
    steps.append(rec.call(c, "charUnboundMgr", "ValidateNameEx", f"Recorded Pilot {run}"))  # now taken
    steps.append(rec.call(c, "charUnboundMgr", "ValidateNameEx", f"recorded  pilot {run}".replace("  ", " ")))  # case-folded duplicate

    # errors and limits
    steps.append(rec.call(c, "charUnboundMgr", "CreateCharacterWithDoll", *create_args("a", 1, 1, ancestry[1])))
    steps.append(rec.call(c, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Recorded Pilot {run}", 1, 1, ancestry[1])))
    for n in (2, 3):
        steps.append(rec.call(c, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Recorded X{n} {run}", 2, 2, ancestry[2])))
    steps.append(rec.call(c, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Recorded X4 {run}", 3, 1, ancestry[3])))
    steps.append(rec.call(c, "charUnboundMgr", "GetNumCharacters"))
    steps.append(rec.call(c, "charUnboundMgr", "SelectCharacterID", 1))
    other = rec.login("other")
    other_char = rec.call(other, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Recorded Other {run}", 7, 1, ancestry[7]))
    walk["otherAccountCharacter"] = other_char
    if "ok" in other_char:
        steps.append(rec.call(c, "charUnboundMgr", "SelectCharacterID", int(other_char["ok"])))

    # the selection itself
    c.notifications.clear()
    steps.append(rec.call(c, "charUnboundMgr", "SelectCharacterID", char_id))
    walk["afterSelect"] = rec.drain(c)
    steps.append(rec.call(c, "machoNet", "GetTime"))

    with open(os.path.join(out, "login_to_character.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", **walk}, handle, indent=1, ensure_ascii=False)
        handle.write("\n")


def record_starters(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    # ── what each playable bloodline gets ──
    starters = []
    for bloodline in RACE_OF_BLOODLINE:
        who = rec.login(f"bl{bloodline}")
        gender = 1 if bloodline % 2 else 2
        made = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Starter {bloodline} {run}", bloodline, gender, ancestry[bloodline]))
        row = rec.call(who, "charUnboundMgr", "GetCharacterSelectionData")
        starters.append({"bloodlineID": bloodline, "raceID": RACE_OF_BLOODLINE[bloodline], "genderID": gender, "ancestryID": ancestry[bloodline],
                         "schoolID": SCHOOL_OF_RACE[RACE_OF_BLOODLINE[bloodline]], "create": made, "selection": row})
    with open(os.path.join(out, "starters.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "starters": starters}, handle, indent=1, ensure_ascii=False)
        handle.write("\n")


# What the client asks the skill handler, and with what, in the order its skill window and login make the calls.
SKILL_CALLS = [
    ("skillMgr2", "GetMySkillHandler", ()),
    ("skillHandler", "GetSkills", ()),
    ("skillHandler", "GetAllSkills", ()),
    ("skillHandler", "GetSkillPoints", ()),
    ("skillHandler", "GetFreeSkillPoints", ()),
    ("skillHandler", "GetSkillQueue", ()),
    ("skillHandler", "GetMySkillQueue", ()),
    ("skillHandler", "GetSkillQueueAndFreePoints", ()),
    ("skillHandler", "GetAttributes", ()),
    ("skillHandler", "GetCharacterAttributeModifiers", ()),
    ("skillHandler", "GetBoosters", ()),
    ("skillHandler", "GetImplants", ()),
    ("skillHandler", "GetSkillHistory", ()),
    ("skillHandler", "GetRespecInfo", ()),
    ("skillHandler", "GetFreeSkillPointsAppliedToQueue", ()),
    ("skillHandler", "GetFreeSkillPointsAppliedToSkills", ()),
    ("skillHandler", "GetMySkillInfo", ()),
    ("skillHandler", "IsSkillInjected", (3300,)),
    ("skillHandler", "IsSkillInjected", (3301,)),
    ("skillHandler", "IsSkill", (3300,)),
    ("skillHandler", "IsSkill", (587,)),
]


def share_skill_list(calls: list) -> None:
    """The three calls that return the whole skill list return the same list: store it once (`{"$same": "GetSkills"}`)."""
    skills = next(step["ok"] for step in calls if step["call"][1] == "GetSkills")
    for step in calls:
        if step["call"][1] == "GetAllSkills" and step.get("ok") == skills:
            step["ok"] = {"$same": "GetSkills"}
        if step["call"][1] == "GetMySkillInfo" and "ok" in step:
            pairs = step["ok"]["args"]["$dict"]
            for pair in pairs:
                if pair[0] == "skills" and pair[1] == skills:
                    pair[1] = {"$same": "GetSkills"}


def record_skills(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """One new character per playable race, chosen, and what the skill handler tells it. The client gets its skill
    handler through a moniker and binds it: the bind call, with a nested call, is recorded too."""
    races = []
    for bloodline in (1, 3, 5, 7):
        who = rec.login(f"skills{bloodline}")
        created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Skilled {bloodline} {run}", bloodline, 1, ancestry[bloodline]))
        char_id = int(created["ok"])
        who.notifications.clear()
        select = rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
        after = rec.drain(who)
        calls = [rec.call(who, service, method, *args) for service, method, args in SKILL_CALLS]
        share_skill_list(calls)
        bind = [
            rec.call(who, "skillHandler", "MachoResolveObject", char_id, 0),
            rec.call(who, "skillHandler", "MachoBindObject", char_id, ("GetSkillPoints", (), None)),
        ]
        races.append({"bloodlineID": bloodline, "raceID": RACE_OF_BLOODLINE[bloodline], "create": created, "select": select,
                      "afterSelect": after, "calls": calls, "bind": bind})
    with open(os.path.join(out, "skills.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": races}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")


# What the client asks about the character it plays: `None` stands for the character's own id, filled in when the call is made.
INFO_CALLS = [
    ("charMgr", "GetPublicInfo", (None,)),
    ("charMgr", "GetPublicInfo3", (None,)),
    ("charMgr", "GetPublicInfo3", (None, 0)),
    ("charMgr", "GetHomeStationRow", ()),
    ("charMgr", "GetCharacterDescription", (None,)),
    ("charMgr", "GetPaperdollState", ()),
    ("charMgr", "GetCharacterCreationDate", (None,)),
    ("charMgr", "GetCohortsForCharacter", ()),
    ("charMgr", "GetSettingsInfo", ()),
    ("charMgr", "GetContactList", ()),
    ("charMgr", "GetRecentShipKillsAndLosses", (10, 0)),
    ("account", "GetCashBalance", ()),
    ("account", "GetCashBalance", (0,)),
    ("account", "GetCashBalance", (1,)),
    # the station the character starts in
    ("stationSvc", "GetStation", (60015249,)),
    ("stationSvc", "GetStationItemBits", ()),
    ("station", "GetStationItemBits", ()),
    ("stationSvc", "GetGuests", ()),
    ("station", "GetGuests", ()),
    ("stationSvc", "GetSolarSystem", ()),
    ("stationSvc", "GetAllianceSystems", ()),
    ("stationSvc", "GetSystemsForAlliance", (0,)),
]


def record_info(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """One new character per playable race, chosen, and what the character manager and the wallet tell it."""
    races = []
    for bloodline in (1, 3, 5, 7):
        who = rec.login(f"info{bloodline}")
        created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Informed {bloodline} {run}", bloodline, 1, ancestry[bloodline]))
        char_id = int(created["ok"])
        rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
        calls = [rec.call(who, service, method, *[char_id if a is None else a for a in args]) for service, method, args in INFO_CALLS]
        races.append({"bloodlineID": bloodline, "raceID": RACE_OF_BLOODLINE[bloodline], "create": created, "calls": calls})
        # leave the game before the next character enters, so the guest list of each is its own
        who.close()
        time.sleep(1.5)
    with open(os.path.join(out, "character_info.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": races}, handle, indent=1, ensure_ascii=False)
        handle.write("\n")


def record_stations(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str, sde: str = "") -> None:
    """The reference's station record for a sample of the stations in the static data (every 87th, in file order, and
    the two the character screens use), to check the names and places eve.rs derives from the same data."""
    rows = load_jsonl(sde, "npcStations")
    wanted = [row["_key"] for row in rows[::87]] + [60015249, 60003760]
    who = rec.login("stations")
    stations = []
    for station_id in dict.fromkeys(wanted):
        stations.append({"id": station_id, "call": rec.call(who, "stationSvc", "GetStation", station_id)})
    with open(os.path.join(out, "stations.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "stations": stations}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")


# What the client asks a bound inventory. The three inventories are the character's hangar in its station, the station
# itself and the ship in the hangar.
INVENTORY_CALLS = [
    ("List", ()),
    ("List", (4,)),
    ("List", (5,)),
    ("ListByFlags", ((4, 5),)),
    ("GetItem", ()),
    ("GetSelfInvItem", ()),
    ("GetCapacity", ()),
    ("GetCapacity", (5,)),
    ("GetItems", ()),
    ("GetContainerContents", ()),
]


def record_inventory(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """One new character per playable race, chosen; then its hangar, its station and its ship opened as inventories and
    asked the calls of INVENTORY_CALLS. A bound object is handed out by `GetInventory` / `GetInventoryFromId`; the
    handle differs on every run and is kept only as a marker."""
    races = []
    for bloodline in (1, 3, 5, 7):
        who = rec.login(f"inventory{bloodline}")
        created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Stocked {bloodline} {run}", bloodline, 1, ancestry[bloodline]))
        char_id = int(created["ok"])
        rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
        descriptor = rec.call(who, "invbroker", "GetItemDescriptor")

        hangar = who.call("invbroker", "GetInventory", 10004)
        ship_id = who.call_bound(hangar, "List")[0].fields()["itemID"]
        inventories = {}
        for label, opener, argument in (("hangar", "GetInventory", 10004), ("station", "GetInventoryFromId", 60015249), ("ship", "GetInventoryFromId", int(ship_id))):
            handle = who.call(("invbroker"), opener, argument)
            opened = {"call": ["invbroker", opener, [describe(argument)]], "ok": describe(handle)}
            calls = []
            for method, args in INVENTORY_CALLS:
                step = {"call": [label, method, [describe(a) for a in args]]}
                try:
                    step["ok"] = describe(who.call_bound(handle, method, *args))
                except p.MachoError as error:
                    step["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
                calls.append(step)
            inventories[label] = {"open": opened, "calls": calls}
        races.append({"bloodlineID": bloodline, "raceID": RACE_OF_BLOODLINE[bloodline], "create": created, "shipID": int(ship_id),
                      "descriptor": descriptor, "inventories": inventories})
        who.close()
        time.sleep(1.5)
    with open(os.path.join(out, "inventory.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": races}, handle, indent=1, ensure_ascii=False)
        handle.write("\n")


def record_dogma(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """One new character per playable race, chosen; then what the client asks the dogma manager when it enters the station:
    the location is bound with (station, group station) and `GetAllInfo(character, ship, structure)` is the nested call.
    The events the server sends because of it (the character's attributes, the brain) are recorded with the answer."""
    races = []
    for bloodline in (1, 3, 5, 7):
        who = rec.login(f"dogma{bloodline}")
        created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Dogmatic {bloodline} {run}", bloodline, 1, ancestry[bloodline]))
        char_id = int(created["ok"])
        rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
        who.notifications.clear()
        bind = rec.call(who, "dogmaIM", "MachoBindObject", (60015249, 15), ("GetAllInfo", (True, True, None), None))
        arrived_first = len([n for n in who.notifications if n.command == p.NOTIFICATION])  # how many came before the answer
        races.append({"bloodlineID": bloodline, "raceID": RACE_OF_BLOODLINE[bloodline], "create": created, "bind": bind,
                      "notifications": notifications_of(who), "notificationsBeforeAnswer": arrived_first})
        who.close()
        time.sleep(1.5)
    with open(os.path.join(out, "dogma.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": races}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")


def record_fitting(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """What the reference does when modules are switched and moved: the online state of the afterburner and the weapon
    (`SetModuleOnline` and `TakeModuleOffline` on the bound dogma manager), and a module moved out to the hangar (the unfit the
    client sends is `hangar.Add(itemID, shipID, flag=hangar)`). Each step: the answer, the events the client is sent, and
    where everything is after. Two races, so the weapon differs."""
    races = []
    for bloodline in (1, 3):
        who = rec.login(f"fitting{bloodline}")
        created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Fitted {bloodline} {run}", bloodline, 1, ancestry[bloodline]))
        char_id = int(created["ok"])
        rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
        mgr = who.call("invbroker", "MachoBindObject", (60015249, 15), None)[0]
        hangar = who.call_bound(mgr, "GetInventory", 10004)
        ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
        before = hangar_and_ship(who, mgr, ship_id)
        ids = [int(x["itemID"]["$long"] if isinstance(x["itemID"], dict) else x["itemID"]) for x in before["shipInfo"]]
        dogma = who.call("dogmaIM", "MachoBindObject", (60015249, 15), ("GetAllInfo", (True, True, None), None))[0]
        _ship, afterburner, weapon, _miner = ids
        notifications_of(who)
        plan = [
            ("afterburner offline", dogma, "TakeModuleOffline", (ship_id, afterburner), {}),
            ("afterburner online", dogma, "SetModuleOnline", (ship_id, afterburner), {}),
            ("afterburner online again", dogma, "SetModuleOnline", (ship_id, afterburner), {}),
            ("weapon offline", dogma, "TakeModuleOffline", (ship_id, weapon), {}),
            ("weapon to the hangar while offline", hangar, "Add", (weapon, ship_id), {"flag": 4}),
            ("afterburner to the hangar", hangar, "Add", (afterburner, ship_id), {"flag": 4}),
        ]
        steps = []
        for label, handle, method, args, kwargs in plan:
            step = {"label": label, "call": [method, list(args), kwargs]}
            try:
                step["ok"] = describe(who.call_bound(handle, method, *args, **kwargs))
            except p.MachoError as error:
                step["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
            step["notifications"] = notifications_of(who)
            step["after"] = hangar_and_ship(who, mgr, ship_id)
            notifications_of(who)  # what looking made the server send is not part of the step
            steps.append(step)
        races.append({"bloodlineID": bloodline, "raceID": RACE_OF_BLOODLINE[bloodline], "create": created, "shipID": ship_id, "moduleIDs": ids, "before": before, "steps": steps})
        who.close()
        time.sleep(1.5)
    with open(os.path.join(out, "fitting.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": races}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")


def record_training(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """The skill queue as the client uses it, at the reference's normal training speed: starting a skill, saving and
    reordering a queue, pausing and resuming it, the errors the queue gives, aborting. Each step: the answer, the events
    the client is sent, and the skill window afterwards. Times differ per run; the tests compare the durations."""
    races = []
    for bloodline in (1, 5):
        who = rec.login(f"training{bloodline}")
        created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Trained {bloodline} {run}", bloodline, 1, ancestry[bloodline]))
        char_id = int(created["ok"])
        rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
        before = training_view(who)
        speed = describe(who.call("skillHandler", "GetAttributes"))["$dict"][0][1] / 20
        ship_id = ship_of(who)
        notifications_of(who)
        own = sorted(before["skills"], key=lambda t: (before["skills"][t]["level"], t))
        below_five = [t for t in own if 1 <= before["skills"][t]["level"] <= 3]
        first, second = below_five[0], below_five[1]
        first_level = before["skills"][first]["level"]
        item = char_id * 100000 + first
        queue = m.Dict([(0, (first, first_level + 1)), (1, (first, first_level + 2)), (2, (second, before["skills"][second]["level"] + 1))])
        reordered = m.Dict([(0, (second, before["skills"][second]["level"] + 1)), (1, (first, first_level + 1))])
        plan = [
            ("empty queue", "skillHandler", "GetSkillQueue", (), {}),
            ("empty queue and free points", "skillHandler", "GetSkillQueueAndFreePoints", (), {}),
            ("start by type", "skillHandler", "CharStartTrainingSkillByTypeID", (first,), {}),
            ("start by type again", "skillHandler", "CharStartTrainingSkillByTypeID", (first,), {}),
            ("start two levels ahead", "skillHandler", "CharStartTrainingSkillByTypeID", (first, first_level + 2), {}),
            ("start unknown type", "skillHandler", "CharStartTrainingSkillByTypeID", (99,), {}),
            ("start by item", "skillHandler", "CharStartTrainingSkill", (item,), {}),
            ("start by unknown item", "skillHandler", "CharStartTrainingSkill", (12345,), {}),
            ("save three", "skillHandler", "SaveNewQueue", (queue,), {}),
            ("save the same again", "skillHandler", "SaveNewQueue", (queue,), {}),
            ("save reordered", "skillHandler", "SaveNewQueue", (reordered,), {}),
            ("save paused", "skillHandler", "SaveNewQueue", (queue,), {"activate": False}),
            ("save and start", "skillHandler", "SaveNewQueue", (queue,), {}),
            ("level out of order", "skillHandler", "SaveNewQueue", (m.Dict([(0, (first, first_level + 2))]),), {}),
            ("level already trained", "skillHandler", "SaveNewQueue", (m.Dict([(0, (first, first_level))]),), {}),
            ("the same level twice", "skillHandler", "SaveNewQueue", (m.Dict([(0, (first, first_level + 1)), (1, (first, first_level + 1))]),), {}),
            ("level six", "skillHandler", "SaveNewQueue", (m.Dict([(0, (first, 6))]),), {}),
            ("skill not injected", "skillHandler", "SaveNewQueue", (m.Dict([(0, (20315, 1))]),), {}),
            ("skill queue after the errors", "skillHandler", "GetSkillQueue", (), {}),
            ("abort", "skillHandler", "AbortTraining", (), {}),
            ("abort again", "skillHandler", "AbortTraining", (), {}),
            ("stop while stopped", "skillHandler", "CharStopTrainingSkill", (), {}),
            ("start again", "skillHandler", "CharStartTrainingSkillByTypeID", (second,), {}),
            ("stop", "skillHandler", "CharStopTrainingSkill", (), {}),
            ("save empty", "skillHandler", "SaveNewQueue", (m.Dict([]),), {}),
            ("history", "skillHandler", "GetSkillHistory", (), {}),
        ]
        steps = []
        for label, service, method, args, kwargs in plan:
            step = {"label": label, "call": [service, method, [describe(a) for a in args], kwargs]}
            try:
                step["ok"] = describe(who.call(service, method, *args, **kwargs))
            except p.MachoError as error:
                step["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
            step["notifications"] = notifications_of(who)
            if method != "GetSkillHistory":
                step["after"] = training_view(who)
                notifications_of(who)
            steps.append(step)
        races.append({"bloodlineID": bloodline, "raceID": RACE_OF_BLOODLINE[bloodline], "create": created, "characterID": char_id, "shipID": ship_id,
                      "skills": {"first": first, "second": second}, "trainingSpeed": speed, "before": before, "steps": steps})
        who.close()
        time.sleep(1.5)
    with open(os.path.join(out, "training.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": races}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")


def record_training_fast(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """Skills that finish while the client watches, which needs a reference started with a huge EVEJS_SKILL_TRAINING_SPEED
    (the file records the speed it saw): a queue of two levels running out, a rank 5 skill (its points are rounded up), and a
    queue that finishes while the character is not logged in. Each step: the events the client is sent while it waits, and the
    skill window afterwards."""
    who = rec.login("trainfast")
    created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Swift {run}", 1, 1, ancestry[1]))
    char_id = int(created["ok"])
    rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
    before = training_view(who)
    speed = describe(who.call("skillHandler", "GetAttributes"))["$dict"][0][1] / 20
    ship_id = ship_of(who)
    notifications_of(who)
    if speed < 1000:
        raise SystemExit(f"the reference trains at speed {speed}: start it with EVEJS_SKILL_TRAINING_SPEED=60000 to record this")
    skills = before["skills"]
    ranked = {t: v for t, v in skills.items() if v["level"] == 1}
    plan = [
        ("two levels in a queue", "SaveNewQueue", (m.Dict([(0, (3301, 2)), (1, (3301, 3))]),), 2),
        ("a rank five skill", "CharStartTrainingSkillByTypeID", (3317,), 1),
        ("a rank one skill from level three", "CharStartTrainingSkillByTypeID", (3327,), 1),
    ]
    steps = []
    for label, method, args, levels in plan:
        step = {"label": label, "call": ["skillHandler", method, [describe(a) for a in args], {}]}
        try:
            step["ok"] = describe(who.call("skillHandler", method, *args))
        except p.MachoError as error:
            step["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        step["notifications"] = wait_for_events(who, lambda seen: sum(1 for e in seen if e["name"] == "OnSkillQueuePausedServer") >= 1)
        step["after"] = training_view(who)
        notifications_of(who)
        steps.append(step)
    # a level that finishes while nobody is logged in is found when the character comes back
    step = {"label": "finished while away", "call": ["skillHandler", "SaveNewQueue", [], {}]}
    who.call("skillHandler", "SaveNewQueue", m.Dict([(0, (3319, 2))]))
    notifications_of(who)
    who.close()
    time.sleep(4)
    back = rec.login("trainfast")
    back.notifications.clear()
    rec.call(back, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
    step["notifications"] = notifications_of(back)
    step["after"] = training_view(back)
    steps.append(step)
    back.close()
    with open(os.path.join(out, "training_fast.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "trainingSpeed": speed, "create": created,
                   "characterID": char_id, "shipID": ship_id, "before": before, "steps": steps}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")


def record_undock(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """A new character leaves its station, is sent the solar system, flies and docks again: `ship.Undock` on the ship service bound
    to the station, the ballpark (`beyonce`) bound to the solar system, `UpdateStateRequest`, then the movement commands and
    `CmdDock`. Each step: the answer and everything the client is sent while it waits (named events and session changes, the
    destiny actions inside them opened)."""
    races = []
    for bloodline in (1, 5):
        who = rec.login(f"undock{bloodline}")
        created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Undocker {bloodline} {run}", bloodline, 1, ancestry[bloodline]))
        char_id = int(created["ok"])
        rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
        manager = who.call("invbroker", "MachoBindObject", (60015249, 15), None)[0]
        hangar = who.call_bound(manager, "GetInventory", 10004)
        ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
        who.call("dogmaIM", "MachoBindObject", (60015249, 15), ("GetAllInfo", (True, True, None), None))
        events_of(who)

        def step(label, call, wait=1.5, until=None):
            entry = {"label": label}
            try:
                entry["ok"] = describe(call())
            except p.MachoError as error:
                entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
            entry["events"] = wait_quietly(who, wait, until)
            return entry

        steps = []
        ship = who.call("ship", "MachoBindObject", (60015249, 15), None)
        steps.append({"label": "bind the ship service", "ok": describe(ship), "events": wait_quietly(who, 0.5)})
        steps.append(step("undock", lambda: who.call_bound(ship[0], "Undock", ship_id, False, onlineModules=m.List([ship_id + 1, ship_id + 2, ship_id + 3])), 2.0))
        beyonce = who.call("beyonce", "MachoBindObject", 30100032, None)
        steps.append({"label": "bind the ballpark", "ok": describe(beyonce), "events": wait_quietly(who, 2.5)})
        steps.append(step("formations", lambda: who.call_bound(beyonce[0], "GetFormations"), 0.5))
        steps.append(step("update state", lambda: who.call_bound(beyonce[0], "UpdateStateRequest"), 2.0))
        steps.append(step("speed fraction", lambda: who.call_bound(beyonce[0], "CmdSetSpeedFraction", 0.5), 1.5))
        steps.append(step("go in a direction", lambda: who.call_bound(beyonce[0], "CmdGotoDirection", 1.0, 0.0, 0.0), 1.5))
        steps.append(step("go in another direction", lambda: who.call_bound(beyonce[0], "CmdGotoDirection", 0.0, 0.0, 1.0), 1.5))
        steps.append(step("stop", lambda: who.call_bound(beyonce[0], "CmdStop"), 1.5))
        steps.append(step("dock", lambda: who.call_bound(beyonce[0], "CmdDock", 60015249), 9.0, until=lambda seen: any(e["name"] == "$sessionchange" for e in seen)))
        races.append({"bloodlineID": bloodline, "raceID": RACE_OF_BLOODLINE[bloodline], "create": created, "characterID": char_id, "shipID": ship_id,
                      "moduleIDs": [ship_id + 1, ship_id + 2, ship_id + 3], "solarSystemID": 30100032, "stationID": 60015249, "steps": steps})
        who.close()
        time.sleep(1.5)
    with open(os.path.join(out, "undock.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": races}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")


def record_approach(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """A ship flies out of docking range of its station and is told to dock: the reference makes it approach the station and tells the client
    (a user error); it does not dock by itself, the pilot asks again. It takes about five minutes (the station's bounding sphere is 70 km
    across), so one race."""
    station = (311989870775.0, -13069156275.0, 156646281447.0)
    station_radius = 70000.0
    bloodline = 1
    who = rec.login(f"approach{bloodline}")
    created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Approacher {bloodline} {run}", bloodline, 1, ancestry[bloodline]))
    char_id = int(created["ok"])
    rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
    manager = who.call("invbroker", "MachoBindObject", (60015249, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
    who.call("dogmaIM", "MachoBindObject", (60015249, 15), ("GetAllInfo", (True, True, None), None))
    events_of(who)

    steps = []
    ship = who.call("ship", "MachoBindObject", (60015249, 15), None)
    steps.append({"label": "bind the ship service", "ok": describe(ship), "events": wait_quietly(who, 0.5)})
    undock = {"label": "undock", "ok": describe(who.call_bound(ship[0], "Undock", ship_id, False))}
    undock["events"] = wait_quietly(who, 2.0)
    steps.append(undock)
    beyonce = who.call("beyonce", "MachoBindObject", 30100032, None)
    steps.append({"label": "bind the ballpark", "ok": describe(beyonce), "events": wait_quietly(who, 2.5)})

    # the ship leaves the station at full speed; ask for its state until it is out of range (the docking range is 2.5 km off the surface)
    started = time.time()
    distance = 0.0
    while time.time() - started < 420:
        who.call_bound(beyonce[0], "UpdateStateRequest")
        events = timed_events(who, 15.0)
        state = ego_state(events, ship_id)
        if state is not None:
            (x, y, z), radius = state
            distance = ((x - station[0]) ** 2 + (y - station[1]) ** 2 + (z - station[2]) ** 2) ** 0.5 - radius - station_radius
            print(f"approach: {distance:.0f} m off the station's surface after {time.time() - started:.0f} s", file=sys.stderr)
            if distance > 6000:
                break
    steps.append({"label": "out of range", "distanceFromSurface": distance, "seconds": round(time.time() - started, 1)})

    entry = {"label": "dock from beyond the range"}
    call_started = time.time()
    try:
        entry["ok"] = describe(who.call_bound(beyonce[0], "CmdDock", 60015249))
    except p.MachoError as error:
        entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
    entry["answeredAfter"] = round(time.time() - call_started, 2)
    entry["events"] = timed_events(who, 1.0)
    steps.append(entry)

    # the reference approaches on its own; watch how it gets there (it does not dock by itself)
    approach = []
    arrived_at = time.time()
    while time.time() - call_started < 150:
        who.call_bound(beyonce[0], "UpdateStateRequest")
        state = ego_state(timed_events(who, 4.0), ship_id)
        if state is None:
            continue
        (x, y, z), radius = state
        distance = ((x - station[0]) ** 2 + (y - station[1]) ** 2 + (z - station[2]) ** 2) ** 0.5 - radius - station_radius
        approach.append({"seconds": round(time.time() - call_started, 1), "distanceFromSurface": distance})
        print(f"approach: {distance:.0f} m off the station's surface after {time.time() - call_started:.0f} s (following)", file=sys.stderr)
        if len(approach) >= 2 and abs(approach[-1]["distanceFromSurface"] - approach[-2]["distanceFromSurface"]) < 20 and distance < 5000:
            break
    steps.append({"label": "the approach", "readings": approach})

    entry = {"label": "dock again, in range"}
    try:
        entry["ok"] = describe(who.call_bound(beyonce[0], "CmdDock", 60015249))
    except p.MachoError as error:
        entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
    entry["events"] = timed_events(who, 15.0, until=lambda seen: any(e["name"] == "$sessionchange" for e in seen))
    steps.append(entry)
    race = {"bloodlineID": bloodline, "raceID": RACE_OF_BLOODLINE[bloodline], "create": created, "characterID": char_id, "shipID": ship_id,
            "moduleIDs": [ship_id + 1, ship_id + 2, ship_id + 3], "solarSystemID": 30100032, "stationID": 60015249, "steps": steps}
    with open(os.path.join(out, "approach.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [race]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    who.close()


def record_movement(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """A ship in space is told to align to, orbit and follow the station: what each command makes the reference send (one destiny update
    with the action, and a speed fraction when the command changes it), and what it does with a target it does not know."""
    bloodline = 1
    who = rec.login(f"movement{bloodline}")
    created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Mover {bloodline} {run}", bloodline, 1, ancestry[bloodline]))
    char_id = int(created["ok"])
    rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
    manager = who.call("invbroker", "MachoBindObject", (60015249, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
    who.call("dogmaIM", "MachoBindObject", (60015249, 15), ("GetAllInfo", (True, True, None), None))
    events_of(who)

    def step(label, call, wait=1.5):
        entry = {"label": label}
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry["events"] = wait_quietly(who, wait)
        return entry

    steps = []
    ship = who.call("ship", "MachoBindObject", (60015249, 15), None)
    steps.append({"label": "bind the ship service", "ok": describe(ship), "events": wait_quietly(who, 0.5)})
    steps.append(step("undock", lambda: who.call_bound(ship[0], "Undock", ship_id, False), 2.0))
    beyonce = who.call("beyonce", "MachoBindObject", 30100032, None)
    steps.append({"label": "bind the ballpark", "ok": describe(beyonce), "events": wait_quietly(who, 2.5)})
    call = lambda name, *args: (lambda: who.call_bound(beyonce[0], name, *args))
    steps.append(step("align to the station", call("CmdAlignTo", 60015249)))
    steps.append(step("align to what is not there", call("CmdAlignTo", 99999999)))
    steps.append(step("orbit the station", call("CmdOrbit", 60015249, 5000)))
    steps.append(step("orbit it again", call("CmdOrbit", 60015249, 5000)))
    steps.append(step("orbit closer", call("CmdOrbit", 60015249, 2500.7)))
    steps.append(step("follow the station", call("CmdFollowBall", 60015249, 3000)))
    steps.append(step("follow it again", call("CmdFollowBall", 60015249, 3000)))
    steps.append(step("stop", call("CmdStop")))
    steps.append(step("follow from a standstill", call("CmdFollowBall", 60015249, 1000)))
    steps.append(step("speed fraction", call("CmdSetSpeedFraction", 0.0)))
    steps.append(step("align from a standstill", call("CmdAlignTo", 60015249)))
    steps.append(step("follow what is not there", call("CmdFollowBall", 99999999, 500)))
    race = {"bloodlineID": bloodline, "raceID": RACE_OF_BLOODLINE[bloodline], "create": created, "characterID": char_id, "shipID": ship_id,
            "moduleIDs": [ship_id + 1, ship_id + 2, ship_id + 3], "solarSystemID": 30100032, "stationID": 60015249, "steps": steps}
    with open(os.path.join(out, "movement.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [race]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    who.close()


def record_warp(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """A ship warps to a planet two AU away and then stops there: `CmdWarpToStuff('item', planet, minRange=)` and what the client is sent while
    the ship goes (every event with the seconds since the call it arrived at), then the same again at a nearer range, and a stop in warp."""
    bloodline = 1
    who = rec.login(f"warp{bloodline}")
    created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Warper {bloodline} {run}", bloodline, 1, ancestry[bloodline]))
    char_id = int(created["ok"])
    rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
    manager = who.call("invbroker", "MachoBindObject", (60015249, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
    who.call("dogmaIM", "MachoBindObject", (60015249, 15), ("GetAllInfo", (True, True, None), None))
    events_of(who)

    steps = []
    ship = who.call("ship", "MachoBindObject", (60015249, 15), None)
    steps.append({"label": "bind the ship service", "ok": describe(ship), "events": wait_quietly(who, 0.5)})
    undock = {"label": "undock", "ok": describe(who.call_bound(ship[0], "Undock", ship_id, False))}
    undock["events"] = wait_quietly(who, 2.0)
    steps.append(undock)
    beyonce = who.call("beyonce", "MachoBindObject", 30100032, None)
    steps.append({"label": "bind the ballpark", "ok": describe(beyonce), "events": wait_quietly(who, 2.5)})

    def timed(label, call, seconds, until=None):
        entry = {"label": label}
        started = time.time()
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry["answeredAfter"] = round(time.time() - started, 2)
        entry["events"] = timed_events(who, seconds, until)
        return entry

    planet = 40491233  # 2.05 AU from the station
    warp = timed("warp to a planet", lambda: who.call_bound(beyonce[0], "CmdWarpToStuff", "item", planet, minRange=0), 1.0)
    # where the ship is every few seconds while it goes (the state the server gives), and every event of the warp
    readings = []
    warp_started = time.time()
    while time.time() - warp_started < 150:
        who.call_bound(beyonce[0], "UpdateStateRequest")
        seen = timed_events(who, 3.0)
        for event in seen:
            event["at"] = round(event["at"] + (time.time() - warp_started) - 3.0, 2)
        state = ego_state(seen, ship_id)
        if state is not None:
            readings.append({"seconds": round(time.time() - warp_started, 1), "position": list(state[0])})
        warp["events"] += [e for e in seen if not (e["name"] == "DoDestinyUpdate" and dest_names(e) == ["SetState"])]
        if any("Stop" in dest_names(e) for e in warp["events"]):
            break
    warp["readings"] = readings
    steps.append(warp)
    steps.append(timed("look at the state", lambda: who.call_bound(beyonce[0], "UpdateStateRequest"), 2.0))
    steps.append(timed("warp to it again, in warp range", lambda: who.call_bound(beyonce[0], "CmdWarpToStuff", "item", planet, minRange=0), 3.0))
    steps.append(timed("a warp to what is not there", lambda: who.call_bound(beyonce[0], "CmdWarpToStuff", "item", 99999999, minRange=0), 3.0))
    race = {"bloodlineID": bloodline, "raceID": RACE_OF_BLOODLINE[bloodline], "create": created, "characterID": char_id, "shipID": ship_id,
            "moduleIDs": [ship_id + 1, ship_id + 2, ship_id + 3], "solarSystemID": 30100032, "stationID": 60015249, "planetID": planet, "steps": steps}
    with open(os.path.join(out, "warp.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [race]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    who.close()


def dest_names(event: dict) -> list:
    """The names of the destiny actions in a `DoDestinyUpdate` event."""
    if event["name"] != "DoDestinyUpdate":
        return []
    actions, _ = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"]
    return [a["$tuple"][1]["$tuple"][0] for a in actions]


def record_jump(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """A ship warps to the stargate of its system and jumps: `CmdStargateJump(from, to, shipID)`, what the client is sent (the session change to the
    next system, the notifications of the jump), the ballpark of the next system bound and its state asked for."""
    bloodline = 1
    who = rec.login(f"jump{bloodline}")
    created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Jumper {bloodline} {run}", bloodline, 1, ancestry[bloodline]))
    char_id = int(created["ok"])
    rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
    manager = who.call("invbroker", "MachoBindObject", (60015249, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
    who.call("dogmaIM", "MachoBindObject", (60015249, 15), ("GetAllInfo", (True, True, None), None))
    events_of(who)

    gate, next_gate, next_system = 50016630, 50016839, 30100034
    steps = []
    ship = who.call("ship", "MachoBindObject", (60015249, 15), None)
    steps.append({"label": "bind the ship service", "ok": describe(ship), "events": wait_quietly(who, 0.5)})
    undock = {"label": "undock", "ok": describe(who.call_bound(ship[0], "Undock", ship_id, False))}
    undock["events"] = wait_quietly(who, 2.0)
    steps.append(undock)
    beyonce = who.call("beyonce", "MachoBindObject", 30100032, None)
    steps.append({"label": "bind the ballpark", "ok": describe(beyonce), "events": wait_quietly(who, 2.5)})

    def timed(label, call, seconds, until=None):
        entry = {"label": label}
        started = time.time()
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry["answeredAfter"] = round(time.time() - started, 2)
        entry["events"] = timed_events(who, seconds, until)
        return entry

    warp = timed("warp to the stargate", lambda: who.call_bound(beyonce[0], "CmdWarpToStuff", "item", gate, minRange=0), 1.0)
    warp_started = time.time()
    while time.time() - warp_started < 330:
        who.call_bound(beyonce[0], "UpdateStateRequest")
        seen = timed_events(who, 3.0)
        for event in seen:
            event["at"] = round(event["at"] + (time.time() - warp_started) - 3.0, 2)
        warp["events"] += [e for e in seen if not (e["name"] == "DoDestinyUpdate" and dest_names(e) == ["SetState"])]
        if any("Stop" in dest_names(e) for e in warp["events"]):
            break
    steps.append(warp)
    steps.append(timed("look at the state", lambda: who.call_bound(beyonce[0], "UpdateStateRequest"), 2.0))
    jump = timed("jump", lambda: who.call_bound(beyonce[0], "CmdStargateJump", gate, next_gate, ship_id), 12.0)
    steps.append(jump)
    landed = who.call("beyonce", "MachoBindObject", next_system, None)
    steps.append({"label": "bind the ballpark of the next system", "ok": describe(landed), "events": wait_quietly(who, 4.0)})
    steps.append(timed("look at the state there", lambda: who.call_bound(landed[0], "UpdateStateRequest"), 3.0))
    race = {"bloodlineID": bloodline, "raceID": RACE_OF_BLOODLINE[bloodline], "create": created, "characterID": char_id, "shipID": ship_id,
            "moduleIDs": [ship_id + 1, ship_id + 2, ship_id + 3], "solarSystemID": 30100032, "stationID": 60015249, "stargateID": gate,
            "nextStargateID": next_gate, "nextSolarSystemID": next_system, "steps": steps}
    with open(os.path.join(out, "jump.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [race]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    who.close()


def watch(clients: dict, seconds: float, until=None) -> dict:
    """The events each client is sent in `seconds` (or until `until(all so far)` is true), with the seconds since the start each arrived at."""
    started = time.time()
    out: dict = {name: [] for name in clients}
    while time.time() - started < seconds:
        for name, client in clients.items():
            for event in events_of(client):
                event["at"] = round(time.time() - started, 2)
                out[name].append(event)
        if until is not None and until(out):
            break
        time.sleep(0.25)
    return out


def record_others(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """Two characters in one system: what the one that is in space (the watcher, A) is sent while the other (B) undocks, binds the ballpark, is
    given orders, stops, docks, and at the end undocks again and warps to a planet. Each step holds what B is sent (`events`) and what A is sent
    (`seen`), with the seconds each arrived at."""
    def character(name: str):
        who = rec.login(name)
        created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"{name} {run}", 1, 1, ancestry[1]))
        char_id = int(created["ok"])
        rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
        manager = who.call("invbroker", "MachoBindObject", (60015249, 15), None)[0]
        hangar = who.call_bound(manager, "GetInventory", 10004)
        ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
        who.call("dogmaIM", "MachoBindObject", (60015249, 15), ("GetAllInfo", (True, True, None), None))
        events_of(who)
        return who, created, char_id, ship_id

    a, a_created, a_char, a_ship = character("Watcher")
    b, b_created, b_char, b_ship = character("Actor")
    both = {"events": b, "seen": a}
    steps: list = []

    a_service = a.call("ship", "MachoBindObject", (60015249, 15), None)
    b_service = b.call("ship", "MachoBindObject", (60015249, 15), None)
    watch(both, 0.5)
    steps.append({"label": "the watcher undocks", "ok": describe(a.call_bound(a_service[0], "Undock", a_ship, False)), **watch(both, 2.0)})
    a_ballpark = a.call("beyonce", "MachoBindObject", 30100032, None)
    steps.append({"label": "the watcher binds the ballpark", "ok": describe(a_ballpark), **watch(both, 2.5)})

    def step(label, call, seconds=2.0, until=None):
        entry = {"label": label}
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry.update(watch(both, seconds, until))
        return entry

    steps.append(step("the actor undocks", lambda: b.call_bound(b_service[0], "Undock", b_ship, False), 2.5))
    b_ballpark = None

    def bind():
        nonlocal b_ballpark
        b_ballpark = b.call("beyonce", "MachoBindObject", 30100032, None)
        return b_ballpark

    steps.append(step("the actor binds the ballpark", bind, 3.0))
    fly = lambda name, *args: (lambda: b.call_bound(b_ballpark[0], name, *args))  # noqa: E731
    steps.append(step("the actor goes in a direction", fly("CmdGotoDirection", 1.0, 0.0, 0.0), 2.0))
    steps.append(step("the actor slows down", fly("CmdSetSpeedFraction", 0.5), 2.0))
    steps.append(step("the actor orbits the station", fly("CmdOrbit", 60015249, 5000), 2.0))
    steps.append(step("the actor stops", fly("CmdStop"), 2.0))
    steps.append(step("the watcher asks for the state", lambda: a.call_bound(a_ballpark[0], "UpdateStateRequest"), 2.0))
    steps.append(step("the actor docks", fly("CmdDock", 60015249), 9.0, until=lambda seen: any(e["name"] == "$sessionchange" for e in seen["events"])))
    steps.append(step("the watcher asks for the state again", lambda: a.call_bound(a_ballpark[0], "UpdateStateRequest"), 2.0))

    # the actor goes out again and warps to a planet two AU away while the watcher stays where it is
    b_service = b.call("ship", "MachoBindObject", (60015249, 15), None)
    watch(both, 0.5)
    steps.append({"label": "the actor undocks again", "ok": describe(b.call_bound(b_service[0], "Undock", b_ship, False)), **watch(both, 2.5)})
    b_ballpark = b.call("beyonce", "MachoBindObject", 30100032, None)
    steps.append({"label": "the actor binds the ballpark again", "ok": describe(b_ballpark), **watch(both, 3.0)})
    planet = 40491233
    warp = {"label": "the actor warps to a planet"}
    try:
        warp["ok"] = describe(b.call_bound(b_ballpark[0], "CmdWarpToStuff", "item", planet, minRange=0))
    except p.MachoError as error:
        warp["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
    started = time.time()
    seen_both: dict = {"events": [], "seen": []}
    while time.time() - started < 150:
        got = watch(both, 3.0)
        for name in seen_both:
            for event in got[name]:
                event["at"] = round(event["at"] + (time.time() - started) - 3.0, 2)
            seen_both[name] += [e for e in got[name] if not (e["name"] == "DoDestinyUpdate" and dest_names(e) == ["SetState"])]
        if any("Stop" in dest_names(e) for e in seen_both["events"]):
            break
    warp.update(seen_both)
    steps.append(warp)
    steps.append(step("the watcher asks for the state after the warp", lambda: a.call_bound(a_ballpark[0], "UpdateStateRequest"), 2.0))
    race = {"bloodlineID": 1, "raceID": 1, "solarSystemID": 30100032, "stationID": 60015249, "planetID": planet,
            "watcher": {"create": a_created, "characterID": a_char, "shipID": a_ship}, "actor": {"create": b_created, "characterID": b_char, "shipID": b_ship},
            "steps": steps}
    with open(os.path.join(out, "others.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [race]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    a.close()
    b.close()


def record_target(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """Two characters in space by the same station: what the one that locks (the actor, B) and the one that is locked (the watcher, A) are
    sent as the actor asks for its targets, tries to lock itself, a planet out of range, the watcher and the station, gives up a lock, drops
    one and clears the lot. Each step holds what B is sent (`events`) and what A is sent (`seen`), with the seconds each arrived at."""
    system, station, planet = 30100032, 60015249, 40491233

    def character(name: str):
        who = rec.login(name)
        created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"{name} {run}", 1, 1, ancestry[1]))
        char_id = int(created["ok"])
        rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
        manager = who.call("invbroker", "MachoBindObject", (station, 15), None)[0]
        hangar = who.call_bound(manager, "GetInventory", 10004)
        ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
        who.call("dogmaIM", "MachoBindObject", (station, 15), ("GetAllInfo", (True, True, None), None))
        events_of(who)
        return who, created, char_id, ship_id

    a, a_created, a_char, a_ship = character("Watcher")
    b, b_created, b_char, b_ship = character("Actor")
    both = {"events": b, "seen": a}
    steps: list = []

    def step(label, call, seconds=2.0, until=None):
        entry = {"label": label}
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry.update(watch(both, seconds, until))
        steps.append(entry)
        return entry

    a_service = a.call("ship", "MachoBindObject", (station, 15), None)
    b_service = b.call("ship", "MachoBindObject", (station, 15), None)
    watch(both, 0.5)
    step("the watcher undocks", lambda: a.call_bound(a_service[0], "Undock", a_ship, False), 2.0)
    a_ballpark = a.call("beyonce", "MachoBindObject", system, None)
    step("the watcher binds the ballpark", lambda: a_ballpark, 2.5)
    step("the actor undocks", lambda: b.call_bound(b_service[0], "Undock", b_ship, False), 2.5)
    b_ballpark = b.call("beyonce", "MachoBindObject", system, None)
    step("the actor binds the ballpark", lambda: b_ballpark, 3.0)
    a_dogma = a.call("dogmaIM", "MachoBindObject", (system, 5), None)
    b_dogma = b.call("dogmaIM", "MachoBindObject", (system, 5), None)
    steps.append({"label": "both bind dogma in space", "ok": describe([a_dogma, b_dogma]), **watch(both, 1.0)})
    # a ship that has just undocked cannot be locked until its protection ends (thirty seconds)
    lock = lambda ship: (lambda: b.call_bound(b_dogma[0], "AddTarget", ship))  # noqa: E731
    got_lock = lambda seen: any(e["name"] == "OnTarget" for e in seen["events"])  # noqa: E731
    watcher_free = lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["seen"])  # noqa: E731
    steps.append({"label": "the protection of the watcher ends", **watch(both, 40.0, until=watcher_free)})
    # the actor's own protection has a few seconds to run: a lock before it ends
    step("the actor locks itself while it is protected", lock(b_ship))
    step("the actor locks the watcher while it is protected", lock(a_ship), 20.0, until=got_lock)
    step("the actor unlocks the watcher it locked while protected", lambda: b.call_bound(b_dogma[0], "RemoveTarget", a_ship), 2.0)
    steps.append({"label": "the protection of the actor ends", **watch(both, 6.0)})
    step("the actor asks what it has locked", lambda: b.call_bound(b_dogma[0], "GetTargets"))
    step("the watcher asks what has locked it", lambda: a.call_bound(a_dogma[0], "GetTargeters"))
    step("the actor locks itself", lock(b_ship))
    step("the actor locks a planet", lock(planet))
    step("the actor locks something that is not there", lock(99999999))
    step("the actor locks the watcher", lock(a_ship), 20.0, until=got_lock)
    step("the actor asks what it has locked with the watcher locked", lambda: b.call_bound(b_dogma[0], "GetTargets"))
    step("the watcher asks what has locked it with the actor locking", lambda: a.call_bound(a_dogma[0], "GetTargeters"))
    step("the actor locks the watcher again", lock(a_ship), 2.0)
    step("the actor unlocks the watcher", lambda: b.call_bound(b_dogma[0], "RemoveTarget", a_ship), 2.0)
    step("the actor locks the watcher to give up", lock(a_ship), 1.5)
    step("the actor gives up the lock", lambda: b.call_bound(b_dogma[0], "CancelAddTarget", a_ship), 2.0)
    step("the actor locks the station", lock(station), 20.0, until=got_lock)
    step("the actor locks the watcher after the station", lock(a_ship), 20.0, until=got_lock)
    step("the actor unlocks the station with a list", lambda: b.call_bound(b_dogma[0], "RemoveTargets", m.List([station])), 2.0)
    step("the actor locks the station again", lock(station), 20.0, until=got_lock)
    step("the actor clears its targets", lambda: b.call_bound(b_dogma[0], "ClearTargets"), 2.0)
    step("the actor asks what it has locked after clearing", lambda: b.call_bound(b_dogma[0], "GetTargets"))
    # a ship that is locked docks: the one that locked it is told it is gone
    step("the actor locks the watcher before it docks", lock(a_ship), 20.0, until=got_lock)
    step("the watcher docks", lambda: a.call_bound(a_ballpark[0], "CmdDock", station), 9.0, until=lambda seen: any(e["name"] == "$sessionchange" for e in seen["seen"]))
    step("the actor asks what it has locked after the watcher docked", lambda: b.call_bound(b_dogma[0], "GetTargets"))

    # the actor warps to a planet with the station locked, tries to lock in warp and, when it is far away, from there
    step("the actor locks the station before warping", lock(station), 20.0, until=got_lock)
    warp = {"label": "the actor warps to a planet"}
    try:
        warp["ok"] = describe(b.call_bound(b_ballpark[0], "CmdWarpToStuff", "item", planet, minRange=0))
    except p.MachoError as error:
        warp["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
    warp.update(watch(both, 14.0))
    steps.append(warp)
    step("the actor locks the station in warp", lock(station), 2.0)
    started = time.time()
    seen_both: dict = {"events": [], "seen": []}
    while time.time() - started < 150:
        got = watch(both, 3.0)
        for name in seen_both:
            for event in got[name]:
                event["at"] = round(event["at"] + (time.time() - started) - 3.0, 2)
            seen_both[name] += [e for e in got[name] if not (e["name"] == "DoDestinyUpdate" and dest_names(e) == ["SetState"])]
        if any("Stop" in dest_names(e) for e in seen_both["events"]):
            break
    steps.append({"label": "the actor arrives", **seen_both})
    step("the actor locks the station from far away", lock(station), 2.0)
    race = {"bloodlineID": 1, "raceID": 1, "solarSystemID": system, "stationID": station, "planetID": planet,
            "watcher": {"create": a_created, "characterID": a_char, "shipID": a_ship}, "actor": {"create": b_created, "characterID": b_char, "shipID": b_ship},
            "steps": steps}
    with open(os.path.join(out, "target.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [race]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    a.close()
    b.close()


def record_module(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """Two characters in space by the same station: what the one that works its modules (the actor, B) and the one that watches (A) are sent as
    the actor flies, starts and stops its afterburner, and tries its gun and its miner where they cannot be used. Each step holds what B is sent
    (`events`) and what A is sent (`seen`), with the seconds each arrived at."""
    system, station = 30100032, 60015249

    def character(name: str):
        who = rec.login(name)
        created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"{name} {run}", 1, 1, ancestry[1]))
        char_id = int(created["ok"])
        rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
        manager = who.call("invbroker", "MachoBindObject", (station, 15), None)[0]
        hangar = who.call_bound(manager, "GetInventory", 10004)
        ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
        who.call("dogmaIM", "MachoBindObject", (station, 15), ("GetAllInfo", (True, True, None), None))
        events_of(who)
        return who, created, char_id, ship_id

    a, a_created, a_char, a_ship = character("Watcher")
    b, b_created, b_char, b_ship = character("Actor")
    both = {"events": b, "seen": a}
    steps: list = []

    def step(label, call, seconds=2.0, until=None):
        entry = {"label": label}
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry.update(watch(both, seconds, until))
        steps.append(entry)
        return entry

    a_service = a.call("ship", "MachoBindObject", (station, 15), None)
    b_service = b.call("ship", "MachoBindObject", (station, 15), None)
    watch(both, 0.5)
    step("the watcher undocks", lambda: a.call_bound(a_service[0], "Undock", a_ship, False), 2.0)
    a_ballpark = a.call("beyonce", "MachoBindObject", system, None)
    step("the watcher binds the ballpark", lambda: a_ballpark, 2.5)
    step("the actor undocks", lambda: b.call_bound(b_service[0], "Undock", b_ship, False), 2.5)
    b_ballpark = b.call("beyonce", "MachoBindObject", system, None)
    step("the actor binds the ballpark", lambda: b_ballpark, 3.0)
    a_dogma = a.call("dogmaIM", "MachoBindObject", (system, 5), None)
    b_dogma = b.call("dogmaIM", "MachoBindObject", (system, 5), None)
    steps.append({"label": "both bind dogma in space", "ok": describe([a_dogma, b_dogma]), **watch(both, 1.0)})
    info = b.call_bound(b_dogma[0], "GetAllInfo", True, True, None)
    described = describe(info)
    ship_info = {k: v for k, v in described["args"]["$dict"]}["shipInfo"]["$dict"]
    fitted = {}
    for key, value in ship_info:
        item = {k: v for k, v in value["args"]["$dict"]}["invItem"]["$row"]
        fitted[item["typeID"]] = int(key["$long"] if isinstance(key, dict) else key)
    afterburner, gun, miner = fitted[21857], fitted[3638], fitted[3651]
    steps.append({"label": "the actor asks for its ship", "ok": described, **watch(both, 1.0)})
    steps.append({"label": "both wait for the protection to end", **watch(both, 40.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))})

    fly = lambda name, *args: (lambda: b.call_bound(b_ballpark[0], name, *args))  # noqa: E731
    activate = lambda module, effect, target=None: (lambda: b.call_bound(b_dogma[0], "Activate", module, effect, target, 1000))  # noqa: E731
    stopped = lambda seen: any(e["name"] == "OnGodmaShipEffect" and len(args_of_effect(e)) > 3 and args_of_effect(e)[3] == 0 for e in seen["events"])  # noqa: E731
    step("the actor flies at full speed", fly("CmdGotoDirection", 1.0, 0.0, 0.0), 3.0)
    step("the actor starts the afterburner", activate(afterburner, "moduleBonusAfterburner"), 13.0)
    step("the actor starts the afterburner again", activate(afterburner, "moduleBonusAfterburner"), 1.0)
    step("the actor stops the afterburner", lambda: b.call_bound(b_dogma[0], "Deactivate", afterburner, "moduleBonusAfterburner"), 12.0, until=stopped)
    step("the actor stops the afterburner again", lambda: b.call_bound(b_dogma[0], "Deactivate", afterburner, "moduleBonusAfterburner"), 2.0)
    step("the actor fires the gun with no target", activate(gun, "targetAttack"), 2.0)
    step("the actor locks the watcher", lambda: b.call_bound(b_dogma[0], "AddTarget", a_ship), 8.0, until=lambda seen: any(e["name"] == "OnTarget" for e in seen["events"]))
    step("the actor fires the gun at the watcher without a charge", activate(gun, "targetAttack", a_ship), 3.0)
    step("the actor works the miner on the watcher", activate(miner, "miningLaser", a_ship), 3.0)
    step("the actor works the miner with no target", activate(miner, "miningLaser"), 2.0)
    race = {"bloodlineID": 1, "raceID": 1, "solarSystemID": system, "stationID": station,
            "modules": {"afterburner": afterburner, "gun": gun, "miner": miner},
            "watcher": {"create": a_created, "characterID": a_char, "shipID": a_ship}, "actor": {"create": b_created, "characterID": b_char, "shipID": b_ship},
            "steps": steps}
    with open(os.path.join(out, "module.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [race]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    a.close()
    b.close()


def slims_in(events: list) -> list[tuple[int, int]]:
    """The (itemID, typeID) of every slim item an event holds (the balls an `AddBalls2` brings into space), in order."""
    found: list[tuple[int, int]] = []
    for event in events:
        for item, kind in re.findall(r'\["itemID", \{"\$long": (\d+)\}\], \["typeID", (\d+)\]', json.dumps(event)):
            found.append((int(item), int(kind)))
    return found


def record_weapon(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """One character in space with a rat beside it (`/npc`, the reference's way to make a target that may be shot: a ship of another pilot cannot
    be, in the empire, with the safety on): what the pilot is sent as it fires its gun (the Civilian Gatling Railgun, which needs no charge: its own
    damage is the shot's) at a target it has not locked, at one it has, stops the gun, starts it again and lets the lock go while it runs. Each step
    holds what the pilot is sent (`events`), with the seconds each arrived at."""
    system, station = 30100032, 60015249
    who = rec.login("Gunner")
    created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Gunner {run}", 1, 1, ancestry[1]))
    char_id = int(created["ok"])
    rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
    manager = who.call("invbroker", "MachoBindObject", (station, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
    who.call("dogmaIM", "MachoBindObject", (station, 15), ("GetAllInfo", (True, True, None), None))
    events_of(who)
    only = {"events": who}
    steps: list = []

    def step(label, call, seconds=2.0, until=None):
        entry = {"label": label}
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry["events"] = watch(only, seconds, (lambda seen: until(seen["events"])) if until else None)["events"]
        steps.append(entry)
        return entry

    service = who.call("ship", "MachoBindObject", (station, 15), None)
    watch(only, 0.5)
    step("the pilot undocks", lambda: who.call_bound(service[0], "Undock", ship_id, False), 2.5)
    ballpark = who.call("beyonce", "MachoBindObject", system, None)
    step("the pilot binds the ballpark", lambda: ballpark, 3.0)
    dogma = who.call("dogmaIM", "MachoBindObject", (system, 5), None)
    step("the pilot binds dogma in space", lambda: dogma, 1.0)
    info = who.call_bound(dogma[0], "GetAllInfo", True, True, None)
    described = describe(info)
    ship_info = {k: v for k, v in described["args"]["$dict"]}["shipInfo"]["$dict"]
    fitted = {}
    for key, value in ship_info:
        item = {k: v for k, v in value["args"]["$dict"]}["invItem"]["$row"]
        fitted[item["typeID"]] = int(key["$long"] if isinstance(key, dict) else key)
    gun = fitted[3638]
    steps.append({"label": "the pilot asks for its ship", "ok": described, "events": watch(only, 1.0)["events"]})
    steps.append({"label": "the pilot waits for the protection to end", "events": watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))["events"]})

    slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731
    activate = lambda target=None: (lambda: who.call_bound(dogma[0], "Activate", gun, "targetAttack", target, 1000))  # noqa: E731
    stopped = lambda seen: any(e["name"] == "OnGodmaShipEffect" and len(args_of_effect(e)) > 3 and args_of_effect(e)[3] == 0 for e in seen)  # noqa: E731
    got_lock = lambda seen: any(e["name"] == "OnTarget" for e in seen)  # noqa: E731
    spawn = step("the pilot calls a rat", slash("/npc 1 Blood Visionary"), 8.0)
    npc = next((item for item, kind in slims_in(spawn["events"]) if item != ship_id), 0)
    if not npc:
        raise SystemExit("the reference brought no rat into space")
    step("the pilot fires the gun at the rat before locking it", activate(npc), 2.0)
    step("the pilot locks the rat", lambda: who.call_bound(dogma[0], "AddTarget", npc), 20.0, until=got_lock)
    step("the pilot heals", slash("/heal"), 1.0)
    step("the pilot fires the gun at the rat", activate(npc), 14.0)
    step("the pilot fires the gun again", activate(npc), 1.0)
    step("the pilot stops the gun", lambda: who.call_bound(dogma[0], "Deactivate", gun, "targetAttack"), 6.0, until=stopped)
    step("the pilot stops the gun again", lambda: who.call_bound(dogma[0], "Deactivate", gun, "targetAttack"), 1.5)
    step("the pilot heals again", slash("/heal"), 1.0)
    step("the pilot fires the gun to lose the lock", activate(npc), 4.0)
    step("the pilot lets the lock go while the gun runs", lambda: who.call_bound(dogma[0], "RemoveTarget", npc), 8.0, until=stopped)
    step("the pilot asks what it has locked", lambda: who.call_bound(dogma[0], "GetTargets"), 1.0)
    race = {"bloodlineID": 1, "raceID": 1, "solarSystemID": system, "stationID": station, "create": created, "characterID": char_id, "shipID": ship_id,
            "modules": {"gun": gun}, "npcID": npc, "steps": steps}
    with open(os.path.join(out, "weapon.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [race]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    who.close()


def record_ammo(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """One character with a gun that takes charges (a 125mm Railgun I, in the place of the miner): ammunition given, the gun loaded, loaded again, unloaded,
    loaded and then given another kind (Iron Charge S, three rounds: the antimatter goes back to the hangar), the ship asked for with a charge in the gun, then in
    space with a rat beside it (`/npc`) the gun fired until the three rounds are spent and fired again with none. Each step holds the answer (or the error) and what
    the pilot was sent, with the seconds each arrived at."""
    system, station = 30100032, 60015249
    who = rec.login("Loader")
    created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Loader {run}", 1, 1, ancestry[1]))
    char_id = int(created["ok"])
    rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
    manager = who.call("invbroker", "MachoBindObject", (station, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
    who.call("dogmaIM", "MachoBindObject", (station, 15), ("GetAllInfo", (True, True, None), None))
    events_of(who)
    only = {"events": who}
    steps: list = []

    def step(label, call, seconds=1.5, until=None):
        entry = {"label": label}
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry["events"] = watch(only, seconds, (lambda seen: until(seen["events"])) if until else None)["events"]
        steps.append(entry)
        return entry

    slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731
    step("the pilot is given antimatter", slash("/giveitem Antimatter Charge S 500"))
    step("the pilot takes the miner out", slash("/unfit me Civilian Miner"))
    step("the pilot fits a railgun", slash("/fit me 125mm Railgun I"))

    def hangar_rows():
        return [row.fields() for row in who.call_bound(hangar, "List")]

    def ids_by_type() -> dict:
        return {int(row["typeID"]): int(row["itemID"]) for row in hangar_rows()}

    docked = who.call("dogmaIM", "MachoBindObject", (station, 15), None)

    def fitted_of(info) -> dict:
        described = describe(info)
        ship_info = {k: v for k, v in described["args"]["$dict"]}["shipInfo"]["$dict"]
        found = {}
        for key, value in ship_info:
            if isinstance(key, dict) and "$tuple" in key:
                continue
            item = {k: v for k, v in value["args"]["$dict"]}["invItem"]["$row"]
            found[item["typeID"]] = int(key["$long"] if isinstance(key, dict) else key)
        return found

    info = who.call_bound(docked[0], "GetAllInfo", True, True, None)
    fitted = fitted_of(info)
    gun = fitted[10678]
    stacks = ids_by_type()
    antimatter = stacks[222]
    steps.append({"label": "the pilot asks for its ship with the railgun empty", "ok": describe(info), "events": watch(only, 1.0)["events"]})
    load = lambda charge: (lambda: who.call_bound(docked[0], "LoadAmmo", ship_id, m.List([gun]), m.List([charge]), station))  # noqa: E731
    step("the pilot loads the railgun", load(antimatter), 2.0)
    step("the pilot loads the railgun again", load(antimatter), 2.0)
    step("the pilot lists its hangar with the railgun loaded", lambda: who.call_bound(hangar, "List"), 0.5)
    step("the pilot asks for its ship with the railgun loaded", lambda: who.call_bound(docked[0], "GetAllInfo", True, True, None), 1.5)
    step("the pilot unloads the railgun", lambda: who.call_bound(docked[0], "UnloadAmmo", ship_id, m.List([gun]), station, None), 2.0)
    step("the pilot loads the railgun for the flight", load(antimatter), 2.0)
    step("the pilot is given three rounds of iron", slash("/giveitem Iron Charge S 3"))
    iron = ids_by_type()[215]
    step("the pilot loads the iron in the place of the antimatter", load(iron), 2.0)

    service = who.call("ship", "MachoBindObject", (station, 15), None)
    step("the pilot undocks", lambda: who.call_bound(service[0], "Undock", ship_id, False), 2.5)
    ballpark = who.call("beyonce", "MachoBindObject", system, None)
    step("the pilot binds the ballpark", lambda: ballpark, 3.0)
    space = who.call("dogmaIM", "MachoBindObject", (system, 5), None)
    step("the pilot binds dogma in space", lambda: space, 1.0)
    step("the pilot asks for its ship in space", lambda: who.call_bound(space[0], "GetAllInfo", True, True, None), 1.0)
    steps.append({"label": "the pilot waits for the protection to end", "events": watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))["events"]})
    spawn = step("the pilot calls a rat", slash("/npc 1 Blood Visionary"), 8.0)
    rat = next((item for item, kind in slims_in(spawn["events"]) if item != ship_id), 0)
    if not rat:
        raise SystemExit("the reference brought no rat into space")
    lock = step("the pilot locks the rat", lambda: who.call_bound(space[0], "AddTarget", rat), 20.0, until=lambda seen: any(e["name"] == "OnTarget" and "otheradd" not in json.dumps(e) for e in seen))
    step("the pilot heals", slash("/heal"), 1.0)
    stopped = lambda seen: any(e["name"] == "OnGodmaShipEffect" and len(args_of_effect(e)) > 3 and args_of_effect(e)[3] == 0 for e in seen)  # noqa: E731
    step("the pilot fires the railgun until the iron is spent", lambda: who.call_bound(space[0], "Activate", gun, "projectileFired", rat, 1000), 20.0, until=stopped)
    step("the pilot fires the railgun with nothing in it", lambda: who.call_bound(space[0], "Activate", gun, "projectileFired", rat, 1000), 2.0)
    race = {"bloodlineID": 1, "raceID": 1, "solarSystemID": system, "stationID": station, "create": created, "characterID": char_id, "shipID": ship_id,
            "moduleIDs": [ship_id + 1, ship_id + 2, ship_id + 3], "roles": {"gun": gun, "antimatter": antimatter, "iron": iron, "rat": rat, "fitted": {str(k): v for k, v in fitted.items()}},
            "steps": steps}
    with open(os.path.join(out, "ammo.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [race]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    who.close()


def record_drones(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """One character and the drone bay of its ship (flag 87), as the client uses it: in the station the bay and the room in it asked for, a drone (Hobgoblin I, 5 m3: the
    bay of the rookie ship holds one) given and put in it, a second refused for want of room, a thing that is not a drone refused, the bay asked for, the drone taken back
    to the hangar; then in space the bay asked for. Each step holds the answer (or the error) and what the pilot was sent, with the seconds each arrived at."""
    system, station = 30100032, 60015249
    who = rec.login("Dronemaster")
    created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Dronemaster {run}", 1, 1, ancestry[1]))
    char_id = int(created["ok"])
    rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
    manager = who.call("invbroker", "MachoBindObject", (station, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
    who.call("dogmaIM", "MachoBindObject", (station, 15), ("GetAllInfo", (True, True, None), None))
    events_of(who)
    only = {"events": who}
    steps: list = []

    def step(label, call, seconds=1.0, until=None):
        entry = {"label": label}
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry["events"] = watch(only, seconds, (lambda seen: until(seen["events"])) if until else None)["events"]
        steps.append(entry)
        return entry

    slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731

    def stacks_of(handle, flag=None) -> dict:
        rows = who.call_bound(handle, "List", flag) if flag else who.call_bound(handle, "List")
        found = {}
        for row in (rows if isinstance(rows, list) else []):
            fields = row.fields()
            found.setdefault(int(fields["typeID"]), []).append(int(fields["itemID"]))
        return found

    def bay_in(place: tuple):
        return who.call("invbroker", "MachoBindObject", place, ("GetInventoryFromId", (ship_id, 0), None))[1]

    docked = bay_in((station, 15))
    step("the pilot asks for its empty drone bay", lambda: who.call_bound(docked, "List", 87))
    step("the pilot asks for the room in its drone bay", lambda: who.call_bound(docked, "GetCapacity", 87))
    step("the pilot is given two drones", slash("/giveitem Hobgoblin I 2"))
    drones = stacks_of(hangar)[2454][0]
    step("the pilot is given tritanium", slash("/giveitem Tritanium 10"))
    trit = stacks_of(hangar)[34][0]
    step("the pilot puts a drone in the drone bay", lambda: who.call_bound(docked, "Add", drones, station, qty=1, flag=87), 1.5)
    step("the pilot asks for its drone bay", lambda: who.call_bound(docked, "List", 87))
    step("the pilot asks for the room in its drone bay again", lambda: who.call_bound(docked, "GetCapacity", 87))
    rest = max(stacks_of(hangar)[2454])
    step("the pilot puts another drone in a drone bay that is full", lambda: who.call_bound(docked, "Add", rest, station, qty=1, flag=87), 1.5)
    step("the pilot puts tritanium in the drone bay", lambda: who.call_bound(docked, "Add", trit, station, qty=5, flag=87), 1.5)
    step("the pilot asks for its drone bay after the refusals", lambda: who.call_bound(docked, "List", 87))
    inside = stacks_of(docked, 87)[2454][0]
    step("the pilot takes the drone back to the hangar", lambda: who.call_bound(hangar, "Add", inside, ship_id, qty=1, flag=4), 1.5)
    step("the pilot asks for its drone bay at the end", lambda: who.call_bound(docked, "List", 87))
    step("the pilot puts a drone in the drone bay for the flight", lambda: who.call_bound(docked, "Add", drones, station, qty=1, flag=87), 1.5)

    service = who.call("ship", "MachoBindObject", (station, 15), None)
    step("the pilot undocks", lambda: who.call_bound(service[0], "Undock", ship_id, False), 2.5)
    ballpark = who.call("beyonce", "MachoBindObject", system, None)
    step("the pilot binds the ballpark", lambda: ballpark, 3.0)
    space = who.call("dogmaIM", "MachoBindObject", (system, 5), None)
    step("the pilot binds dogma in space", lambda: space, 1.0)
    step("the pilot asks for its ship in space", lambda: who.call_bound(space[0], "GetAllInfo", True, True, None), 1.0)
    steps.append({"label": "the pilot waits for the protection to end", "events": watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))["events"]})
    flying = bay_in((system, 5))
    step("the pilot asks for its drone bay in space", lambda: who.call_bound(flying, "List", 87))
    step("the pilot asks for the room in its drone bay in space", lambda: who.call_bound(flying, "GetCapacity", 87))
    with open(os.path.join(out, "drones.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [{"bloodlineID": 1, "raceID": 1, "solarSystemID": system, "stationID": station, "create": created, "characterID": char_id, "shipID": ship_id, "moduleIDs": [ship_id + 1, ship_id + 2, ship_id + 3], "steps": steps}]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    who.close()


def record_reload(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """One character with a gun that takes charges (a 125mm Railgun I in the place of the miner), loaded in the station with some more ammunition in the hold, then in
    space with a rat beside it (`/npc`): the gun unloaded into the hold, loaded again from it (the reference makes the pilot wait the gun's reload time: what it tells
    the pilot of the wait and of the end of it is what is recorded), loaded when it is full, fired until its rounds are spent (with more in the hold the reference
    reloads by itself) and asked for after, and fired while a reload runs. Each step holds the answer (or the error) and what the pilot was sent, with the seconds
    each arrived at."""
    system, station = 30100032, 60015249
    who = rec.login("Reloader")
    created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Reloader {run}", 1, 1, ancestry[1]))
    char_id = int(created["ok"])
    rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
    manager = who.call("invbroker", "MachoBindObject", (station, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
    who.call("dogmaIM", "MachoBindObject", (station, 15), ("GetAllInfo", (True, True, None), None))
    events_of(who)
    only = {"events": who}
    steps: list = []

    def step(label, call, seconds=1.5, until=None):
        entry = {"label": label}
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry["events"] = watch(only, seconds, (lambda seen: until(seen["events"])) if until else None)["events"]
        steps.append(entry)
        return entry

    slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731
    step("the pilot is given antimatter", slash("/giveitem Antimatter Charge S 500"))
    step("the pilot takes the miner out", slash("/unfit me Civilian Miner"))
    step("the pilot fits a railgun", slash("/fit me 125mm Railgun I"))

    def ids_by_type(handle, flag=None) -> dict:
        rows = who.call_bound(handle, "List", flag) if flag else who.call_bound(handle, "List")
        return {int(row.fields()["typeID"]): int(row.fields()["itemID"]) for row in (rows if isinstance(rows, list) else [])}

    docked = who.call("dogmaIM", "MachoBindObject", (station, 15), None)
    info = who.call_bound(docked[0], "GetAllInfo", True, True, None)
    described = describe(info)
    ship_info = {k: v for k, v in described["args"]["$dict"]}["shipInfo"]["$dict"]
    fitted = {}
    for key, value in ship_info:
        if isinstance(key, dict) and "$tuple" in key:
            continue
        item = {k: v for k, v in value["args"]["$dict"]}["invItem"]["$row"]
        fitted[item["typeID"]] = int(key["$long"] if isinstance(key, dict) else key)
    gun = fitted[10678]
    antimatter = ids_by_type(hangar)[222]
    hold_docked = who.call("invbroker", "MachoBindObject", (station, 15), ("GetInventoryFromId", (ship_id, 0), None))[1]
    load = lambda charge, where: (lambda: who.call_bound(docked[0], "LoadAmmo", ship_id, m.List([gun]), m.List([charge]), where))  # noqa: E731
    step("the pilot loads the railgun in the station", load(antimatter, station), 2.0)
    step("the pilot puts more antimatter in the hold", lambda: who.call_bound(hold_docked, "Add", ids_by_type(hangar)[222], station, qty=40, flag=5), 1.5)

    service = who.call("ship", "MachoBindObject", (station, 15), None)
    step("the pilot undocks", lambda: who.call_bound(service[0], "Undock", ship_id, False), 2.5)
    ballpark = who.call("beyonce", "MachoBindObject", system, None)
    step("the pilot binds the ballpark", lambda: ballpark, 3.0)
    space = who.call("dogmaIM", "MachoBindObject", (system, 5), None)
    step("the pilot binds dogma in space", lambda: space, 1.0)
    step("the pilot asks for its ship in space", lambda: who.call_bound(space[0], "GetAllInfo", True, True, None), 1.0)
    steps.append({"label": "the pilot waits for the protection to end", "events": watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))["events"]})
    hold = who.call("invbroker", "MachoBindObject", (system, 5), ("GetInventoryFromId", (ship_id, 0), None))[1]
    spawn = step("the pilot calls a rat", slash("/npc 1 Blood Visionary"), 8.0)
    rat = next((item for item, kind in slims_in(spawn["events"]) if item != ship_id), 0)
    if not rat:
        raise SystemExit("the reference brought no rat into space")
    step("the pilot locks the rat", lambda: who.call_bound(space[0], "AddTarget", rat), 20.0, until=lambda seen: any(e["name"] == "OnTarget" and "otheradd" not in json.dumps(e) for e in seen))
    step("the pilot heals", slash("/heal"), 1.0)
    step("the pilot asks for its hold in space", lambda: who.call_bound(hold, "List", 5), 0.5)
    step("the pilot unloads the railgun into the hold", lambda: who.call_bound(space[0], "UnloadAmmo", ship_id, m.List([gun]), ship_id, None), 2.0)
    step("the pilot asks for its hold with the rounds back", lambda: who.call_bound(hold, "List", 5), 0.5)
    in_hold = ids_by_type(hold, 5)[222]
    reloaded = lambda seen: sum(1 for e in seen if e["name"] == "OnChargeBeingLoadedToModule") >= 1 and sum(1 for e in seen if e["name"] == "OnModuleAttributeChanges") >= 4  # noqa: E731
    step("the pilot loads the railgun from the hold", lambda: who.call_bound(space[0], "LoadAmmo", ship_id, m.List([gun]), m.List([in_hold]), ship_id), 12.0)
    step("the pilot asks for its hold after the reload", lambda: who.call_bound(hold, "List", 5), 0.5)
    step("the pilot loads the railgun when it is full", lambda: who.call_bound(space[0], "LoadAmmo", ship_id, m.List([gun]), m.List([in_hold]), ship_id), 3.0)
    stopped = lambda seen: any(e["name"] == "OnGodmaShipEffect" and len(args_of_effect(e)) > 3 and args_of_effect(e)[3] == 0 for e in seen)  # noqa: E731
    step("the pilot heals again", slash("/heal"), 1.0)
    step("the pilot unloads most of the rounds", lambda: who.call_bound(space[0], "UnloadAmmo", ship_id, m.List([gun]), ship_id, 75), 2.0)
    step("the pilot fires the railgun until the rounds are spent", lambda: who.call_bound(space[0], "Activate", gun, "projectileFired", rat, 1000), 45.0, until=stopped)
    step("the pilot stops the railgun", lambda: who.call_bound(space[0], "Deactivate", gun, "projectileFired"), 3.0)
    step("the pilot asks for its hold after the fight", lambda: who.call_bound(hold, "List", 5), 0.5)
    step("the pilot heals once more", slash("/heal"), 1.0)
    step("the pilot unloads what is left in the railgun", lambda: who.call_bound(space[0], "UnloadAmmo", ship_id, m.List([gun]), ship_id, None), 2.0)
    in_hold = ids_by_type(hold, 5)[222]
    step("the pilot loads the railgun again", lambda: who.call_bound(space[0], "LoadAmmo", ship_id, m.List([gun]), m.List([in_hold]), ship_id), 0.8)
    step("the pilot fires the railgun while it reloads", lambda: who.call_bound(space[0], "Activate", gun, "projectileFired", rat, 1000), 8.0)
    step("the pilot unloads most of the rounds again", lambda: who.call_bound(space[0], "UnloadAmmo", ship_id, m.List([gun]), ship_id, 75), 2.0)
    step("the pilot tops the railgun up", lambda: who.call_bound(space[0], "LoadAmmo", ship_id, m.List([gun]), m.List([in_hold]), ship_id), 0.8)
    step("the pilot fires the railgun while it tops up", lambda: who.call_bound(space[0], "Activate", gun, "projectileFired", rat, 1000), 9.0)
    step("the pilot stops the railgun after the top up", lambda: who.call_bound(space[0], "Deactivate", gun, "projectileFired"), 4.0)
    step("the pilot asks for its hold at the end", lambda: who.call_bound(hold, "List", 5), 0.5)
    race = {"bloodlineID": 1, "raceID": 1, "solarSystemID": system, "stationID": station, "create": created, "characterID": char_id, "shipID": ship_id,
            "moduleIDs": [ship_id + 1, ship_id + 2, ship_id + 3],
            "roles": {"gun": gun, "antimatter": antimatter, "rat": rat, "fitted": {str(k): v for k, v in fitted.items()}}, "steps": steps}
    with open(os.path.join(out, "reload.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [race]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    who.close()


def record_cargo(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """One character and the cargo hold of its ship, as the client uses it: in the station a stack of tritanium given, asked for, moved into the hold (all of it,
    part of it, more than the hold takes), stacked, moved back to the hangar; then in space the hold asked for, a stack jettisoned, the can that comes of it
    opened and what is in it taken back. Each step holds the answer (or the error) and what the pilot was sent, with the seconds each arrived at. The inventory
    is bound the way the client binds it: `invbroker.MachoBindObject((place, group), ("GetInventoryFromId", (itemID, 0)))`, the place being the station (group 15)
    or the solar system (group 5)."""
    system, station = 30100032, 60015249
    who = rec.login("Hauler")
    created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Hauler {run}", 1, 1, ancestry[1]))
    char_id = int(created["ok"])
    rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
    manager = who.call("invbroker", "MachoBindObject", (station, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
    who.call("dogmaIM", "MachoBindObject", (station, 15), ("GetAllInfo", (True, True, None), None))
    events_of(who)
    only = {"events": who}
    steps: list = []

    def step(label, call, seconds=1.0, until=None):
        entry = {"label": label}
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry["events"] = watch(only, seconds, (lambda seen: until(seen["events"])) if until else None)["events"]
        steps.append(entry)
        return entry

    slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731

    def cargo_in(place: tuple):
        return who.call("invbroker", "MachoBindObject", place, ("GetInventoryFromId", (ship_id, 0), None))[1]

    def stacks_of(handle, flag=None) -> dict:
        rows = who.call_bound(handle, "List", flag) if flag else who.call_bound(handle, "List")
        found = {}
        for row in (rows if isinstance(rows, list) else []):
            fields = row.fields()
            found.setdefault(int(fields["typeID"]), []).append(int(fields["itemID"]))
        return found

    docked = cargo_in((station, 15))
    step("the pilot asks for its empty hold", lambda: who.call_bound(docked, "List", 5))
    step("the pilot asks for the room in its hold", lambda: who.call_bound(docked, "GetCapacity", 5))
    step("the pilot is given tritanium", slash("/giveitem Tritanium 100"))
    trit = stacks_of(hangar)[34][0]
    step("the pilot puts all the tritanium in the hold", lambda: who.call_bound(docked, "Add", trit, station, qty=100, flag=5), 1.5)
    step("the pilot asks for its hold", lambda: who.call_bound(docked, "List", 5))
    step("the pilot asks for the room in its hold again", lambda: who.call_bound(docked, "GetCapacity", 5))
    step("the pilot is given more tritanium", slash("/giveitem Tritanium 40"))
    more = [i for i in stacks_of(hangar)[34] if i != trit][0] if len(stacks_of(hangar).get(34, [])) > 1 else stacks_of(hangar)[34][0]
    step("the pilot puts part of it in the hold", lambda: who.call_bound(docked, "Add", more, station, qty=15, flag=5), 1.5)
    step("the pilot asks for its hold with two stacks", lambda: who.call_bound(docked, "List", 5))
    step("the pilot stacks its hold", lambda: who.call_bound(docked, "StackAll", 5), 1.5)
    step("the pilot asks for its hold stacked", lambda: who.call_bound(docked, "List", 5))
    step("the pilot is given more than the hold takes", slash("/giveitem Tritanium 20000"))
    big = max(stacks_of(hangar)[34], key=lambda i: i)
    step("the pilot puts more than the hold takes in it", lambda: who.call_bound(docked, "Add", big, station, qty=20000, flag=5), 1.5)
    step("the pilot asks for the room in its hold at the end", lambda: who.call_bound(docked, "GetCapacity", 5))
    inside = stacks_of(docked, 5)[34][0]
    step("the pilot takes some back to the hangar", lambda: who.call_bound(hangar, "Add", inside, ship_id, qty=20, flag=4), 1.5)
    step("the pilot asks for its hold after", lambda: who.call_bound(docked, "List", 5))

    service = who.call("ship", "MachoBindObject", (station, 15), None)
    step("the pilot undocks", lambda: who.call_bound(service[0], "Undock", ship_id, False), 2.5)
    ballpark = who.call("beyonce", "MachoBindObject", system, None)
    step("the pilot binds the ballpark", lambda: ballpark, 3.0)
    space = who.call("dogmaIM", "MachoBindObject", (system, 5), None)
    step("the pilot binds dogma in space", lambda: space, 1.0)
    step("the pilot asks for its ship in space", lambda: who.call_bound(space[0], "GetAllInfo", True, True, None), 1.0)
    steps.append({"label": "the pilot waits for the protection to end", "events": watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))["events"]})
    flying = cargo_in((system, 5))
    step("the pilot asks for its hold in space", lambda: who.call_bound(flying, "List", 5))
    step("the pilot asks for the room in its hold in space", lambda: who.call_bound(flying, "GetCapacity", 5))
    held = stacks_of(flying, 5).get(34, [])
    ship_service = who.call("ship", "MachoBindObject", system, None)
    jettison = step("the pilot jettisons a stack", lambda: who.call_bound(ship_service[0], "Jettison", m.List(held[:1])), 4.0)
    can = next((item for item, kind in slims_in(jettison["events"]) if item != ship_id), 0)
    if can:
        opened = who.call("invbroker", "MachoBindObject", (system, 5), ("GetInventoryFromId", (can, 0), None))[1]
        step("the pilot opens the can", lambda: who.call_bound(opened, "List"))
        step("the pilot asks for the room in the can", lambda: who.call_bound(opened, "GetCapacity"))
        inside_can = stacks_of(opened).get(34, [0])[0]
        step("the pilot takes what is in the can back", lambda: who.call_bound(flying, "Add", inside_can, can, qty=25, flag=5), 1.5)
        step("the pilot asks for its hold after the can", lambda: who.call_bound(flying, "List", 5))
        step("the pilot asks for the can after", lambda: who.call_bound(opened, "List"))
    with open(os.path.join(out, "cargo.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [{"bloodlineID": 1, "raceID": 1, "solarSystemID": system, "stationID": station, "create": created, "characterID": char_id, "shipID": ship_id, "canID": can, "steps": steps}]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    who.close()


def record_loot(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """One character in space takes what is in a can with the loot window's "Loot All" (`invbroker` `MultiAdd` on the ship's inventory: the item ids, the can they are
    in, `flag=5`): two stacks (tritanium and pyerite) are put in the hold, jettisoned into one can, the can is opened and everything is taken at once
    (not yet recorded: a batch that is too much for what is left of the hold, and one that names an item that is not in the can any more).
    Each step holds the answer (or the error) and what the pilot was sent, with the seconds each arrived at."""
    system, station = 30100032, 60015249
    who = rec.login("Looter")
    created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Looter {run}", 1, 1, ancestry[1]))
    char_id = int(created["ok"])
    rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
    manager = who.call("invbroker", "MachoBindObject", (station, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
    who.call("dogmaIM", "MachoBindObject", (station, 15), ("GetAllInfo", (True, True, None), None))
    events_of(who)
    only = {"events": who}
    steps: list = []

    def step(label, call, seconds=1.0, until=None):
        entry = {"label": label}
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry["events"] = watch(only, seconds, (lambda seen: until(seen["events"])) if until else None)["events"]
        steps.append(entry)
        return entry

    slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731

    def cargo_in(place: tuple):
        return who.call("invbroker", "MachoBindObject", place, ("GetInventoryFromId", (ship_id, 0), None))[1]

    def stacks_of(handle, flag=None) -> dict:
        rows = who.call_bound(handle, "List", flag) if flag else who.call_bound(handle, "List")
        found = {}
        for row in (rows if isinstance(rows, list) else []):
            fields = row.fields()
            found.setdefault(int(fields["typeID"]), []).append(int(fields["itemID"]))
        return found

    docked = cargo_in((station, 15))
    step("the pilot is given tritanium", slash("/giveitem Tritanium 100"))
    step("the pilot is given pyerite", slash("/giveitem Pyerite 60"))
    trit, pye = stacks_of(hangar)[34][0], stacks_of(hangar)[35][0]
    step("the pilot puts the tritanium in the hold", lambda: who.call_bound(docked, "Add", trit, station, qty=100, flag=5), 1.5)
    step("the pilot puts the pyerite in the hold", lambda: who.call_bound(docked, "Add", pye, station, qty=60, flag=5), 1.5)
    service = who.call("ship", "MachoBindObject", (station, 15), None)
    step("the pilot undocks", lambda: who.call_bound(service[0], "Undock", ship_id, False), 2.5)
    ballpark = who.call("beyonce", "MachoBindObject", system, None)
    step("the pilot binds the ballpark", lambda: ballpark, 3.0)
    space = who.call("dogmaIM", "MachoBindObject", (system, 5), None)
    step("the pilot binds dogma in space", lambda: space, 1.0)
    step("the pilot asks for its ship in space", lambda: who.call_bound(space[0], "GetAllInfo", True, True, None), 1.0)
    steps.append({"label": "the pilot waits for the protection to end", "events": watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))["events"]})
    flying = cargo_in((system, 5))
    step("the pilot asks for its hold in space", lambda: who.call_bound(flying, "List", 5))
    held = stacks_of(flying, 5)
    ship_service = who.call("ship", "MachoBindObject", system, None)
    jettison = step("the pilot jettisons both stacks", lambda: who.call_bound(ship_service[0], "Jettison", m.List([held[34][0], held[35][0]])), 4.0)
    can = next((item for item, kind in slims_in(jettison["events"]) if item != ship_id), 0)
    if can:
        opened = who.call("invbroker", "MachoBindObject", (system, 5), ("GetInventoryFromId", (can, 0), None))[1]
        step("the pilot opens the can", lambda: who.call_bound(opened, "List"))
        step("the pilot asks for the room in the can", lambda: who.call_bound(opened, "GetCapacity"))
        inside = stacks_of(opened)
        ids = [inside[34][0], inside[35][0]]
        step("the pilot loots everything", lambda: who.call_bound(flying, "MultiAdd", m.List(ids), can, flag=5), 2.0)
        step("the pilot asks for its hold after", lambda: who.call_bound(flying, "List", 5))
        step("the pilot asks for the room in its hold after", lambda: who.call_bound(flying, "GetCapacity", 5))
        step("the pilot asks for the can after", lambda: who.call_bound(opened, "List"))
    with open(os.path.join(out, "loot.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [{"bloodlineID": 1, "raceID": 1, "solarSystemID": system, "stationID": station, "create": created, "characterID": char_id, "shipID": ship_id, "canID": can, "steps": steps}]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    who.close()


def record_health(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """One character damages its ship in the hangar (`/dmg`, the reference's only way to hurt a ship on demand), asks for it, leaves the station
    with the damage, watches its shield and capacitor recharge, heals in space and tries to damage again there. Each step holds the answer (or
    the error) and what the client was sent, with the seconds each arrived at."""
    system, station = 30100032, 60015249
    who = rec.login("Damaged")
    created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"Damaged {run}", 1, 1, ancestry[1]))
    char_id = int(created["ok"])
    rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
    manager = who.call("invbroker", "MachoBindObject", (station, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
    who.call("dogmaIM", "MachoBindObject", (station, 15), ("GetAllInfo", (True, True, None), None))
    events_of(who)
    only = {"events": who}
    steps: list = []

    def step(label, call, seconds=1.5, until=None):
        entry = {"label": label}
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry["events"] = watch(only, seconds, (lambda seen: until(seen["events"])) if until else None)["events"]
        steps.append(entry)
        return entry

    docked = who.call("dogmaIM", "MachoBindObject", (station, 15), None)
    info = lambda: who.call_bound(docked[0], "GetAllInfo", True, True, None)  # noqa: E731
    slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731
    step("the pilot asks for its ship", info)
    step("light damage in the hangar", slash("/dmg light"), 1.5)
    step("the pilot asks for its damaged ship", info)
    step("heavy damage in the hangar", slash("/dmg heavy"), 1.5)
    step("the pilot asks for its badly damaged ship", info)
    step("a heal in the hangar", slash("/heal"), 1.5)
    step("the pilot asks for its healed ship", info)
    step("heavy damage before the undock", slash("/dmg heavy"), 1.5)
    ship = who.call("ship", "MachoBindObject", (station, 15), None)
    step("the damaged ship undocks", lambda: who.call_bound(ship[0], "Undock", ship_id, False, onlineModules=m.List([ship_id + 1, ship_id + 2, ship_id + 3])), 2.5)
    ballpark = who.call("beyonce", "MachoBindObject", system, None)
    step("the pilot binds the ballpark", lambda: ballpark, 3.0)
    space = who.call("dogmaIM", "MachoBindObject", (system, 5), None)
    step("the pilot binds dogma in space", lambda: space, 1.0)
    step("the pilot asks for its damaged ship in space", lambda: who.call_bound(space[0], "GetAllInfo", True, True, None), 1.0)
    step("the shield and the capacitor recharge", lambda: None, 20.0)
    step("damage in space", slash("/dmg light"), 2.0)
    step("a heal in space", slash("/heal"), 3.0)
    step("the healed ship stays as it is", lambda: None, 3.0)
    race = {"bloodlineID": 1, "raceID": 1, "solarSystemID": system, "stationID": station, "create": created, "characterID": char_id, "shipID": ship_id,
            "moduleIDs": [ship_id + 1, ship_id + 2, ship_id + 3], "steps": steps}
    with open(os.path.join(out, "health.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [race]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    who.close()


def record_death(rec: Recorder, out: str, meta: dict, ancestry: dict, run: str) -> None:
    """Two characters in space by the same station: what both are sent when a hull that nobody flies is detonated beside them (`/deathtest`, the
    reference's way to make a ship die on demand: the explosion, the ball that goes, the wreck that comes) and when the one of them (the victim,
    B) destroys its own ship (`/suicide`: the same, and the capsule the pilot is left in) and then the capsule (the pilot wakes in its station). Each step holds what B is sent (`events`) and what A
    is sent (`seen`), with the seconds each arrived at."""
    system, station = 30100032, 60015249

    def character(name: str):
        who = rec.login(name)
        created = rec.call(who, "charUnboundMgr", "CreateCharacterWithDoll", *create_args(f"{name} {run}", 1, 1, ancestry[1]))
        char_id = int(created["ok"])
        rec.call(who, "charUnboundMgr", "SelectCharacterID", char_id, None, False)
        manager = who.call("invbroker", "MachoBindObject", (station, 15), None)[0]
        hangar = who.call_bound(manager, "GetInventory", 10004)
        ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
        who.call("dogmaIM", "MachoBindObject", (station, 15), ("GetAllInfo", (True, True, None), None))
        events_of(who)
        return who, created, char_id, ship_id

    a, a_created, a_char, a_ship = character("Watcher")
    b, b_created, b_char, b_ship = character("Victim")
    both = {"events": b, "seen": a}
    steps: list = []

    def step(label, call, seconds=2.0, until=None):
        entry = {"label": label}
        try:
            entry["ok"] = describe(call())
        except p.MachoError as error:
            entry["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
        entry.update(watch(both, seconds, until))
        steps.append(entry)
        return entry

    a_service = a.call("ship", "MachoBindObject", (station, 15), None)
    b_service = b.call("ship", "MachoBindObject", (station, 15), None)
    watch(both, 0.5)
    step("the watcher undocks", lambda: a.call_bound(a_service[0], "Undock", a_ship, False), 2.0)
    a_ballpark = a.call("beyonce", "MachoBindObject", system, None)
    step("the watcher binds the ballpark", lambda: a_ballpark, 2.5)
    step("the victim undocks", lambda: b.call_bound(b_service[0], "Undock", b_ship, False), 2.5)
    b_ballpark = b.call("beyonce", "MachoBindObject", system, None)
    step("the victim binds the ballpark", lambda: b_ballpark, 3.0)
    a_dogma = a.call("dogmaIM", "MachoBindObject", (system, 5), None)
    b_dogma = b.call("dogmaIM", "MachoBindObject", (system, 5), None)
    steps.append({"label": "both bind dogma in space", "ok": describe([a_dogma, b_dogma]), **watch(both, 1.0)})
    steps.append({"label": "both wait for the protection to end", **watch(both, 40.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))})
    slash_a = lambda command: (lambda: a.call("slash", "SlashCmd", command))  # noqa: E731
    slash_b = lambda command: (lambda: b.call("slash", "SlashCmd", command))  # noqa: E731
    gone = lambda seen: sum(1 for e in seen["events"] + seen["seen"] if e["name"] == "DoDestinyUpdate" and "RemoveBalls" in json.dumps(e)) >= 2  # noqa: E731
    step("the watcher detonates a hull beside both", slash_a("/deathtest 1"), 40.0, until=gone)
    step("both are quiet again", lambda: None, 5.0)
    step("the victim destroys its ship", slash_b("/suicide"), 30.0)
    step("both are quiet again after the suicide", lambda: None, 5.0)
    step("the victim destroys its capsule", slash_b("/suicide"), 40.0)
    step("both are quiet again after the capsule", lambda: None, 5.0)
    race = {"bloodlineID": 1, "raceID": 1, "solarSystemID": system, "stationID": station,
            "watcher": {"create": a_created, "characterID": a_char, "shipID": a_ship}, "victim": {"create": b_created, "characterID": b_char, "shipID": b_ship},
            "steps": steps}
    with open(os.path.join(out, "death.json"), "w", encoding="utf-8") as handle:
        json.dump({**meta, "note": "Recorded by tools/headless-client/scripts/record_character_scenarios.py", "races": [race]}, handle, separators=(",", ":"), ensure_ascii=False)
        handle.write("\n")
    a.close()
    b.close()


def args_of_effect(event: dict) -> list:
    """The arguments of an `OnGodmaShipEffect`: `(itemID, effectID, when, isStart, isActive, environment, startTime, duration, repeat, error)`."""
    try:
        return event["args"]["$tuple"][1]["$tuple"][1]["$tuple"]
    except (KeyError, TypeError):
        return []


def main() -> int:
    if len(sys.argv) < 4:
        print(__doc__)
        return 2
    address, sde, out = sys.argv[1], sys.argv[2], sys.argv[3]
    commit = sys.argv[sys.argv.index("--reference-commit") + 1] if "--reference-commit" in sys.argv else None
    only = sys.argv[sys.argv.index("--only") + 1] if "--only" in sys.argv else None
    host, _, port = address.partition(":")
    run = str(int(time.time()))
    ancestry = first_ancestry(sde)
    rec = Recorder(host, int(port), run)
    meta = {"reference": "EVE.js", "referenceCommit": commit, "recordedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    parts = {"walk": record_walk, "starters": record_starters, "skills": record_skills, "info": record_info, "inventory": record_inventory, "dogma": record_dogma, "fitting": record_fitting, "training": record_training, "training_fast": record_training_fast, "undock": record_undock, "approach": record_approach, "movement": record_movement, "warp": record_warp, "jump": record_jump, "others": record_others, "target": record_target, "module": record_module, "weapon": record_weapon, "ammo": record_ammo, "cargo": record_cargo, "loot": record_loot, "reload": record_reload, "drones": record_drones, "health": record_health, "death": record_death}
    try:
        for name, record in parts.items():
            if only == name or (only is None and name not in ("training_fast", "undock", "approach", "movement", "warp", "jump", "others", "target", "module", "weapon", "ammo", "cargo", "loot", "reload", "drones", "health", "death")):  # the fast one needs a reference started for it
                record(rec, out, meta, ancestry, run)
        if only in (None, "stations"):
            record_stations(rec, out, meta, ancestry, run, sde)
    finally:
        rec.close()
    print("recorded to", out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
