#!/usr/bin/env python3
"""Verifies the Gemini key end to end, without ever printing it.

    python3 verify_key.py

Makes one real (cheap) call and reports whether the key works, which model
answered, and how many tokens it cost. Run this before anything else — a
credential problem found here is thirty seconds; found during a session flow it
is an afternoon.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from app.config import ConfigurationError, load_settings  # noqa: E402
from app.gemini import GeminiClient, GeminiError  # noqa: E402


def main() -> int:
    try:
        settings = load_settings()
    except ConfigurationError as exc:
        print(f"✗ {exc}")
        return 1

    # A fingerprint, never the key itself — this output may end up in a
    # terminal history or a screenshot.
    print(f"  key    : {settings.masked_key}")
    print(f"  model  : {settings.gemini_model}")
    print("  calling Gemini …")

    client = GeminiClient(settings.gemini_api_key, settings.gemini_model)

    try:
        response = client.generate_json(
            "Return the Arabic meaning of the English word 'book' as a noun "
            "meaning a written work.",
            schema={
                "type": "object",
                "properties": {
                    "word": {"type": "string"},
                    "arabic": {"type": "string"},
                },
                "required": ["word", "arabic"],
            },
            temperature=0,
        )
    except GeminiError as exc:
        print(f"✗ {exc}")
        if exc.status == 400:
            print("\n  A 400 usually means the key is malformed.")
        elif exc.status in (401, 403):
            print("\n  A 401/403 means the key was rejected — check that the "
                  "Generative Language API is enabled for the project.")
        elif exc.status == 429:
            print("\n  A 429 is a rate/quota limit, not a bad key.")
        return 1

    print(f"✓ Gemini answered: {response}")
    print("\n  The key works. It is in .env, which Git ignores.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
