#!/usr/bin/env python3
"""Record what the EVE.js reference does from login to a session with a character.

    scripts/record_character_scenarios.py <host:port> <sde-dir> <out-dir> [--reference-commit SHA]

The recording is the contract for the same path in eve.rs: `tests/test_login_to_character.py` replays the calls
against any server and compares each answer with what is stored here, by shape, treating per-run values
(identifiers, times) by type only. Nothing here is copied from client code: the calls are the ones the client
makes, and the answers are what the reference sent.

It writes login_to_character.json (one account walked through creation and selection, plus name validation,
errors and limits) and starters.json (a character per playable bloodline: what the reference gives each).
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


def main() -> int:
    if len(sys.argv) < 4:
        print(__doc__)
        return 2
    address, sde, out = sys.argv[1], sys.argv[2], sys.argv[3]
    commit = sys.argv[sys.argv.index("--reference-commit") + 1] if "--reference-commit" in sys.argv else None
    host, _, port = address.partition(":")
    run = str(int(time.time()))
    ancestry = first_ancestry(sde)
    rec = Recorder(host, int(port), run)
    meta = {"reference": "EVE.js", "referenceCommit": commit, "recordedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    try:
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
    finally:
        rec.close()
    print("recorded to", out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
