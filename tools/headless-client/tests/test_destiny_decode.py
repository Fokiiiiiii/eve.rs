import struct

from evecli.describe import destiny_state_fields
from evecli.observe import _destiny_update_view


def free_ball(item_id, mode, flags, mode_tail):
    return b"".join((
        struct.pack("<qBf3dB", item_id, mode, 25.0, 1.0, 2.0, 3.0, flags | 1),
        struct.pack("<dBqii f3dffB", 1_000_000.0, 0, -1, 42, 7, 250.0, 4.0, 5.0, 6.0, 0.5, 0.75, 0xFF),
        mode_tail,
    ))


def test_destiny_state_decoder_reports_free_ball_fields_and_record_order():
    follow = struct.pack("<qf", 9002, 1500.0)
    rigid = struct.pack("<qBf3dB B", 9003, 11, 4.0, -1.0, -2.0, -3.0, 2, 0xFF)
    payload = b"\x01" + struct.pack("<I", 123456) + free_ball(9001, 1, 0, follow) + rigid

    result = destiny_state_fields(payload)

    assert result["packetTypeName"] == "AddBalls2"
    assert result["stamp"] == 123456
    assert "decodeError" not in result
    assert [ball["itemID"] for ball in result["balls"]] == [9001, 9003]
    assert result["balls"][0]["modeName"] == "follow"
    assert result["balls"][0]["followTarget"] == 9002
    assert result["balls"][0]["followRange"] == 1500.0
    assert result["balls"][0]["position"] == (1.0, 2.0, 3.0)
    assert result["balls"][1]["modeName"] == "rigid"
    assert result["balls"][1]["position"] == (-1.0, -2.0, -3.0)


def test_destiny_state_decoder_keeps_partial_fields_and_reports_truncation():
    payload = b"\x00" + struct.pack("<I", 8) + struct.pack("<qB", 7001, 0)

    result = destiny_state_fields({"$buffer": payload.hex()})

    assert result["packetTypeName"] == "SetState"
    assert result["balls"] == []
    assert result["decodeError"] == "truncated ball radius at offset 14"


def test_destiny_update_view_adds_decoded_ball_fields_to_set_state_and_add_balls():
    state = b"\x00" + struct.pack("<I", 12) + free_ball(501, 0, 0, struct.pack("<3d", 9.0, 8.0, 7.0))
    added = b"\x01" + struct.pack("<I", 13) + free_ball(502, 1, 0, struct.pack("<qf", 501, 100.0))
    update = [
        {"$tuple": [{"$long": 12}, {"$tuple": ["SetState", {"$tuple": [{"$dict": [
            ["ego", 501], ["state", {"$buffer": state.hex()}], ["slims", []],
        ]}]}]}]},
        {"$tuple": [{"$long": 13}, {"$tuple": ["AddBalls2", {"$tuple": [
            {"$buffer": added.hex()}, [],
        ]}]}]},
    ]
    args = {"$tuple": [0, {"$tuple": [1, {"$tuple": [update, False, []]}]}]}

    view = _destiny_update_view(args)

    assert view["ballparkResets"][0]["ballFields"]["balls"][0]["targetPosition"] == (9.0, 8.0, 7.0)
    assert view["ballUpdates"][0]["ballFields"]["balls"][0]["followTarget"] == 501
