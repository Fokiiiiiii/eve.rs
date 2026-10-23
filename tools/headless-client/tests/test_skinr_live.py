"""A V24 SKINR draft-to-license walk through the live public gateway.

The test derives a published hull slot, component, license item, binder and hull from the official mission SDE tables. It enters a real character,
saves and edits a draft, consumes the component license, exercises skill/binder/PLEX refusals, retries one sequence concurrently,
observes the committed completion notices, activates the resulting license, and applies it to the matching owned ship.
The durable job due time is advanced in the isolated live-run schemas so the normal scheduler completes it without waiting hours.
"""

from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import atexit
import os
import signal
import socket
import subprocess
import time
import uuid

import grpc
import pytest

from evecli import marshal as m
from tests import dbseed
from tests.gateway_wire import (
    REQUEST_SEND,
    Authorized,
    as_user,
    answer,
    field,
    identifier,
    number_field,
    read_fields,
    request,
    send,
    type_identifier,
)
from tests.test_login_to_character import WALK, connect, revive

LIVE = os.environ.get("EVE_LIVE_HOST", "")
GATEWAY = os.environ.get("EVE_PUBLIC_GATEWAY", "")
pytestmark = pytest.mark.skipif(
    not (LIVE and GATEWAY),
    reason="set EVE_LIVE_HOST and EVE_PUBLIC_GATEWAY to test SKINR against a running server",
)

START_STATION = 60015249
DRAFT = "eve_public.cosmetic.ship.skin.thirdparty.draft.api."
COMPONENT_ITEM = "eve_public.cosmetic.ship.skin.thirdparty.component.license.item.api."
COMPONENT_LICENSE = "eve_public.cosmetic.ship.skin.thirdparty.component.license.api."
SEQUENCING = "eve_public.cosmetic.ship.skin.thirdparty.sequencing.api."
JOB = "eve_public.cosmetic.ship.skin.thirdparty.sequencing.job.api."
LICENSE = "eve_public.cosmetic.ship.skin.thirdparty.license.api."
NOTICES_CONSUME = "/eve_public.gateway.Notices/Consume"
SEQUENCE_KIND = "ship-skinr.sequence.complete"

SKINR_FIXTURE_SQL = """WITH ship_types AS (
    SELECT type.key::int AS ship_type_id
    FROM sde_mission."types" AS type
    JOIN sde_mission."groups" AS ship_group
        ON ship_group.key = type.doc->>'groupID'
    JOIN sde_mission."categories" AS ship_category
        ON ship_category.key = ship_group.doc->>'categoryID'
    JOIN sde_mission."skinrTierThresholds" AS tiers
        ON tiers.key = ship_group.key
    WHERE ship_category.doc->'name'->>'en' = 'Ship'
),
configurations AS (
    SELECT
        configuration.key::int AS configuration_id,
        COALESCE(NULLIF(configuration.doc->>'priority', '')::int, 0) AS priority,
        configuration.doc->'config' AS slot_configuration,
        COALESCE(configuration.doc->'ships', '[]'::jsonb) AS ships,
        configuration.doc->>'allowAllShips' = 'true' AS allow_all_ships
    FROM sde_mission."skinrSlotConfigurations" AS configuration
),
explicit_ship_configurations AS (
    SELECT DISTINCT ON (ship.value::int)
        ship.value::int AS ship_type_id,
        configuration.slot_configuration
    FROM configurations AS configuration
    CROSS JOIN LATERAL jsonb_array_elements_text(configuration.ships) AS ship(value)
    ORDER BY ship.value::int, configuration.priority, configuration.configuration_id
),
hull_configurations AS (
    SELECT ship_type_id, slot_configuration
    FROM explicit_ship_configurations
    WHERE slot_configuration IS NOT NULL
    UNION ALL
    SELECT ship.ship_type_id, configuration.slot_configuration
    FROM configurations AS configuration
    CROSS JOIN ship_types AS ship
    WHERE configuration.allow_all_ships
      AND configuration.slot_configuration IS NOT NULL
      AND NOT EXISTS (
          SELECT 1
          FROM configurations AS claimed_configuration
          CROSS JOIN LATERAL jsonb_array_elements_text(claimed_configuration.ships) AS claimed(ship_type_id)
          WHERE claimed.ship_type_id::int = ship.ship_type_id
      )
),
hull_slots AS (
    SELECT
        hull.ship_type_id,
        slot.value::int AS slot_id
    FROM hull_configurations AS hull
    CROSS JOIN LATERAL jsonb_array_elements_text(hull.slot_configuration) AS slot(value)
)
SELECT concat_ws(
    '|',
    hull_slots.ship_type_id,
    hull_slots.slot_id,
    component.key::int,
    component.doc->>'category',
    COALESCE(
        NULLIF(associated.value->>'typeID', '')::int,
        NULLIF(associated.value->>'typeId', '')::int
    ),
    COALESCE(
        NULLIF(associated.value->>'licenseUsesGranted', '')::int,
        NULLIF(associated.value->>'license_uses_granted', '')::int,
        0
    ),
    COALESCE(
        NULLIF(binder.item_type->>'itemTypeID', '')::int,
        NULLIF(binder.item_type->>'itemTypeId', '')::int,
        0
    ),
    COALESCE(NULLIF(binder.item_type->>'count', '')::int, 0)
)
FROM hull_slots
JOIN sde_mission."skinrSlots" AS slot
    ON COALESCE(
        NULLIF(slot.doc->>'id', '')::int,
        NULLIF(slot.doc->>'slotID', '')::int,
        NULLIF(slot.doc->>'slotId', '')::int,
        slot.key::int
    ) = hull_slots.slot_id
CROSS JOIN sde_mission."skinrComponents" AS component
CROSS JOIN LATERAL jsonb_array_elements(
    COALESCE(component.doc->'associatedTypeIDs', component.doc->'associatedTypeIds', '[]'::jsonb)
) AS associated(value)
CROSS JOIN LATERAL (
    SELECT COALESCE(component.doc->'sequenceBinder', component.doc->'sequence_binder') AS item_type
) AS binder
CROSS JOIN LATERAL jsonb_array_elements_text(
    COALESCE(
        slot.doc->'allowedDesignComponentCategories',
        slot.doc->'allowed_design_component_categories',
        '[]'::jsonb
    )
) AS allowed(category)
WHERE component.doc->>'published' = 'true'
  AND component.doc->>'category' IN ('1', '2')
  AND allowed.category::int = (component.doc->>'category')::int
  AND NOT EXISTS (
      SELECT 1
      FROM jsonb_array_elements_text(
          COALESCE(component.doc->'disallowedSlots', component.doc->'disallowed_slots', '[]'::jsonb)
      ) AS disallowed(slot_id)
      WHERE disallowed.slot_id::int = hull_slots.slot_id
  )
  AND COALESCE(
        NULLIF(associated.value->>'licenseUsesGranted', '')::int,
        NULLIF(associated.value->>'license_uses_granted', '')::int,
        0
      ) = -1
  AND COALESCE(
        NULLIF(binder.item_type->>'itemTypeID', '')::int,
        NULLIF(binder.item_type->>'itemTypeId', '')::int,
        0
      ) > 0
  AND COALESCE(NULLIF(binder.item_type->>'count', '')::int, 0) > 0
ORDER BY hull_slots.ship_type_id, hull_slots.slot_id, component.key::int
LIMIT 1
"""


def _skinr_fixture() -> tuple[int, int, int, int, int, int, int, int]:
    row = dbseed.sql(SKINR_FIXTURE_SQL)
    assert row, "the mission SDE has no published unlimited SKINR component with a compatible slot and binder"
    values = tuple(int(value) for value in row.split("|"))
    assert len(values) == 8, values
    return values


def _layout(ship_type_id: int, slot_id: int, component_id: int, category: int) -> bytes:
    configuration = field(1, type_identifier(slot_id)) + field(
        2, field(category, field(1, type_identifier(component_id)))
    )
    return field(1, configuration) + field(2, type_identifier(ship_type_id))


def _component_kind(component_id: int, category: int, *, infinite: bool) -> bytes:
    identifier_field = field(category, type_identifier(component_id))
    kind = field(1, identifier_field)
    return kind + (number_field(3, 1) if infinite else number_field(2, 1))


def _skin_identifier(character: int, skin_hex: str) -> bytes:
    return field(1, type_identifier(character)) + field(2, field(1, skin_hex.encode()))


def _item_consume_payload(item_id: int) -> bytes:
    return field(1, field(1, number_field(1, item_id))) + number_field(2, 1)


def _skin_hex_from_draft(draft: bytes) -> str:
    fields = read_fields(draft)
    attributes = fields[2][0] if 2 in fields else fields[1][0]
    return read_fields(read_fields(attributes)[1][0])[1][0].decode()


def _send(channel, request_name: str, character: int, payload: bytes, request_id=None):
    return send(channel, [request(request_name, character, payload, request_id=request_id)])[0]


def _send_detail(channel, request_name: str, character: int, payload: bytes, request_id=None):
    call = channel.stream_stream(REQUEST_SEND)(
        iter([request(request_name, character, payload, request_id=request_id)])
    )
    responses = list(call)
    assert call.code() == grpc.StatusCode.OK, call.details()
    status, response_type, answer_payload = answer(responses[0])
    message = read_fields(responses[0]).get(4, [b""])[0].decode()
    return status, response_type, answer_payload, message


def _notice(frame: bytes) -> tuple[str, bytes]:
    any_fields = read_fields(read_fields(frame)[5][0])
    type_url = any_fields[1][0].decode()
    return type_url[type_url.rfind("/") + 1 :], any_fields.get(2, [b""])[0]


def _new_character_with_ship(client, run: str, ship_type_id: int) -> tuple[int, int]:
    creation = next(
        step
        for step in WALK["steps"]
        if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step
    )
    service, method, described = creation["call"]
    character = int(
        m.plain(
            client.call(
                service,
                method,
                *[revive(value, run) for value in described],
            )
        )
    )
    ship = dbseed.new_item(
        ship_type_id,
        owner=character,
        location=START_STATION,
        singleton=True,
    )
    dbseed.set_ship(character, ship)
    client.notifications.clear()
    client.call(
        "charUnboundMgr",
        "SelectCharacterID",
        charID=character,
        secondChoiceID=None,
        skipTutorial=False,
    )
    return character, ship


def _unix_listener_open(path: str) -> bool:
    probe = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    probe.settimeout(0.25)
    try:
        probe.connect(path)
        return True
    except OSError:
        return False
    finally:
        probe.close()


def _tcp_listener_open(address: str) -> bool:
    host, separator, port = address.rpartition(":")
    if not separator:
        return False
    host = host.strip("[]")
    try:
        with socket.create_connection((host, int(port)), timeout=0.25):
            return True
    except OSError:
        return False


def _stop_restarted_host(process: subprocess.Popen) -> None:
    if process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=30)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=10)


def _restart_host() -> subprocess.Popen:
    socket_path = os.environ["EVE_HOST_SOCKET"]
    simulation_socket = os.environ["EVE_SIM_SOCKET"]
    host_dll = os.environ["EVE_HOST_DLL"]
    old_pid = int(os.environ["EVE_HOST_PID"])
    front_port = os.environ["EVE_LIVE_PORT"]
    net_binary = os.environ["EVE_NET_BINARY"]
    os.kill(old_pid, signal.SIGTERM)

    deadline = time.monotonic() + 45
    while _unix_listener_open(socket_path) or _tcp_listener_open(f"127.0.0.1:{front_port}"):
        assert time.monotonic() < deadline, "the existing host and front did not close their listeners"
        time.sleep(0.1)

    log_path = os.path.join(
        os.environ.get("EVE_LIVE_LOG_DIR", "/tmp"),
        f"host-skinr-restart-{os.getpid()}.out",
    )
    host_env = os.environ.copy()
    host_env.update(
        EVE_DEV_AUTO_CREATE="1",
        EVE_HOST=socket_path,
        EVE_SIM_SOCKET=simulation_socket,
    )
    with open(log_path, "ab") as log:
        process = subprocess.Popen(
            ["dotnet", host_dll],
            env=host_env,
            stdout=log,
            stderr=subprocess.STDOUT,
        )

    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        if process.poll() is not None:
            with open(log_path, encoding="utf-8", errors="replace") as log:
                details = "".join(log.readlines()[-80:])
            raise AssertionError(f"the restarted host exited early:\n{details}")
        if _unix_listener_open(socket_path) and _tcp_listener_open(GATEWAY):
            break
        time.sleep(0.2)
    else:
        raise AssertionError(f"the restarted host did not reopen its listeners; see {log_path}")

    net_env = os.environ.copy()
    net_env.update(EVE_HOST=socket_path, EVE_NET_LISTEN=f"127.0.0.1:{front_port}")
    with open(log_path, "ab") as log:
        net_process = subprocess.Popen(
            [net_binary],
            env=net_env,
            stdout=log,
            stderr=subprocess.STDOUT,
        )
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        if net_process.poll() is not None:
            _stop_restarted_host(process)
            raise AssertionError("the restarted front exited before it reopened its listener")
        if _tcp_listener_open(f"127.0.0.1:{front_port}"):
            break
        time.sleep(0.1)
    else:
        _stop_restarted_host(net_process)
        _stop_restarted_host(process)
        raise AssertionError("the restarted front did not reopen its listener")
    atexit.register(_stop_restarted_host, process)
    atexit.register(_stop_restarted_host, net_process)
    return process


def test_v24_skinr_design_sequences_and_applies_a_persisted_license():
    (
        ship_type_id,
        slot_id,
        component_id,
        category,
        component_item_type_id,
        _uses_granted,
        binder_type_id,
        binder_count,
    ) = _skinr_fixture()
    assert category in (1, 2)

    run = uuid.uuid4().hex[:8]
    with connect(f"skinr-{run}") as client:
        character, ship = _new_character_with_ship(client, run, ship_type_id)
        dbseed.set_isk(character, 0)
        wallet = dbseed.schema("WALLET")
        dbseed.sql(
            f"update {wallet}.balances set plex = 0 "
            f"where owner_id = {character} and account_key = 1000"
        )

        item = dbseed.new_item(
            component_item_type_id,
            owner=character,
            location=START_STATION,
        )
        with grpc.insecure_channel(GATEWAY) as raw:
            channel = as_user(raw, client)
            notices = channel.stream_stream(NOTICES_CONSUME)(
                iter([request("eve_public.gateway.Notices.Consume", character, b"")]),
                timeout=45.0,
            )
            notices.initial_metadata()
            time.sleep(0.1)

            # ConsumeRequest is retried with the same correlation UUID: the item and its license change once.
            consume_request_id = uuid.uuid4()
            status, response_type, _, message = _send_detail(
                channel,
                COMPONENT_ITEM + "ConsumeRequest",
                character,
                _item_consume_payload(item),
                consume_request_id,
            )
            assert (status, response_type) == (200, COMPONENT_ITEM + "ConsumeResponse"), message
            assert _send(
                channel,
                COMPONENT_ITEM + "ConsumeRequest",
                character,
                _item_consume_payload(item),
                consume_request_id,
            )[:2] == (200, COMPONENT_ITEM + "ConsumeResponse")
            assert dbseed.sql(
                f"select count(*) from {dbseed.schema('INVENTORY')}.items where id = {item}"
            ) == "0"
            assert dbseed.sql(
                f"select infinite::text from {dbseed.schema('SKINR')}.skinr_component_licenses "
                f"where character_id = {character} and component_id = {component_id}"
            ) == "true"

            # A hull item is not a component-license item.
            status, response_type, _, message = _send_detail(
                channel,
                COMPONENT_ITEM + "ConsumeRequest",
                character,
                _item_consume_payload(ship),
            )
            assert (status, response_type, message) == (
                400, COMPONENT_ITEM + "ConsumeResponse", "INVALID_COMPONENT_LICENSE_ITEM"
            )

            layout = _layout(ship_type_id, slot_id, component_id, category)
            name = f"Skinr {run}"
            line_name = "Live design"
            draft_id = uuid.uuid4()
            save_payload = field(1, name.encode()) + field(2, line_name.encode()) + field(3, layout)
            status, response_type, saved, message = _send_detail(
                channel,
                DRAFT + "SaveRequest",
                character,
                save_payload,
                draft_id,
            )
            assert (status, response_type) == (200, DRAFT + "SaveResponse"), message
            assert uuid.UUID(bytes=read_fields(read_fields(saved)[1][0])[1][0]) == draft_id
            assert _skin_hex_from_draft(saved)

            status, response_type, all_saved = _send(
                channel, DRAFT + "GetAllSavedRequest", character, b""
            )
            assert (status, response_type) == (200, DRAFT + "GetAllSavedResponse")
            rows = read_fields(all_saved).get(1, [])
            assert len(rows) == 1
            assert uuid.UUID(bytes=read_fields(read_fields(rows[0])[1][0])[1][0]) == draft_id

            status, response_type, draft = _send(
                channel,
                DRAFT + "GetRequest",
                character,
                field(1, identifier(draft_id)),
            )
            assert (status, response_type) == (200, DRAFT + "GetResponse")
            assert _skin_hex_from_draft(draft)

            # An SDE-unknown hull is refused, and the existing saved draft remains readable.
            bad_layout = field(2, type_identifier(2_000_000_000))
            status, response_type, _, message = _send_detail(
                channel,
                DRAFT + "SaveRequest",
                character,
                field(1, name.encode()) + field(2, line_name.encode()) + field(3, bad_layout),
            )
            assert (status, response_type, message) == (
                400, DRAFT + "SaveResponse", "INVALID_DESIGN"
            )

            updated_line = "Edited live design"
            update_payload = (
                field(1, identifier(draft_id))
                + field(2, name.encode())
                + field(3, updated_line.encode())
                + field(4, layout)
            )
            status, response_type, updated = _send(
                channel, DRAFT + "UpdateRequest", character, update_payload
            )
            assert (status, response_type) == (200, DRAFT + "UpdateResponse")
            skin_hex = _skin_hex_from_draft(updated)
            status, response_type, draft_skin_data = _send(
                channel,
                "eve_public.cosmetic.ship.skin.thirdparty.api.GetRequest",
                character,
                field(1, skin_hex.encode()),
            )
            assert (status, response_type) == (
                200,
                "eve_public.cosmetic.ship.skin.thirdparty.api.GetResponse",
            )
            draft_fields = read_fields(read_fields(draft_skin_data)[1][0])
            assert draft_fields[1][0] == name.encode()
            assert draft_fields[2][0] == updated_line.encode()
            assert draft_fields[4][0] == layout
            assert draft_fields[6]
            assert 7 not in draft_fields

            sequence_components = _component_kind(component_id, category, infinite=True)
            invalid_component = _component_kind(component_id + 1, category, infinite=True)
            sequence_payload = (
                field(1, name.encode())
                + field(2, updated_line.encode())
                + field(3, layout)
                + field(4, sequence_components)
                + number_field(5, 2)
            )
            # No sequencing skill means one run: a two-license request is refused before state changes.
            status, response_type, _, message = _send_detail(
                channel, JOB + "InitiateRequest", character, sequence_payload
            )
            assert (status, response_type, message) == (
                403, JOB + "InitiateResponse", "SKINR_RUN_LIMIT"
            )

            # A license naming another component cannot pay for the component in this design.
            invalid_license_payload = (
                field(1, name.encode())
                + field(2, updated_line.encode())
                + field(3, layout)
                + field(4, invalid_component)
                + number_field(5, 1)
            )
            status, response_type, _, message = _send_detail(
                channel, JOB + "InitiateRequest", character, invalid_license_payload
            )
            assert (status, response_type, message) == (
                400, JOB + "InitiateResponse", "COMPONENT_LICENSES_INVALID"
            )

            # Unlimited components require their SDE binders. No job or wallet change is written yet.
            sequence_request_id = uuid.uuid4()
            valid_sequence_payload = (
                field(1, name.encode())
                + field(2, updated_line.encode())
                + field(3, layout)
                + field(4, sequence_components)
                + number_field(5, 1)
            )
            status, response_type, _, message = _send_detail(
                channel,
                JOB + "InitiateRequest",
                character,
                valid_sequence_payload,
                sequence_request_id,
            )
            assert (status, response_type, message) == (
                403, JOB + "InitiateResponse", "SKINR_BINDERS_INSUFFICIENT"
            )
            assert dbseed.sql(
                f"select count(*) from {dbseed.schema('SKINR')}.skinr_sequencing_jobs "
                f"where character_id = {character}"
            ) == "0"

            binder = dbseed.new_item(
                binder_type_id,
                owner=character,
                location=START_STATION,
                quantity=binder_count,
            )
            # PLEX failure rolls the binder and component-license changes back together.
            status, response_type, _, message = _send_detail(
                channel,
                JOB + "InitiateRequest",
                character,
                valid_sequence_payload,
                sequence_request_id,
            )
            assert (status, response_type, message) == (
                403, JOB + "InitiateResponse", "SKINR_PLEX_INSUFFICIENT"
            )
            inventory = dbseed.schema("INVENTORY")
            assert dbseed.sql(
                f"select quantity from {inventory}.items where id = {binder}"
            ) == str(binder_count)
            assert dbseed.sql(
                f"select infinite::text from {dbseed.schema('SKINR')}.skinr_component_licenses "
                f"where character_id = {character} and component_id = {component_id}"
            ) == "true"
            assert dbseed.sql(
                f"select count(*) from {dbseed.schema('SKINR')}.skinr_sequencing_jobs "
                f"where character_id = {character}"
            ) == "0"

            dbseed.sql(
                f"update {wallet}.balances set plex = 1000 "
                f"where owner_id = {character} and account_key = 1000"
            )

            def initiate_once(_):
                return _send(
                    channel,
                    JOB + "InitiateRequest",
                    character,
                    valid_sequence_payload,
                    sequence_request_id,
                )

            # Two overlapping retries with one correlation id settle once and return the same job.
            with ThreadPoolExecutor(max_workers=2) as pool:
                starts = list(pool.map(initiate_once, range(2)))
            assert all((status, response_type) == (200, JOB + "InitiateResponse")
                       for status, response_type, _ in starts)
            job_ids = {
                uuid.UUID(bytes=read_fields(read_fields(payload)[1][0])[1][0])
                for _, _, payload in starts
            }
            assert len(job_ids) == 1
            job_id = next(iter(job_ids))
            job_key = job_id.hex
            skinr = dbseed.schema("SKINR")
            stored_cost = int(dbseed.sql(
                f"select plex_cost from {skinr}.skinr_sequencing_jobs where job_id = '{job_id}'"
            ))
            assert int(float(dbseed.sql(
                f"select plex from {wallet}.balances where owner_id = {character} and account_key = 1000"
            ))) == 1000 - stored_cost
            assert dbseed.sql(
                f"select count(*) from {inventory}.items where id = {binder}"
            ) == "0"
            assert dbseed.sql(
                f"select count(*) from {skinr}.skinr_sequencing_jobs "
                f"where character_id = {character} and state = 'pending'"
            ) == "1"

            status, response_type, active = _send(
                channel, JOB + "GetAllActiveRequest", character, b""
            )
            assert (status, response_type) == (200, JOB + "GetAllActiveResponse")
            active_rows = read_fields(active).get(1, [])
            assert len(active_rows) == 1
            assert uuid.UUID(bytes=read_fields(read_fields(active_rows[0])[1][0])[1][0]) == job_id

            # The saved design can be deleted after start: the durable job carries its authoritative layout.
            status, response_type, _ = _send(
                channel,
                DRAFT + "DeleteRequest",
                character,
                field(1, identifier(draft_id)),
            )
            assert (status, response_type) == (200, DRAFT + "DeleteResponse")
            assert dbseed.sql(
                f"select count(*) from {skinr}.skinr_design_drafts where character_id = {character}"
            ) == "0"

            now = int(time.time() * 1000) - 1000
            scheduler = dbseed.schema("SCHEDULER")
            dbseed.sql(
                f"update {skinr}.skinr_sequencing_jobs set due_ms = {now} where job_id = '{job_id}'"
            )
            dbseed.sql(
                f"update {scheduler}.jobs set run_at_ms = {now} "
                f"where kind = '{SEQUENCE_KIND}' and key = '{job_key}' and state = 'pending'"
            )

            event_names = []
            while len(event_names) < 2:
                notice_name, payload = _notice(next(notices))
                if notice_name.endswith("CompletedNotice"):
                    assert uuid.UUID(bytes=read_fields(read_fields(payload)[1][0])[1][0]) == job_id
                    event_names.append(notice_name)
                elif notice_name.endswith("UpdatedNotice"):
                    assert read_fields(payload).get(1)
                    event_names.append(notice_name)
            assert event_names == [
                "eve_public.cosmetic.ship.skin.thirdparty.sequencing.job.api.CompletedNotice",
                "eve_public.cosmetic.ship.skin.thirdparty.license.api.UpdatedNotice",
            ]

            assert dbseed.sql(
                f"select state from {skinr}.skinr_sequencing_jobs where job_id = '{job_id}'"
            ) == "completed"
            assert dbseed.sql(
                f"select unactivated from {skinr}.skinr_ship_licenses "
                f"where character_id = {character} and skin_hex = '{skin_hex}'"
            ) == "1"

            status, response_type, completed = _send(
                channel,
                JOB + "GetRequest",
                character,
                field(1, identifier(job_id)),
            )
            assert (status, response_type) == (200, JOB + "GetResponse")
            completed_attrs = read_fields(completed)[1][0]
            assert read_fields(completed_attrs).get(7)

            status, response_type, skin_data = _send(
                channel,
                "eve_public.cosmetic.ship.skin.thirdparty.api.GetRequest",
                character,
                field(1, skin_hex.encode()),
            )
            assert (status, response_type) == (
                200,
                "eve_public.cosmetic.ship.skin.thirdparty.api.GetResponse",
            )
            saved_skin = read_fields(skin_data)[1][0]
            skin_fields = read_fields(saved_skin)
            assert skin_fields[1][0] == name.encode()
            assert skin_fields[2][0] == updated_line.encode()
            assert skin_fields[3]
            assert skin_fields[4][0] == layout
            assert skin_fields[5]
            assert 6 not in skin_fields
            assert read_fields(skin_fields[7][0])[1][0] > 0

            license_id = _skin_identifier(character, skin_hex)
            status, response_type, owned = _send(
                channel, LICENSE + "GetOwnedRequest", character, b""
            )
            assert (status, response_type) == (200, LICENSE + "GetOwnedResponse")
            assert any(
                read_fields(read_fields(value)[2][0])[1][0].decode() == skin_hex
                for value in read_fields(owned).get(1, [])
            )

            # Claiming is the completed job state in V24; there is no separate client claim RPC.
            status, response_type, _ = _send(
                channel,
                LICENSE + "ActivateRequest",
                character,
                field(1, license_id),
            )
            assert (status, response_type) == (200, LICENSE + "ActivateResponse")
            status, response_type, license = _send(
                channel,
                LICENSE + "GetRequest",
                character,
                field(1, license_id),
            )
            assert (status, response_type) == (200, LICENSE + "GetResponse")
            license_attributes = read_fields(license)[1][0]
            assert read_fields(license_attributes).get(1, [0])[0] == 1
            assert read_fields(license_attributes).get(2, [0])[0] == 0

            activation_notice, _ = _notice(next(notices))
            assert activation_notice == LICENSE + "UpdatedNotice"
            notices.cancel()

            # Restart the Host after completion and activation, then read and apply the persisted license.
            restart_token = m.plain(client.call("userSvc", "GetUserToken"))
            assert isinstance(restart_token, str)
            restarted_host = _restart_host()
            assert restarted_host.poll() is None
            with grpc.insecure_channel(GATEWAY) as restarted_raw:
                restarted_channel = Authorized(restarted_raw, restart_token)
                restarted_notices = restarted_channel.stream_stream(NOTICES_CONSUME)(
                    iter([request("eve_public.gateway.Notices.Consume", character, b"")]),
                    timeout=45.0,
                )
                status, response_type, persisted_job = _send(
                    restarted_channel,
                    JOB + "GetRequest",
                    character,
                    field(1, identifier(job_id)),
                )
                assert (status, response_type) == (200, JOB + "GetResponse")
                assert read_fields(read_fields(persisted_job)[1][0]).get(7)

                status, response_type, persisted_license = _send(
                    restarted_channel,
                    LICENSE + "GetRequest",
                    character,
                    field(1, license_id),
                )
                assert (status, response_type) == (200, LICENSE + "GetResponse")
                persisted_attributes = read_fields(persisted_license)[1][0]
                assert read_fields(persisted_attributes).get(1, [0])[0] == 1
                assert read_fields(persisted_attributes).get(2, [0])[0] == 0

                # Apply through the public gateway after Host restart; eve-net closes with its Host link.
                application_payload = field(1, license_id) + field(
                    2, field(1, type_identifier(ship))
                )
                apply_request_id = uuid.uuid4()
                status, response_type, _ = _send(
                    restarted_channel,
                    LICENSE + "ApplyRequest",
                    character,
                    application_payload,
                    apply_request_id,
                )
                assert (status, response_type) == (200, LICENSE + "ApplyResponse")
                assert _send(
                    restarted_channel,
                    LICENSE + "ApplyRequest",
                    character,
                    application_payload,
                    apply_request_id,
                )[:2] == (200, LICENSE + "ApplyResponse")
                names = [_notice(next(restarted_notices))[0] for _ in range(2)]
                assert names.count("OnShipCosmeticsChanged") == 1
                assert names.count("OnCurrentShipSkinChange") == 1
                restarted_notices.cancel()
                assert dbseed.sql(
                    f"select activated::text || '|' || unactivated from {skinr}.skinr_ship_licenses "
                    f"where character_id = {character} and skin_hex = '{skin_hex}'"
                ) == "true|0"
