"""QA prompt 07: injected faults against a local API (working tree, wordos_qa).

The API on :5299 reaches PostgreSQL through fault_proxy on :55432 and the AI
service through fault_proxy on :8199. Each scenario writes the proxy's mode,
probes the API, restores `pass`, and measures recovery. Every fault here is
INJECTED; none is an incident the product had.

    python3 -I resilience_run.py SCRATCH_DIR PSQL_WRAPPER
"""
import concurrent.futures as cf
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

SCRATCH, PSQL = Path(sys.argv[1]), sys.argv[2]
OUT = Path(__file__).resolve().parent
B = "http://127.0.0.1:5299"
results = []


def req(method, path, body=None, token=None, timeout=90):
    h = {"Content-Type": "application/json"}
    if token:
        h["Authorization"] = f"Bearer {token}"
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(B + path, data=data, method=method, headers=h)
    t = time.perf_counter()
    try:
        with urllib.request.urlopen(r, timeout=timeout) as resp:
            raw = resp.read()
            code = resp.status
    except urllib.error.HTTPError as e:
        raw, code = e.read(), e.code
    except Exception as e:
        return 0, {"client_error": type(e).__name__}, (time.perf_counter() - t) * 1000
    ms = (time.perf_counter() - t) * 1000
    try:
        return code, json.loads(raw or b"null"), ms
    except ValueError:
        return code, {"raw": raw[:200].decode(errors="replace")}, ms


def mode(which, value):
    (SCRATCH / f"{which}.mode").write_text(value)


def sql(q):
    return subprocess.run([PSQL, "-Atc", q], capture_output=True, text=True).stdout.strip()


def code_of(body):
    return (body or {}).get("error", {}).get("code") if isinstance(body, dict) and isinstance(body.get("error"), dict) else None


def record(name, injected, expected, observed, verdict):
    row = {"scenario": name, "injected": injected, "expected": expected, "observed": observed, "verdict": verdict}
    results.append(row)
    print(json.dumps(row, ensure_ascii=False), flush=True)


def until_ok(fn, limit=120):
    t = time.perf_counter()
    while time.perf_counter() - t < limit:
        if fn():
            return round(time.perf_counter() - t, 2)
        time.sleep(0.5)
    return None


# ── Setup ────────────────────────────────────────────────────────────────────
mode("db", "pass"); mode("ai", "pass")
email = f"resil-{uuid.uuid4().hex[:8]}@wordos.test"
c, body, _ = req("POST", "/api/auth/register", {"email": email, "password": "correct-horse-battery",
             "displayName": "R", "phoneCountryCode": "967", "phoneNumber": "770000002"})
assert c in (200, 201), (c, body)
tok = body["token"]
uid = body["user"]["id"]
c, cands, _ = req("GET", "/api/words/lookup?q=research", token=tok)
req("POST", "/api/words", {"senseId": cands[0]["senseId"]}, token=tok)
words_before = sql(f"select count(*) from words where \"UserId\"='{uid}'")

# ── S1: AI service down (connections refused) ────────────────────────────────
mode("ai", "refuse")
hub = req("GET", "/api/hub", token=tok)
lst = req("GET", "/api/words", token=tok)
add = req("POST", "/api/words", {"text": "ponder", "customMeaning": "يتأمل"}, token=tok)
start = req("POST", "/api/sessions/reading/start", {}, token=tok)
after = sql(f"select count(*) from words where \"UserId\"='{uid}'")
sess = sql(f"select count(*) || ' sessions, fallback=' || coalesce(string_agg(\"UsedAiFallback\"::text, ','), '-') from skill_sessions where \"UserId\"='{uid}'")
record("S1 AI down", "AI proxy refuses connections",
       "hub/list 200; written meaning 503 MEANING_CHECK_UNAVAILABLE with no word stored; Reading served from the marked fallback or refused cleanly, never 500",
       {"hub": [hub[0], round(hub[2])], "list": [lst[0], round(lst[2])], "add_custom": [add[0], code_of(add[1]), round(add[2])],
        "start_reading": [start[0], code_of(start[1]), round(start[2])], "words_before_after": [words_before, after], "sessions": sess},
       "PASS" if hub[0] == 200 and lst[0] == 200 and add[0] == 503 and after == words_before and start[0] != 500 else "CHECK")
if start[0] == 200:
    req("POST", f"/api/sessions/{start[1]['id']}/abandon", {}, token=tok)
mode("ai", "pass")
rec = until_ok(lambda: req("POST", "/api/words", {"text": "ponder", "customMeaning": "يتأمل"}, token=tok)[0] in (200, 201, 409))
record("S1 recovery", "AI proxy back to pass", "the next written meaning is checked", {"seconds_to_first_success": rec}, "PASS" if rec is not None else "FAIL")

# ── S2: AI hung (accepts, never answers) — does the light API stay up? ──────
mode("ai", "blackhole")
with cf.ThreadPoolExecutor(8) as pool:
    hung = [pool.submit(req, "POST", "/api/words", {"text": f"zeal{i}", "customMeaning": "حماس"}, tok, 120) for i in range(3)]
    time.sleep(2)
    light = [req("GET", "/api/hub", token=tok) for _ in range(5)]
    health = req("GET", "/health/ready")
    hung = [f.result() for f in hung]
mode("ai", "pass")
time.sleep(1)
slots = req("GET", "/health/ready")[1].get("aiSlotsFree")
record("S2 AI hung", "AI proxy accepts and never answers",
       "the AI calls give up at the configured timeout (60 s) with 503; hub stays fast meanwhile; AI slots return",
       {"hung_calls": [[h[0], code_of(h[1]), round(h[2] / 1000, 1)] for h in hung],
        "hub_during_ms": [round(x[2]) for x in light], "hub_codes": sorted({x[0] for x in light}),
        "health_during": [health[0], (health[1] or {}).get("aiSlotsFree")], "ai_slots_after": slots},
       "PASS" if all(h[0] == 503 for h in hung) and all(x[0] == 200 and x[2] < 1000 for x in light) and slots == 24 else "CHECK")

# ── S3: AI slow (3 s per chunk) ──────────────────────────────────────────────
mode("ai", "delay:3000")
slow = req("POST", "/api/words", {"text": "candid", "customMeaning": "صريح"}, token=tok)
mode("ai", "pass")
record("S3 AI slow", "AI proxy delays every chunk 3 s", "succeeds, slower; no duplicate word",
       {"status": slow[0], "seconds": round(slow[2] / 1000, 1),
        "rows_for_word": sql(f"select count(*) from words where \"UserId\"='{uid}' and \"Text\"='candid'")},
       "PASS" if slow[0] in (200, 201) else "CHECK")

# ── S4: database down (refused) ──────────────────────────────────────────────
mode("db", "cut")
time.sleep(0.5)
mode("db", "refuse")
probes = {"ready": req("GET", "/health/ready", timeout=60), "hub": req("GET", "/api/hub", token=tok, timeout=60),
          "add": req("POST", "/api/words", {"senseId": cands[1]["senseId"]}, token=tok, timeout=60),
          "live": req("GET", "/health/live")}
mode("db", "pass")
rec = until_ok(lambda: req("GET", "/api/hub", token=tok, timeout=30)[0] == 200)
record("S4 DB down", "DB proxy drops open connections, then refuses new ones",
       "a clear 503 (or quick failure) rather than a hang or a 200 that saved nothing; live stays 200; automatic recovery",
       {k: [v[0], code_of(v[1]) or (v[1] or {}).get("title"), round(v[2] / 1000, 1)] for k, v in probes.items()} | {"seconds_to_recover": rec},
       "CHECK")

# ── S5: database hung (blackhole) ────────────────────────────────────────────
mode("db", "cut"); time.sleep(0.5); mode("db", "blackhole")
hung_db = req("GET", "/api/hub", token=tok, timeout=120)
live = req("GET", "/health/live")
mode("db", "pass")
rec = until_ok(lambda: req("GET", "/api/hub", token=tok, timeout=30)[0] == 200)
record("S5 DB hung", "DB proxy accepts and never answers",
       "the request ends at the command/connection timeout with an error, live stays up, then recovers",
       {"hub": [hung_db[0], code_of(hung_db[1]) or (hung_db[1] or {}).get("title"), round(hung_db[2] / 1000, 1)],
        "live_during": [live[0], round(live[2])], "seconds_to_recover": rec}, "CHECK")

# ── S6: data intact after all of it ──────────────────────────────────────────
dupes = sql(f"select count(*) from (select \"SenseId\" from words where \"UserId\"='{uid}' group by 1 having count(*)>1) d")
openses = sql(f"select count(*) from skill_sessions where \"UserId\"='{uid}' and not \"IsComplete\"")
record("S6 integrity", "-", "no duplicate words, no orphaned open sessions from the faults",
       {"duplicate_senses": dupes, "open_sessions": openses,
        "words": sql(f"select string_agg(\"Text\", ',') from words where \"UserId\"='{uid}'")}, "PASS" if dupes == "0" else "FAIL")

(OUT / "resilience-results.json").write_text(json.dumps(results, ensure_ascii=False, indent=1))
