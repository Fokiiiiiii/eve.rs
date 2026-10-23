"""The fitting browser reads a character's and corporation's shared fitting lists.

    EVE_LIVE_HOST=host:port pytest tests/test_fitting_lists_live.py
"""

from __future__ import annotations

import uuid

import pytest

from evecli import marshal as m, packet
from evecli.describe import describe
from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_others import watch
from tests.test_undock import arrive, keyval, number

pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")


def test_fitting_browser_applies_saved_fit_before_undocking():
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-fittings-{run}") as client:
        character_id, ship_id, modules, _ = arrive(client, HEALTH["races"][0], run)
        corporation_id = client.session.session_init.get("corpid")
        assert isinstance(corporation_id, int) and corporation_id > 0

        station = int(client.session.session_init["stationid"])
        manager = client.call("invbroker", "MachoBindObject", (station, 15), None)[0]
        hangar = client.call_bound(manager, "GetInventory", 10004)
        ship_type_id = next(
            int(row.fields()["typeID"])
            for row in client.call_bound(hangar, "List")
            if int(row.fields()["itemID"]) == ship_id
        )
        inventory = client.call(
            "invbroker", "MachoBindObject", (station, 15),
            ("GetInventoryFromId", (ship_id, 0), None),
        )[1]
        fitting_info = client.call(
            "dogmaIM", "MachoBindObject", (station, 15),
            ("GetAllInfo", (True, True, None), None),
        )
        ship_info = keyval(describe(fitting_info[1]))["shipInfo"]["$dict"]
        fitted_rows = [
            keyval(value)["invItem"]["$row"]
            for item_id, value in ship_info
            if number(item_id) in set(modules)
        ]
        assert len(fitted_rows) == len(modules), fitted_rows
        fit_data = m.List([
            (int(row["typeID"]), int(row["flagID"]), 1)
            for row in fitted_rows
        ])
        fitting_id = client.call(
            "charFittingMgr", "SaveFitting", character_id,
            m.Dict([
                ("shipTypeID", ship_type_id),
                ("name", f"walk-{run}"),
                ("description", "live fitting application"),
                ("fitData", fit_data),
            ]),
        )
        assert int(fitting_id) > 0, fitting_id

        for module_id in modules:
            client.call_bound(hangar, "Add", module_id, ship_id, qty=1, flag=4)
        candidates = {}
        for row in fitted_rows:
            candidates.setdefault(int(row["typeID"]), []).append(int(row["itemID"]))
        fit_info = m.Dict([
            ("modulesByFlag", m.Dict([
                (int(row["flagID"]), int(row["typeID"])) for row in fitted_rows
            ])),
            ("chargesByType", m.Dict([])),
            ("dronesByType", m.Dict([])),
            ("fightersByTypeID", m.Dict([])),
            ("iceByType", m.Dict([])),
            ("implantsByTypeID", m.Dict([])),
        ])
        try:
            missing = client.call_bound(
                inventory, "FitFitting", ship_id, ship_type_id,
                m.Dict([(type_id, m.List(item_ids)) for type_id, item_ids in candidates.items()]),
                station, fit_info, m.Dict([]), True,
            )
        except packet.MachoError as error:
            raise AssertionError(
                f"FitFitting failed: code={error.code}, payload={error.payload!r}"
            ) from error
        assert m.plain(missing) == [], missing
        refitted_info = client.call(
            "dogmaIM", "MachoBindObject", (station, 15),
            ("GetAllInfo", (True, True, None), None),
        )
        refitted_ship_info = keyval(describe(refitted_info[1]))["shipInfo"]["$dict"]
        refitted = {
            number(item_id): keyval(value)["invItem"]["$row"]
            for item_id, value in refitted_ship_info
            if number(item_id) in set(modules)
        }
        assert set(refitted) == set(modules), refitted
        expected_flags = {int(row["itemID"]): int(row["flagID"]) for row in fitted_rows}
        actual_flags = {item_id: int(row["flagID"]) for item_id, row in refitted.items()}
        assert actual_flags == expected_flags, {"expected": expected_flags, "actual": actual_flags}
        ship_service = client.call("ship", "MachoBindObject", (station, 15), None)[0]
        client.call_bound(ship_service, "Undock", ship_id, False, onlineModules=m.List(modules))
        undocked = watch(
            {"client": client}, 15.0,
            until=lambda seen: any(event["name"] == "$sessionchange" for event in seen["client"]),
        )["client"]
        assert any(event["name"] == "$sessionchange" for event in undocked), undocked

        character_fittings = m.plain(client.call("charFittingMgr", "GetFittings", character_id))
        assert int(fitting_id) in character_fittings, character_fittings
        corporation_fittings = m.plain(client.call("corpFittingMgr", "GetFittings", corporation_id))

        assert isinstance(character_fittings, dict)
        assert isinstance(corporation_fittings, tuple)
        assert len(corporation_fittings) == 2
        assert corporation_fittings[0] == "carbon.common.script.net.objectCaching.CachedMethodCallResult"
        cache_metadata, fitting_rows, version = corporation_fittings[1]
        assert cache_metadata == {"versionCheck": "15 minutes"}
        assert isinstance(fitting_rows, dict)
        assert len(version) == 2
