"""Who is allowed to spend the Gemini budget.

This service holds the API key. The token check is the only thing standing
between an open port and somebody else's bill, so it gets the most direct tests
in the suite.
"""

from __future__ import annotations

import importlib

import pytest

from conftest import TOKEN

PROTECTED = [
    "/ai/content",
    "/ai/content/relevel",
    "/ai/writing",
    "/ai/speaking/turn",
    "/ai/speaking/evaluate",
    "/ai/placement/evaluate",
]


@pytest.mark.parametrize("path", PROTECTED)
def test_no_token_is_refused(client, path):
    response = client.post(path, json={})

    assert response.status_code == 401
    assert response.json()["error"]["code"] == "UNAUTHORIZED"


@pytest.mark.parametrize("path", PROTECTED)
def test_wrong_token_is_refused(client, path):
    response = client.post(
        path, json={}, headers={"X-Service-Token": "not-the-token"})

    assert response.status_code == 401


def test_a_token_that_is_a_prefix_of_the_real_one_is_refused(client):
    """The comparison is on the whole value, not a prefix of it."""
    response = client.post(
        "/ai/content",
        json={},
        headers={"X-Service-Token": TOKEN[:-1]},
    )

    assert response.status_code == 401


def test_the_right_token_gets_past_the_door(client, auth):
    """422, not 401: the body is empty, but the caller was let in."""
    response = client.post("/ai/content", json={}, headers=auth)

    assert response.status_code == 422


def test_health_needs_no_token(client):
    response = client.get("/health")

    assert response.status_code == 200
    assert response.json()["status"] == "ok"


# ── Startup: the token cannot simply be forgotten ────────────────────────────
#
# It used to be optional, and an unset value did not weaken the check above —
# it removed it, silently, leaving an open relay to a paid API. These three
# tests are the guard on that.

def _settings_with(monkeypatch, **env):
    from app import config

    importlib.reload(config)
    for key, value in env.items():
        if value is None:
            monkeypatch.delenv(key, raising=False)
        else:
            monkeypatch.setenv(key, value)
    return config


def test_startup_refuses_a_missing_service_token(monkeypatch):
    config = _settings_with(
        monkeypatch,
        GEMINI_API_KEY="k",
        AI_SERVICE_TOKEN="",
        AI_ALLOW_UNAUTHENTICATED=None,
    )

    with pytest.raises(config.ConfigurationError) as raised:
        config.load_settings()

    assert "AI_SERVICE_TOKEN is not set" in str(raised.value)


def test_running_unauthenticated_takes_an_explicit_opt_in(monkeypatch, capsys):
    config = _settings_with(
        monkeypatch,
        GEMINI_API_KEY="k",
        AI_SERVICE_TOKEN="",
        AI_ALLOW_UNAUTHENTICATED="true",
    )

    settings = config.load_settings()

    assert settings.service_token == ""
    # And it says so loudly, because this is not a state to be in by accident.
    assert "WARNING" in capsys.readouterr().out


def test_a_configured_token_is_used_as_given(monkeypatch):
    config = _settings_with(
        monkeypatch, GEMINI_API_KEY="k", AI_SERVICE_TOKEN="  spaced  ")

    assert config.load_settings().service_token == "spaced"


def test_the_gemini_key_is_never_returned_whole(monkeypatch):
    config = _settings_with(
        monkeypatch,
        GEMINI_API_KEY="AIzaSyEXAMPLEEXAMPLEEXAMPLE",
        AI_SERVICE_TOKEN="t",
    )

    masked = config.load_settings().masked_key

    assert "AIzaSyEXAMPLEEXAMPLEEXAMPLE" not in masked
    assert masked.startswith("AIza") and masked.endswith("chars)")
