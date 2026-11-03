"""Groq chat completions — the model behind WordOS AI (ADR-134).

    backend ──► /ai/admin/agent ──► here ──► Groq (openai/gpt-oss-120b)

The admin's conversational analyst runs on Groq's free tier rather than the
paid Gemini key that writes lessons: the product owner asked for it, the Owner
asks a handful of questions a day at most, and a separate measurement (three
free models, the same twelve scenarios) chose ``openai/gpt-oss-120b`` with low
reasoning effort. Lessons, review and speech are untouched.

The free tier is the constraint everything here is shaped by. Measured on the
project's key, 2026-11-03: **8,000 tokens a minute**, 200,000 a day and 1,000
requests a day. One step of a data question is 4,500–6,500 tokens and a data
question is two or three steps in a row, so the second usually lands inside
the same minute as the first:

* a 429 is waited out, for as long as Groq says and never longer than
  ``max_wait`` in total — the admin sees a slower answer, not a failure;
* a 413 (one request bigger than the minute allows) is retried once with a
  smaller output ceiling, when there is room for a useful answer at all;
* past those, the error says how long until the quota is back, so the admin
  can be told that instead of "something failed". The **daily** ceiling —
  some fifteen data questions — comes back over hours and is never waited out.

The answer's shape is checked by Groq against the schema, not forced while
it is written (strict mode was measured to reject a well-formed answer that
left out an optional field), so a reply that fails the check is asked for
once more.

Dependency-free (urllib) like ``gemini.py``. Groq's edge answers a bare 403 to
Python's default User-Agent, so one is always sent.
"""

from __future__ import annotations

import json
import logging
import os
import re
import time
import urllib.error
import urllib.request
from dataclasses import dataclass

from .gemini import GeminiError, GeminiResponse

log = logging.getLogger("wordos.ai.groq")

_URL = "https://api.groq.com/openai/v1/chat/completions"

#: Groq's own words for how long to wait, when there is no Retry-After header.
_TRY_AGAIN = re.compile(r"try again in (?:(\d+)m)?([\d.]+)(ms|s)", re.I)
#: "Limit 8000, Requested 9214" — how far over the minute a request was.
_REQUESTED = re.compile(r"Limit (\d+), Requested (\d+)", re.I)


class GroqError(GeminiError):
    """A Groq failure. A GeminiError so every caller's handling still applies."""


def _env_float(name: str, default: float) -> float:
    raw = os.environ.get(name, "").strip()
    return float(raw) if raw else default


def _env_int(name: str, default: int) -> int:
    raw = os.environ.get(name, "").strip()
    return int(raw) if raw else default


@dataclass(frozen=True)
class GroqSettings:
    api_key: str
    model: str
    reasoning_effort: str
    max_output_tokens: int
    # Below this an answer is not worth asking for: a 413 then fails instead.
    min_output_tokens: int
    timeout: float
    # The longest one step may spend waiting out the per-minute limit, in
    # total. The backend's own timeout for a step sits above it.
    max_wait: float

    @property
    def configured(self) -> bool:
        return bool(self.api_key)

    @classmethod
    def from_env(cls) -> "GroqSettings":
        return cls(
            api_key=os.environ.get("GROQ_API_KEY", "").strip(),
            model=os.environ.get("AGENT_GROQ_MODEL", "openai/gpt-oss-120b").strip(),
            reasoning_effort=os.environ.get("AGENT_REASONING_EFFORT", "low").strip(),
            max_output_tokens=_env_int("AGENT_MAX_OUTPUT_TOKENS", 4096),
            min_output_tokens=_env_int("AGENT_MIN_OUTPUT_TOKENS", 1024),
            timeout=_env_float("AGENT_GROQ_TIMEOUT_SECONDS", 40),
            max_wait=_env_float("AGENT_GROQ_MAX_WAIT_SECONDS", 45),
        )


class GroqChatClient:
    def __init__(self, settings: GroqSettings, *, sleep=time.sleep) -> None:
        self._s = settings
        self._sleep = sleep

    @property
    def model(self) -> str:
        return self._s.model

    def generate(
        self,
        prompt: str,
        *,
        system_instruction: str | None = None,
        json_schema: dict | None = None,
        temperature: float = 0.3,
    ) -> GeminiResponse:
        if not self._s.configured:
            raise GroqError("GROQ_API_KEY is not set", status=503)

        messages = []
        if system_instruction:
            messages.append({"role": "system", "content": system_instruction})
        messages.append({"role": "user", "content": prompt})

        body: dict = {
            "model": self._s.model,
            "messages": messages,
            "temperature": temperature,
            "max_completion_tokens": self._s.max_output_tokens,
        }
        if self._s.reasoning_effort:
            body["reasoning_effort"] = self._s.reasoning_effort
        if json_schema is not None:
            body["response_format"] = {
                "type": "json_schema",
                "json_schema": {"name": "reply", "strict": False,
                                "schema": json_schema},
            }

        waited = 0.0
        shrunk = False
        reshaped = False
        while True:
            try:
                payload = self._post(body)
                break
            except GroqError as exc:
                if exc.status == 429:
                    delay = getattr(exc, "retry_after", None) or 2.0
                    if waited + delay > self._s.max_wait:
                        error = GroqError(
                            f"Groq limit: waited {waited:.0f}s, needs {delay:.0f}s "
                            f"more — {exc}", status=429)
                        error.retry_after = delay
                        raise error from exc
                    log.info("groq rate limit: waiting %.1fs", delay)
                    self._sleep(delay + 0.25)
                    waited += delay + 0.25
                    continue
                if exc.status == 400 and "json_validate_failed" in str(exc) and not reshaped:
                    log.info("groq answer did not match the schema: asking again")
                    reshaped = True
                    continue
                over = getattr(exc, "over_by", None)
                if exc.status == 413 and over and not shrunk:
                    room = body["max_completion_tokens"] - over - 64
                    if room >= self._s.min_output_tokens:
                        log.info("groq request too large: output ceiling %d → %d",
                                 body["max_completion_tokens"], room)
                        body["max_completion_tokens"] = room
                        shrunk = True
                        continue
                raise

        choice = (payload.get("choices") or [{}])[0]
        text = ((choice.get("message") or {}).get("content") or "").strip()
        if choice.get("finish_reason") == "length":
            raise GroqError("Groq stopped at the output ceiling before finishing")
        if not text:
            raise GroqError("Groq returned an empty response")

        usage = payload.get("usage") or {}
        return GeminiResponse(
            text=text,
            model=self._s.model,
            prompt_tokens=usage.get("prompt_tokens"),
            output_tokens=usage.get("completion_tokens"),
        )

    def _post(self, body: dict) -> dict:
        request = urllib.request.Request(
            _URL,
            data=json.dumps(body).encode("utf-8"),
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {self._s.api_key}",
                "User-Agent": "wordos-ai/1.0",
            },
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=self._s.timeout) as response:
                return json.loads(response.read())
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")[:600]
            error = GroqError(f"Groq returned {exc.code}: {detail}", status=exc.code)
            error.retry_after = _retry_after(exc.headers.get("retry-after"), detail)
            m = _REQUESTED.search(detail)
            error.over_by = int(m.group(2)) - int(m.group(1)) if m else None
            raise error from exc
        except (OSError, json.JSONDecodeError) as exc:  # URLError, reset, timeout
            raise GroqError(f"Could not reach Groq: {exc}") from exc


def _retry_after(header: str | None, detail: str) -> float | None:
    if header:
        try:
            return float(header)
        except ValueError:
            pass
    m = _TRY_AGAIN.search(detail)
    if not m:
        return None
    seconds = float(m.group(2)) / (1000 if m.group(3).lower() == "ms" else 1)
    return seconds + 60 * int(m.group(1) or 0)
