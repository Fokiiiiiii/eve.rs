"""Live client walk: moving materials into a watched industry input location refreshes availability."""

from __future__ import annotations

import time
import uuid

from evecli.observe import notifications_of
from tests import test_walk_industry as industry

pytestmark = industry.pytestmark


def tuple_items(value):
    if isinstance(value, dict) and isinstance(value.get("$tuple"), list):
        return value["$tuple"]
    if isinstance(value, (list, tuple)):
        return list(value)
    return None


def material_map(value) -> dict[int, int]:
    return {
        industry.number(key): industry.number(quantity)
        for key, quantity in industry._entries(industry.describe(value))
    }


def test_inventory_move_updates_the_open_industry_monitor_materials():
    race = industry.HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    system = industry.station_system()
    with industry.base.connect(f"walk-industry-monitor-{run}") as who:
        character, ship, _, _ = industry.arrive(who, race, run)
        blueprint = industry.dbseed.new_item(
            industry.BLUEPRINT, owner=character, location=industry.STATION, singleton=True, quantity=-1
        )
        industry.dbseed.new_item(industry.MATERIAL, owner=character, location=industry.STATION, quantity=5)
        incoming = industry.dbseed.new_item(
            industry.MATERIAL, owner=character, location=ship, flag=5, quantity=3
        )

        station = who.call("invbroker", "MachoBindObject", (industry.STATION, 15), None)[0]
        hangar = who.call_bound(station, "GetInventory", 10004)
        monitor_request = industry.job_dump(character, blueprint, industry.MANUFACTURING, system)
        monitor_request["inputLocation"].update(ownerID=character, canTake=True)
        monitor_id, available = who.call("industryMonitor", "ConnectJob", monitor_request)
        monitor_id = industry.number(monitor_id)
        assert material_map(available)[industry.MATERIAL] == 5

        notifications_of(who)
        who.call_bound(hangar, "Add", incoming, ship, qty=3, flag=industry.dbseed.HANGAR)

        events = []
        deadline = time.monotonic() + 5.0
        notice = None
        while time.monotonic() < deadline:
            events.extend(notifications_of(who))
            for event in events:
                if event.get("name") != "OnIndustryMaterials":
                    continue
                args = tuple_items(event.get("args"))
                if args and industry.number(args[0]) == monitor_id:
                    notice = event
                    break
            if notice is not None:
                break
            time.sleep(0.2)

        assert notice is not None, f"no OnIndustryMaterials for monitor {monitor_id}: {[event.get('name') for event in events]}"
        names = [event.get("name") for event in events]
        assert names.index("OnItemChange") < names.index("OnIndustryMaterials"), names
        args = tuple_items(notice["args"])
        materials = material_map(args[1])
        assert materials[industry.MATERIAL] == 8, materials
