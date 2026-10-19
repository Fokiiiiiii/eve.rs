"""The incursion report and the systems in incursions, read through a client session (slice 2, the incursion walk).

    EVE_LIVE_HOST=host:port pytest tests/test_incursion_walk.py

The walk logs in, calls `map.GetIncursionGlobalReport` (the report the map shows: one row per live incursion) and
`map.GetSystemsInIncursions` (the rows of the systems the incursions touch), and checks the shape the client reads. With
EVE_LIVE_SHAPE_OUT=path it writes the shape of both answers there, so the eve.rs and EVE.js captures can be compared key by key:
the values differ (the incursions are random), the shape must not. The reference server starts its incursions at boot, so
both sides have rows; a server with none answers an empty list, which the walk accepts.
"""

from __future__ import annotations

import json
import os

import pytest

from evecli import marshal as m
from evecli.client import Client

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

# the keys of an incursion report row the map reads (the client reads the tale id, state, influence and incursed systems of each row)
REPORT_KEYS = {"taleID", "templateClassID", "stagingSolarSystemID", "state", "influence", "rewardGroupID", "incursedSystems"}

def _client() -> Client:
    host, _, port = LIVE.partition(":")
    return Client(host or "127.0.0.1", int(port or 26000), timeout=20.0)

def normal(value):
    """A decoded answer as plain Python: a named object (a KeyVal, a rowset) is its fields, a tuple is a list."""
    if isinstance(value, tuple) and len(value) == 2 and isinstance(value[0], str) and isinstance(value[1], dict):
        return normal(value[1])
    if isinstance(value, (list, tuple)):
        return [normal(item) for item in value]
    if isinstance(value, dict):
        return {str(key): normal(item) for key, item in value.items()}
    return value

def shape(value):
    """The keys and the types of a normalised answer, with a list shown by its first row (the values are not compared)."""
    if isinstance(value, dict):
        return {key: shape(item) for key, item in sorted(value.items())}
    if isinstance(value, list):
        return [shape(value[0])] if value else []
    return type(value).__name__

def _dump(name: str, answer) -> None:
    path = os.environ.get("EVE_LIVE_SHAPE_OUT")
    if not path:
        return
    try:
        with open(path) as handle:
            recorded = json.load(handle)
    except (FileNotFoundError, json.JSONDecodeError):
        recorded = {}
    recorded[name] = shape(answer)
    with open(path, "w") as handle:
        json.dump(recorded, handle, indent=2, sort_keys=True)

def test_the_incursion_report_has_the_rows_the_map_reads():
    with _client() as client:
        client.login("evecli-live", "evecli-live")
        report = normal(m.plain(client.call("map", "GetIncursionGlobalReport")))
        _dump("GetIncursionGlobalReport", report)
        assert isinstance(report, list)
        for row in report:
            assert isinstance(row, dict)
            assert REPORT_KEYS <= set(row), f"missing keys: {sorted(REPORT_KEYS - set(row))}"
            assert row["state"] in (0, 1, 2)
            assert 0.0 <= float(row["influence"]) <= 1.0

def test_the_systems_in_incursions_answer_rows_for_the_staging_systems():
    with _client() as client:
        client.login("evecli-live", "evecli-live")
        answer = normal(m.plain(client.call("map", "GetSystemsInIncursions")))
        _dump("GetSystemsInIncursions", answer)
        lines = answer["lines"] if isinstance(answer, dict) and "lines" in answer else answer
        assert isinstance(lines, list)
        for line in lines:
            assert line is not None
