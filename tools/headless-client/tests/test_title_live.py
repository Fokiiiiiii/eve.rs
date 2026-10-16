"""Character titles through the public gateway, against a running server (CH-22).

    EVE_LIVE_HOST=host:port EVE_PUBLIC_GATEWAY=host:port EVE_RUN='<venv python> -m pytest -q tests/test_title_live.py' tools/dev/run-eve-live.sh

A new pilot is entered through the client protocol (its character id is the gateway's identity). The title it owns is granted through the
database, as a reward would grant it (no reward is claimed through the gateway yet). The walk then reads the owned titles, wears one, is refused
a title it does not own (409), takes it off, and reads the worn title back from PostgreSQL. The messages are the client's (eve-logic
cosmetics/client/character/title/*) by field number; see tests/gateway_wire.py. Skipped unless EVE_LIVE_HOST and EVE_PUBLIC_GATEWAY are set.
"""

import os
import uuid

import grpc
import pytest

from tests import dbseed
from tests.gateway_wire import answer, field, identifier, read_fields, request, send
from tests.test_jump_clone_live import enter_new_character
from tests.test_login_to_character import connect

LIVE = os.environ.get("EVE_LIVE_HOST", "")
GATEWAY = os.environ.get("EVE_PUBLIC_GATEWAY", "")
pytestmark = pytest.mark.skipif(not (LIVE and GATEWAY), reason="set EVE_LIVE_HOST and EVE_PUBLIC_GATEWAY to test against a running server")

TITLE_API = "eve_public.cosmetic.character.title.api."
BATTLECRUISER_SMITH = uuid.UUID("00851de7-b7ca-5136-871e-d07429563c06")  # SDE characterTitles, granted to the pilot below
STARKMAN_CHAMPION = uuid.UUID("04ae6e1f-d39a-5f5b-b9f4-7a2b3e426a38")  # SDE characterTitles, not granted


def test_a_title_is_listed_worn_refused_when_not_owned_and_taken_off_through_the_gateway():
    run = uuid.uuid4().hex[:10]
    with connect(f"titles-{run}") as client:
        character = enter_new_character(client, run)
        titles = f"{dbseed.schema('CHARACTERS')}.character_titles"
        dbseed.sql(f"insert into {titles} (character_id, title_id, granted_ms) values ({character}, '{BATTLECRUISER_SMITH}', 0)")
        with grpc.insecure_channel(GATEWAY) as channel:
            # GetOwnedRequest lists the granted title (the page is empty: the default page)
            status, name, payload = send(channel, [request(TITLE_API + "GetOwnedRequest", character, b"")])[0]
            assert (status, name) == (200, TITLE_API + "GetOwnedResponse")
            owned = [uuid.UUID(bytes=read_fields(entry)[1][0]) for entry in read_fields(payload).get(1, [])]
            assert owned == [BATTLECRUISER_SMITH]

            # nothing is worn: GetEquippedForCharacterResponse has no_title (2)
            status, _, payload = send(channel, [request(TITLE_API + "GetEquippedForCharacterRequest", character, b"")])[0]
            assert status == 200 and read_fields(payload).get(2, [0])[0] == 1

            # EquipRequest wears the owned title; the next read names it
            assert send(channel, [request(TITLE_API + "EquipRequest", character, field(1, identifier(BATTLECRUISER_SMITH)))])[0][0] == 200
            status, _, payload = send(channel, [request(TITLE_API + "GetEquippedForCharacterRequest", character, b"")])[0]
            assert uuid.UUID(bytes=read_fields(read_fields(payload)[1][0])[1][0]) == BATTLECRUISER_SMITH

            # a title the pilot does not own is refused with 409 and nothing changes
            assert send(channel, [request(TITLE_API + "EquipRequest", character, field(1, identifier(STARKMAN_CHAMPION)))])[0][0] == 409
            assert dbseed.sql(f"select title_id from {dbseed.schema('CHARACTERS')}.character_equipped_titles where character_id = {character}") == str(BATTLECRUISER_SMITH)

            # UnequipRequest takes it off; the table has no worn title for the pilot then
            assert send(channel, [request(TITLE_API + "UnequipRequest", character, b"")])[0][0] == 200
            assert dbseed.sql(f"select count(*) from {dbseed.schema('CHARACTERS')}.character_equipped_titles where character_id = {character}") == "0"
            status, _, payload = send(channel, [request(TITLE_API + "GetEquippedForCharacterRequest", character, b"")])[0]
            assert read_fields(payload).get(2, [0])[0] == 1
