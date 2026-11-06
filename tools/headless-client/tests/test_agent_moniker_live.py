import uuid

import pytest

from evecli import marshal as m
from tests import test_agents_live as live_agents

pytestmark = pytest.mark.skipif(
    not (live_agents.LIVE and live_agents.PG_URL and not live_agents.REFERENCE),
    reason="set EVE_LIVE_HOST and EVE_PG_URL and select eve.rs to run this walk",
)
ACCEPT, REQUEST = live_agents.ACCEPT, live_agents.REQUEST
agents, connect, do_action = live_agents.agents, live_agents.connect, live_agents.do_action
new_pilot = live_agents.new_pilot
seed_agent_standing, seed_personal_standing = live_agents.seed_agent_standing, live_agents.seed_personal_standing


def test_bound_agent_moniker_courier_queries_and_offer_removal():
    run = uuid.uuid4().hex[:10]
    with connect("agent-moniker-" + run) as client:
        character_id = new_pilot(client, run)
        candidates = [
            row for row in agents(client)
            if row["agentTypeID"] == 2 and row["missionKind"] == "courier"
            and 1 <= row["level"] <= 5 and not row["isInSpace"] and not row["isLocatorAgent"]
        ]
        assert len(candidates) >= 2, "the pinned SDE must provide two station distribution agents"
        first, second = candidates[:2]
        for agent in (first, second):
            seed_agent_standing(client, agent["corporationID"], character_id)
            seed_personal_standing(client, agent["agentID"], character_id)

        first_handle = client.call("agentMgr", "MachoBindObject", first["agentID"])
        _, actions, _ = do_action(client, first_handle, REQUEST)
        assert (ACCEPT, 3) in actions
        do_action(client, first_handle, ACCEPT)
        info = m.plain(client.call_bound(first_handle, "GetMissionJournalInfo", character_id, None))
        assert isinstance(info, dict), info
        objectives = info["objectives"]
        assert objectives["missionState"] == 2
        transport = next(payload for kind, payload in objectives["objectives"] if kind == "transport")
        assert isinstance(transport[4].get("hasCargo"), bool), transport
        content_id = info["contentID"]
        keywords = m.plain(client.call_bound(first_handle, "GetMissionKeywords", content_id))
        assert isinstance(keywords, dict) and keywords.get("missionTypeLabel")
        gains = m.plain(client.call_bound(first_handle, "GetStandingGainsForMission", content_id))
        assert isinstance(gains, dict)
        assert all(isinstance(owner, int) and isinstance(gain, (int, float)) for owner, gain in gains.items())

        second_handle = client.call("agentMgr", "MachoBindObject", second["agentID"])
        _, actions, _ = do_action(client, second_handle, REQUEST)
        assert (ACCEPT, 3) in actions
        offered = m.plain(client.call_bound(second_handle, "GetMissionJournalInfo", character_id, None))
        assert isinstance(offered, dict) and offered["missionState"] == 1
        assert m.plain(client.call_bound(second_handle, "RemoveOfferFromJournal")) is None
        assert m.plain(client.call_bound(second_handle, "RemoveOfferFromJournal")) is None

        journal = m.plain(client.call("agentMgr", "GetMyJournalDetails"))
        agent_ids = {row[4] for row in journal[0]}
        assert first["agentID"] in agent_ids, (first["agentID"], agent_ids)
        assert second["agentID"] not in agent_ids, (second["agentID"], agent_ids)
