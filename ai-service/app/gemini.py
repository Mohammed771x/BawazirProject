"""Thin client for the Gemini API.

Kept deliberately small and dependency-free (urllib, not the vendor SDK): the
service's job is to own prompts and return validated JSON, and a smaller
surface is easier to reason about, mock and swap. `System Archticture.txt` §11
requires the provider to be replaceable without touching WordOS logic, so
everything provider-specific lives in this one file.
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from dataclasses import dataclass

_BASE = "https://generativelanguage.googleapis.com/v1beta/models"


class GeminiError(RuntimeError):
    """A provider failure the caller can report without leaking internals."""

    def __init__(self, message: str, *, status: int | None = None) -> None:
        super().__init__(message)
        self.status = status


@dataclass(frozen=True)
class GeminiResponse:
    text: str
    model: str
    prompt_tokens: int | None
    output_tokens: int | None


class GeminiClient:
    def __init__(self, api_key: str, model: str, timeout: float = 60.0) -> None:
        self._api_key = api_key
        self._model = model
        self._timeout = timeout

    def generate(
        self,
        prompt: str,
        *,
        system_instruction: str | None = None,
        json_schema: dict | None = None,
        temperature: float = 0.7,
    ) -> GeminiResponse:
        """Sends one prompt and returns the model's text.

        When ``json_schema`` is given the model is constrained to that shape,
        which is what makes the output parseable rather than prose we have to
        guess at (rule R2: the backend decides, so it needs structured data).
        """
        body: dict = {
            "contents": [{"parts": [{"text": prompt}]}],
            "generationConfig": {"temperature": temperature},
        }

        if system_instruction:
            body["systemInstruction"] = {"parts": [{"text": system_instruction}]}

        if json_schema is not None:
            body["generationConfig"]["responseMimeType"] = "application/json"
            body["generationConfig"]["responseSchema"] = json_schema

        # The key travels in a header, never in the URL: query strings end up in
        # proxy logs, browser history and error reports.
        request = urllib.request.Request(
            f"{_BASE}/{self._model}:generateContent",
            data=json.dumps(body).encode("utf-8"),
            headers={
                "Content-Type": "application/json",
                "x-goog-api-key": self._api_key,
            },
            method="POST",
        )

        try:
            with urllib.request.urlopen(request, timeout=self._timeout) as response:
                payload = json.loads(response.read())
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")[:400]
            # The provider's message may echo the request; the key is never in
            # the body, so this is safe to surface to an operator — but it is
            # never returned to the mobile client.
            raise GeminiError(
                f"Gemini returned {exc.code}: {detail}", status=exc.code
            ) from exc
        except urllib.error.URLError as exc:
            raise GeminiError(f"Could not reach Gemini: {exc.reason}") from exc

        candidates = payload.get("candidates") or []
        if not candidates:
            reason = payload.get("promptFeedback", {}).get("blockReason")
            raise GeminiError(
                f"Gemini returned no candidates (blockReason={reason})"
            )

        parts = candidates[0].get("content", {}).get("parts") or []
        text = "".join(part.get("text", "") for part in parts).strip()
        if not text:
            raise GeminiError("Gemini returned an empty response")

        usage = payload.get("usageMetadata", {})
        return GeminiResponse(
            text=text,
            model=self._model,
            prompt_tokens=usage.get("promptTokenCount"),
            output_tokens=usage.get("candidatesTokenCount"),
        )

    def generate_json(self, prompt: str, schema: dict, **kwargs) -> dict:
        """Generates and parses JSON, failing loudly on malformed output.

        AI output is never trusted: if it does not parse, that is an error the
        caller handles, not something to paper over with a regex (rule R2).
        """
        response = self.generate(prompt, json_schema=schema, **kwargs)
        try:
            return json.loads(response.text)
        except json.JSONDecodeError as exc:
            raise GeminiError(
                f"Gemini returned unparseable JSON: {response.text[:200]}"
            ) from exc
