"""Skill plans through the public protobuf gateway, against a running server (CH-21).

    EVE_LIVE_HOST=host:port EVE_PUBLIC_GATEWAY=host:port EVE_RUN='<venv python> -m pytest -q tests/test_skill_plan_live.py' tools/dev/run-eve-live.sh

The client's request goes over gRPC (grpcio) to /eve_public.gateway.Requests/Send as the V24.01 client does: an eve_public.Request frame whose
payload is the request message in an Any, and whose authoritative_context names the active character. The answers are read the way the
client reads them (eve-logic skills/skillplan/grpc/message_bus/requestsMessenger.py: status 200 is success; skillPlanRequestsMessenger.py:
the field numbers of plan_pb2 and milestone_pb2). The messages are encoded here by hand from those field numbers, so the test does not use
the server's own codec. A new pilot is entered through the client protocol first (its character id is the gateway's identity). The plans are
read back from PostgreSQL too. Skipped unless EVE_LIVE_HOST and EVE_PUBLIC_GATEWAY are set.
"""

import os
import uuid

import grpc
import pytest

from tests import dbseed
from tests.test_jump_clone_live import enter_new_character
from tests.test_login_to_character import connect

LIVE = os.environ.get("EVE_LIVE_HOST", "")
GATEWAY = os.environ.get("EVE_PUBLIC_GATEWAY", "")
pytestmark = pytest.mark.skipif(not (LIVE and GATEWAY), reason="set EVE_LIVE_HOST and EVE_PUBLIC_GATEWAY to test against a running server")

from tests.gateway_wire import as_user, REQUEST_PING, REQUEST_SEND, answer, field, identifier, number_field, plan_attributes, read_fields, requirement, request, send, type_identifier

PLAN = "eve_public.character.skill.plan."
MILESTONE = "eve_public.character.skill.plan.milestone."
GUNNERY = 3300
SMALL_HYBRID_TURRET = 3310

def test_a_skill_plan_is_created_read_changed_with_a_milestone_activated_and_deleted_through_the_gateway():
    run = uuid.uuid4().hex[:10]
    with connect(f"skillplan-{run}") as client:
        character = enter_new_character(client, run)
        with grpc.insecure_channel(GATEWAY) as raw:
            channel = as_user(raw, client.session.user_id)
            # the Ping calls answer an empty frame
            assert channel.unary_unary(REQUEST_PING)(b"") == b""

            # CreateRequest: skill_plan (name 1, skill_requirements 2, description 3) answers the new plan's identifier (1)
            status, name, payload = send(channel, [request(PLAN + "CreateRequest", character,
                field(1, plan_attributes("Gunnery", "Guns first", [(GUNNERY, 4)])))])[0]
            assert (status, name) == (200, PLAN + "CreateResponse")
            plan_id = uuid.UUID(bytes=read_fields(read_fields(payload)[1][0])[1][0])

            # GetAllRequest lists the identifiers; GetRequest carries the attributes
            status, name, payload = send(channel, [request(PLAN + "GetAllRequest", character, b"")])[0]
            assert status == 200
            assert [uuid.UUID(bytes=read_fields(entry)[1][0]) for entry in read_fields(payload).get(1, [])] == [plan_id]
            status, name, payload = send(channel, [request(PLAN + "GetRequest", character, field(1, identifier(plan_id)))])[0]
            assert status == 200 and name == PLAN + "GetResponse"
            attributes = read_fields(read_fields(payload)[1][0])
            assert attributes[1][0] == b"Gunnery" and attributes[3][0] == b"Guns first"
            # the canonical form: every level of the skill, one entry each (levels 1 to 4)
            assert [(read_fields(read_fields(entry)[1][0])[1][0], read_fields(entry)[2][0]) for entry in attributes[2]] == [(GUNNERY, level) for level in range(1, 5)]

            # SetNameRequest and SetSkillRequirementsRequest change it; the next read shows both
            status, _, _ = send(channel, [request(PLAN + "SetNameRequest", character, field(1, identifier(plan_id)) + field(2, b"Gunnery II"))])[0]
            assert status == 200
            status, _, _ = send(channel, [request(PLAN + "SetSkillRequirementsRequest", character,
                field(1, identifier(plan_id)) + field(2, requirement(GUNNERY, 5)) + field(2, requirement(SMALL_HYBRID_TURRET, 3)))])[0]
            assert status == 200
            status, _, payload = send(channel, [request(PLAN + "GetRequest", character, field(1, identifier(plan_id)))])[0]
            attributes = read_fields(read_fields(payload)[1][0])
            assert attributes[1][0] == b"Gunnery II"
            requirements = [(read_fields(read_fields(entry)[1][0])[1][0], read_fields(entry)[2][0]) for entry in attributes[2]]
            # the plan is kept in the reference's canonical form: Small Hybrid Turret needs Gunnery 2, so every level of Gunnery comes first
            assert requirements == [(GUNNERY, level) for level in range(1, 6)] + [(SMALL_HYBRID_TURRET, level) for level in range(1, 4)]

            # a milestone (milestone.CreateRequest: milestone's skill_plan 1, train_to_type 2) and its list
            milestone_attributes = field(1, identifier(plan_id)) + field(2, type_identifier(GUNNERY)) + field(4, b"Train the gun")
            status, name, _ = send(channel, [request(MILESTONE + "CreateRequest", character, field(1, milestone_attributes))])[0]
            assert (status, name) == (200, MILESTONE + "CreateResponse")
            status, _, payload = send(channel, [request(MILESTONE + "GetAllRequest", character, field(1, identifier(plan_id)))])[0]
            assert status == 200 and len(read_fields(payload).get(1, [])) == 1

            # SetActiveRequest, then GetActiveResponse names the plan and carries its attributes
            assert send(channel, [request(PLAN + "SetActiveRequest", character, field(1, identifier(plan_id)))])[0][0] == 200
            status, _, payload = send(channel, [request(PLAN + "GetActiveRequest", character, b"")])[0]
            assert uuid.UUID(bytes=read_fields(read_fields(payload)[1][0])[1][0]) == plan_id
            assert read_fields(payload)[2][0]

            # the plan is kept in PostgreSQL for the pilot (the gateway does not hold it in memory)
            assert int(dbseed.sql(f"select count(*) from {dbseed.schema('SKILLS')}.skill_plans where character_id = {character}")) == 1

            # DeleteRequest removes it, and GetRequest then answers 404
            assert send(channel, [request(PLAN + "DeleteRequest", character, field(1, identifier(plan_id)))])[0][0] == 200
            assert send(channel, [request(PLAN + "GetRequest", character, field(1, identifier(plan_id)))])[0][0] == 404
            assert int(dbseed.sql(f"select count(*) from {dbseed.schema('SKILLS')}.skill_plans where character_id = {character}")) == 0
