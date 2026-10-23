"""Live login-to-selection walk for the client-facing paperdoll states."""

import uuid

from tests.test_login_to_character import WALK, connect, m, revive


def test_selection_routes_missing_appearance_through_avatar_save_before_game_entry():
    run = uuid.uuid4().hex[:10]
    creation = next(
        step
        for step in WALK["steps"]
        if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step
    )
    service, method, described = creation["call"]
    arguments = [revive(value, run) for value in described]

    with connect(f"paperdoll-{run}") as client:
        character_id = int(m.plain(client.call(service, method, *arguments)))

        selection = m.plain(client.call("charUnboundMgr", "GetCharacterSelectionData"))
        row = next(row[1] for row in selection[2] if row[1]["characterID"] == character_id)
        assert row["paperdollState"] == 0

        # A missing stored doll routes through avatar customization before the client can enter the game.
        client.call("paperDollServer", "UpdateExistingCharacterFull", character_id, None, None, False)
        selection = m.plain(client.call("charUnboundMgr", "GetCharacterSelectionData"))
        row = next(row[1] for row in selection[2] if row[1]["characterID"] == character_id)
        assert row["paperdollState"] == 2

        # Saving the appearance clears the state; the next selection enters the game.
        client.call(
            "paperDollServer",
            "UpdateExistingCharacterFull",
            character_id,
            arguments[5],
            arguments[6],
            True,
        )
        selection = m.plain(client.call("charUnboundMgr", "GetCharacterSelectionData"))
        row = next(row[1] for row in selection[2] if row[1]["characterID"] == character_id)
        assert row["paperdollState"] == 0
        client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False)
        assert client.session.session_init.get("charid") == character_id
