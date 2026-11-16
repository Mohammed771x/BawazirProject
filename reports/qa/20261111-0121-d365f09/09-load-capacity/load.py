"""QA prompt 09: local load against the working-tree API (path A: AI replaced).

    ai-service/.venv/bin/python -I load.py setup N            # register N learners, save tokens
    ai-service/.venv/bin/python -I load.py journeys VUS SECS   # closed-loop returning-user journeys
    ai-service/.venv/bin/python -I load.py login N WINDOW_S    # N real-Argon2 logins arriving within WINDOW_S
    ai-service/.venv/bin/python -I load.py refresh N WINDOW_S  # N refreshes arriving within WINDOW_S

Every learner is synthetic (`load-*@wordos.test`) in the throwaway wordos_qa
database. The AI service is unreachable on purpose, so session content is the
backend's own fallback (marked UsedAiFallback): this measures the API and the
database, not the model. Results append to results.jsonl.
"""
import asyncio
import json
import random
import resource
import statistics
import sys
import time
from collections import defaultdict
from pathlib import Path

import httpx

B = "http://127.0.0.1:5299"
OUT = Path(__file__).resolve().parent
ACCTS = Path("/private/tmp/claude-501/-Users-m-f-d--claude-spares-spare-CuSLPH/204cd855-047f-4656-9a27-6c0677d11004/scratchpad/load-accounts.json")  # tokens of synthetic accounts, kept out of the repo
PASSWORD = "load-test-pass-123"
lat: dict[str, list[float]] = defaultdict(list)
codes: dict[str, dict[int, int]] = defaultdict(lambda: defaultdict(int))
journeys = {"started": 0, "ok": 0, "failed": 0, "durations": []}


def pct(xs, p):
    if not xs:
        return None
    xs = sorted(xs)
    return round(xs[min(len(xs) - 1, int(round(p / 100 * (len(xs) - 1))))], 1)


async def call(c, name, method, path, **kw):
    t = time.perf_counter()
    try:
        r = await c.request(method, path, **kw)
        code = r.status_code
    except Exception as e:
        r, code = None, 0
    ms = (time.perf_counter() - t) * 1000
    lat[name].append(ms)
    codes[name][code] += 1
    return r


def summary(kind, extra):
    eps = {k: {"n": len(v), "p50": pct(v, 50), "p90": pct(v, 90), "p95": pct(v, 95), "p99": pct(v, 99),
               "max": round(max(v), 1), "codes": dict(codes[k])} for k, v in lat.items()}
    total = sum(len(v) for v in lat.values())
    bad = sum(n for k in codes for c, n in codes[k].items() if c == 0 or c >= 500)
    rej = sum(n for k in codes for c, n in codes[k].items() if c in (429, 503))
    row = {"kind": kind, **extra, "requests": total, "errors_5xx_or_net": bad, "rejected_429_503": rej,
           "client_cpu_s": round(resource.getrusage(resource.RUSAGE_SELF).ru_utime, 1), "endpoints": eps}
    with open(OUT / "results.jsonl", "a") as f:
        f.write(json.dumps(row) + "\n")
    print(json.dumps({k: v for k, v in row.items() if k != "endpoints"}))
    for k, v in eps.items():
        print(f"  {k:14s} n={v['n']:6d} p50={v['p50']} p95={v['p95']} p99={v['p99']} max={v['max']} {v['codes']}")


async def setup(n):
    accts = []
    async with httpx.AsyncClient(base_url=B, timeout=60) as c:
        sem = asyncio.Semaphore(8)

        async def one(i):
            async with sem:
                email = f"load-{i:04d}-{random.randrange(1 << 30):x}@wordos.test"
                r = await call(c, "register", "POST", "/api/auth/register", json={
                    "email": email, "password": PASSWORD, "displayName": f"L{i}",
                    "phoneCountryCode": "967", "phoneNumber": "770000003"})
                if r is not None and r.status_code in (200, 201):
                    b = r.json()
                    accts.append({"email": email, "token": b["token"], "refresh": b["refreshToken"]})
        await asyncio.gather(*(one(i) for i in range(n)))
    ACCTS.write_text(json.dumps(accts))
    summary("setup", {"accounts": len(accts)})


PREFIXES = ["res", "bo", "tra", "cl", "ma", "ha", "wa", "pro", "dec", "ex", "in", "co", "st", "pl", "sp"]


async def journey_loop(c, acct, stop, think):
    h = {"Authorization": f"Bearer {acct['token']}"}
    added = False
    while time.perf_counter() < stop:
        journeys["started"] += 1
        t = time.perf_counter()
        ok = True
        try:
            r = await call(c, "hub", "GET", "/api/hub", headers=h); ok &= r is not None and r.status_code == 200
            await asyncio.sleep(think())
            r = await call(c, "words.list", "GET", "/api/words?offset=0&pageSize=20", headers=h); ok &= r is not None and r.status_code == 200
            r = await call(c, "lookup", "GET", f"/api/words/lookup?q={random.choice(PREFIXES)}", headers=h)
            ok &= r is not None and r.status_code == 200
            if not added and r is not None and r.status_code == 200 and r.json():
                cand = random.choice(r.json())
                a = await call(c, "words.add", "POST", "/api/words", headers=h, json={"senseId": cand["senseId"]})
                added = a is not None and a.status_code in (200, 201, 409)
                ok &= added
            await asyncio.sleep(think())
            s = await call(c, "session.start", "POST", "/api/sessions/reading/start", headers=h, json={})
            ok &= s is not None and s.status_code == 200
            if s is not None and s.status_code == 200:
                sid = s.json()["id"]
                items = [i for i in s.json().get("items", []) if i.get("options")]
                for it in items[:2]:
                    await asyncio.sleep(think())
                    a = await call(c, "session.answer", "POST", f"/api/sessions/{sid}/answer", headers=h,
                                   json={"itemId": it["id"], "answer": random.choice(it["options"])})
                    ok &= a is not None and a.status_code == 200
                r = await call(c, "session.resume", "GET", f"/api/sessions/{sid}", headers=h); ok &= r is not None and r.status_code == 200
                r = await call(c, "session.abandon", "POST", f"/api/sessions/{sid}/abandon", headers=h, json={})
                ok &= r is not None and r.status_code in (200, 204)
        except Exception:
            ok = False
        journeys["ok" if ok else "failed"] += 1
        journeys["durations"].append(time.perf_counter() - t)


async def run_journeys(vus, secs):
    accts = json.loads(ACCTS.read_text())[:vus]
    stop = time.perf_counter() + secs
    think = lambda: random.uniform(1.0, 3.0)
    limits = httpx.Limits(max_connections=vus + 10, max_keepalive_connections=vus + 10)
    async with httpx.AsyncClient(base_url=B, timeout=60, limits=limits) as c:
        await asyncio.gather(*(journey_loop(c, a, stop + random.uniform(0, 0), think) for a in accts))
    d = journeys["durations"]
    done = journeys["ok"] + journeys["failed"]
    summary("journeys", {"vus": len(accts), "seconds": secs, "think_s": "1-3",
                         "journeys_done": done, "journeys_ok": journeys["ok"],
                         "journey_success": round(journeys["ok"] / done, 4) if done else None,
                         "journey_p50_s": pct(d, 50), "journey_p95_s": pct(d, 95),
                         "rps": round(sum(len(v) for v in lat.values()) / secs, 1)})


async def burst(kind, n, window):
    accts = json.loads(ACCTS.read_text())[:n]
    limits = httpx.Limits(max_connections=n + 10, max_keepalive_connections=n + 10)
    async with httpx.AsyncClient(base_url=B, timeout=120, limits=limits) as c:
        async def one(i, a):
            await asyncio.sleep(window * i / max(1, n))  # arrivals spread evenly over the window
            if kind == "login":
                await call(c, "login", "POST", "/api/auth/login", json={"email": a["email"], "password": PASSWORD})
            else:
                r = await call(c, "refresh", "POST", "/api/auth/refresh", json={"refreshToken": a["refresh"]})
                if r is not None and r.status_code == 200:
                    a["refresh"] = r.json()["refreshToken"]
                    a["token"] = r.json()["token"]
        t = time.perf_counter()
        await asyncio.gather(*(one(i, a) for i, a in enumerate(accts)))
        wall = time.perf_counter() - t
    if kind == "refresh":
        ACCTS.write_text(json.dumps(json.loads(ACCTS.read_text())[:0] + accts + json.loads(ACCTS.read_text())[n:]))
    summary(kind, {"arrivals": len(accts), "window_s": window, "wall_s": round(wall, 2)})


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "setup":
        asyncio.run(setup(int(sys.argv[2])))
    elif cmd == "journeys":
        asyncio.run(run_journeys(int(sys.argv[2]), int(sys.argv[3])))
    else:
        asyncio.run(burst(cmd, int(sys.argv[2]), float(sys.argv[3])))
