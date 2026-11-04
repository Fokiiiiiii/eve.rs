from tests.test_undock import compare_event


def test_disconnect_comparison_accepts_integer_width_but_checks_other_fields():
    def event(object_id, client_id, ref_id):
        return {
            "name": "OnMachoObjectDisconnect",
            "idType": "+clientID",
            "args": {"$tuple": [0, {"$tuple": [1, {"$tuple": [object_id, client_id, ref_id]}]}]},
        }

    expected = event("N=1:2", 123, None)
    assert compare_event(expected, event("N=2:3", {"$long": 456}, None), "disconnect", {}) == []
    assert compare_event(expected, event(1, {"$long": 456}, None), "disconnect", {})
    assert compare_event(expected, event("N=2:3", {"$long": 456}, 0), "disconnect", {})
    assert compare_event(expected, event("N=2:3", True, None), "disconnect", {})
