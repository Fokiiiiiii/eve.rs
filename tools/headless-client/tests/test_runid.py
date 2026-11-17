import re

from tests.runid import run_token


def test_default_run_tokens_are_unique_and_fit_character_names(monkeypatch):
    monkeypatch.delenv("EVE_LIVE_RUN_ID", raising=False)

    tokens = [run_token() for _ in range(20)]

    assert len(set(tokens)) == len(tokens)
    assert all(re.fullmatch(r"[0-9a-f]{21}", token) for token in tokens)
    assert all(len(f"Recorded Pilot {token}{suffix}") <= 37 for token in tokens for suffix in ("a", "b"))
