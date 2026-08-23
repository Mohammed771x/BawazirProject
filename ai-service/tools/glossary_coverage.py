"""Measures whether a tap on any word of a passage can be answered.

The Reading section answers a tap from the passage's glossary and nothing else.
Where there is no entry the client falls back to the lexicon, which returns
every sense the word has ever had — six for "bank", five of them wrong in the
sentence in front of the learner. So "is the glossary complete?" is not a
detail of the prompt: it is whether the feature works.

It varies by level and by passage, which is why this measures rather than
assumes: it asks for a real passage at every CEFR band, tokenises it exactly
the way the Flutter client does, and reports the words a learner could tap and
get no answer for.

Run it against a service that is up, with a real key:

    ./wordos start
    ai-service/.venv/bin/python ai-service/tools/glossary_coverage.py
    ai-service/.venv/bin/python ai-service/tools/glossary_coverage.py --repeats 3

Exits non-zero if any level left a word unglossed, so it can be used as a check
rather than only read.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

#: Every level the app offers, in ladder order (Enums.cs). C2 is the top —
#: there is no C2+.
LEVELS = ["A1", "A1_PLUS", "A2", "A2_PLUS", "B1", "B1_PLUS",
          "B2", "B2_PLUS", "C1", "C1_PLUS", "C2"]

#: The client's own rule for what counts as a tappable word, copied from
#: `HighlightedPassage`. If the two ever disagree, this measurement is
#: measuring the wrong thing.
WORD = re.compile(r"[^\W_](?:[^\W_]|['’-])*")

#: A few ordinary words to build the passage around, so every level is asked
#: for the same job. Meanings are the Arabic the pipeline would carry.
WORDS = [
    {"text": "gather", "meaning": "يجمع", "definition": "to bring together",
     "part_of_speech": "v"},
    {"text": "steady", "meaning": "ثابت", "definition": "firm and regular",
     "part_of_speech": "adj"},
]

TOPICS = ["technology", "travel", "sport", "cooking", "music"]


def env(name: str) -> str | None:
    """Reads a variable from the environment, falling back to `.env`."""
    if os.environ.get(name):
        return os.environ[name]

    dotenv = ROOT / ".env"
    if not dotenv.exists():
        return None
    for line in dotenv.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line.startswith(f"{name}=") and not line.startswith("#"):
            return line.split("=", 1)[1].strip()
    return None


def request_passage(base: str, token: str | None, level: str, topic: str) -> dict:
    body = json.dumps({
        "level": level,
        "interests": [topic],
        "words": WORDS,
        "listening": False,
        "comprehension_count": 5,
    }).encode()

    headers = {"Content-Type": "application/json"}
    if token:
        headers["X-Service-Token"] = token

    req = urllib.request.Request(f"{base}/ai/content", data=body, headers=headers)
    # Generous: a passage plus its repair pass is two model calls.
    with urllib.request.urlopen(req, timeout=180) as response:
        return json.loads(response.read())


def uncovered(content: dict) -> list[str]:
    """The words a learner could tap and get no in-context answer for."""
    glossed = {g["word"].strip().lower() for g in content.get("glossary", [])}

    missing: list[str] = []
    seen: set[str] = set()
    for match in WORD.finditer(content["text"]):
        word = match.group(0)
        key = word.lower()
        if key in glossed or key in seen:
            continue
        seen.add(key)
        missing.append(word)
    return missing


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="http://127.0.0.1:8099")
    parser.add_argument("--repeats", type=int, default=1,
                        help="passages per level; each uses a different topic, "
                             "because coverage varies with the text")
    parser.add_argument("--levels", nargs="*", default=LEVELS)
    args = parser.parse_args()

    token = env("AI_SERVICE_TOKEN")
    if not token:
        # A local run may be opted out of the token entirely — the service
        # says so loudly at boot, and it is bound to loopback. Refusing to
        # measure in that case would make the tool useless on the one machine
        # it is meant for.
        unauthenticated = (env("AI_ALLOW_UNAUTHENTICATED") or "").lower()
        if unauthenticated not in ("1", "true", "yes"):
            print("AI_SERVICE_TOKEN is not set, and ai-service/.env carries "
                  "neither it nor AI_ALLOW_UNAUTHENTICATED. The service "
                  "cannot be called without one of them.")
            return 2

    print(f"{'level':<9}{'run':<5}{'len':>6}{'sent':>6}{'secs':>7}"
          f"{'distinct':>10}{'missed':>8}  title / words with no answer")
    print("-" * 92)

    failures = 0
    for level in args.levels:
        for run in range(args.repeats):
            topic = TOPICS[run % len(TOPICS)]
            started = time.monotonic()
            try:
                content = request_passage(args.base, token, level, topic)
                elapsed = time.monotonic() - started
            except urllib.error.HTTPError as e:
                print(f"{level:<9}{run + 1:<5}  HTTP {e.code}: "
                      f"{e.read().decode()[:120]}")
                failures += 1
                continue
            except Exception as e:  # noqa: BLE001 — a report, not a library
                print(f"{level:<9}{run + 1:<5}  {type(e).__name__}: {e}")
                failures += 1
                continue

            total = len({m.group(0).lower()
                         for m in WORD.finditer(content["text"])})
            length = len(WORD.findall(content["text"]))
            missing = uncovered(content)
            if missing:
                failures += 1

            tail = (", ".join(missing[:6]) if missing
                    else content.get("title") or "(no title)")
            print(f"{level:<9}{run + 1:<5}{length:>6}"
                  f"{len(content['sentences']):>6}{elapsed:>6.0f}s"
                  f"{total:>10}{len(missing):>8}  {tail}")

    print()
    if failures:
        print(f"{failures} passage(s) left a word a learner could tap "
              f"without an answer.")
        return 1

    print("Every word of every passage answers a tap with its meaning here.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
