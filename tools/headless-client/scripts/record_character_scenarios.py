#!/usr/bin/env python3
"""Record what the EVE.js reference does from login to a session with a character.

    scripts/record_character_scenarios.py <host:port> <sde-dir> <out-dir> [--reference-commit SHA] [--only walk|starters|skills|info|stations|inventory|dogma|fitting|training|training_fast|undock]

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
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from evecli import marshal as m  # noqa: E402
from evecli import packet as p  # noqa: E402
from evecli.client import Client  # noqa: E402
from evecli.describe import describe  # noqa: E402
from evecli.observe import events_of, hangar_and_ship, notifications_of, ship_of, training_view, wait_for_events, wait_quietly  # noqa: E402

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
    parts = {"walk": record_walk, "starters": record_starters, "skills": record_skills, "info": record_info, "inventory": record_inventory, "dogma": record_dogma, "fitting": record_fitting, "training": record_training, "training_fast": record_training_fast, "undock": record_undock}
    try:
        for name, record in parts.items():
            if only == name or (only is None and name not in ("training_fast", "undock")):  # the fast one needs a reference started for it
                record(rec, out, meta, ancestry, run)
        if only in (None, "stations"):
            record_stations(rec, out, meta, ancestry, run, sde)
    finally:
        rec.close()
    print("recorded to", out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
