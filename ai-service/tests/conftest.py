"""Shared fixtures.

Two rules hold for everything in this directory:

* **No test ever calls Gemini.** Every generation is stubbed. A suite that
  spends money to run is a suite nobody runs.
* **No test needs the real key.** ``GEMINI_API_KEY`` and ``AI_SERVICE_TOKEN``
  are set to fixtures here before ``app.main`` is imported, so the suite runs
  on a machine that has never seen ``.env`` — which is what makes it usable in
  CI.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

import pytest

# The service reads its configuration at import time, so this has to happen
# before `app.main` is imported anywhere — including by another conftest.
os.environ["GEMINI_API_KEY"] = "test-key-not-a-real-credential"
os.environ["GEMINI_MODEL"] = "gemini-3.1-flash-lite"
os.environ["AI_SERVICE_TOKEN"] = "test-service-token"
os.environ.pop("AI_ALLOW_UNAUTHENTICATED", None)

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

TOKEN = os.environ["AI_SERVICE_TOKEN"]


@pytest.fixture(autouse=True)
def never_call_gemini(monkeypatch):
    """Makes an unstubbed generation impossible, rather than merely unlikely.

    Without this the provider client is real: a test whose request turns out to
    be *valid* when the author expected it to be rejected sails through to
    googleapis.com. That happened while this suite was being written — the call
    went out, and only the fake key stopped it becoming a charge.

    Any test that wants a generation asks for ``stub_gemini`` and says what the
    model returned. Everything else fails loudly here instead of quietly
    reaching the network.
    """
    from app import main

    def refuse(*_args, **_kwargs):
        raise AssertionError(
            "This test called Gemini for real. Use the `stub_gemini` fixture.")

    monkeypatch.setattr(main.CLIENT, "generate", refuse)


@pytest.fixture
def client():
    from fastapi.testclient import TestClient

    from app.main import app

    with TestClient(app) as test_client:
        yield test_client


@pytest.fixture
def auth() -> dict[str, str]:
    """The header the backend sends. Everything protected needs it."""
    return {"X-Service-Token": TOKEN}


@pytest.fixture
def stub_gemini(monkeypatch):
    """Replaces the provider call with a payload the test chooses.

    Returns a function taking the dict the model is pretending to have
    returned; the recorded prompts are exposed on ``.prompts`` so a test can
    assert on what the service actually asked for.
    """
    import json

    from app import main
    from app.gemini import GeminiResponse

    def install(payload: dict, *, tokens: int = 123):
        recorder = _Recorder()

        def fake_generate(prompt, **kwargs):
            recorder.prompts.append(prompt)
            recorder.kwargs.append(kwargs)
            return GeminiResponse(
                text=json.dumps(payload),
                model="gemini-3.1-flash-lite",
                prompt_tokens=tokens,
                output_tokens=0,
            )

        monkeypatch.setattr(main.CLIENT, "generate", fake_generate)
        return recorder

    return install


class _Recorder:
    def __init__(self) -> None:
        self.prompts: list[str] = []
        self.kwargs: list[dict] = []
