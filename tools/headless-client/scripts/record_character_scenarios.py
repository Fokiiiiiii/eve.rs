#!/usr/bin/env python3
"""Record what the EVE.js reference does from login to a session with a character.

    scripts/record_character_scenarios.py <host:port> <sde-dir> <out-dir> [--reference-commit SHA] [--only walk|starters|skills|info|stations|inventory]

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
    parts = {"walk": record_walk, "starters": record_starters, "skills": record_skills, "info": record_info, "inventory": record_inventory}
    try:
        for name, record in parts.items():
            if only in (None, name):
                record(rec, out, meta, ancestry, run)
        if only in (None, "stations"):
            record_stations(rec, out, meta, ancestry, run, sde)
    finally:
        rec.close()
    print("recorded to", out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
