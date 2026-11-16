"""Re-computable free-tier model for QA campaign 20261111-0121-d365f09 (prompt 10).

Every input is an assumption or a measured local figure, labelled in
budget-model.json. Run:  python3 -I budget_model.py
"""
import csv, json, random

SEED = 20261111
DAYS = 30
CU = 0.25                 # Neon Free floor compute size (assumed: no autoscale above floor)
SUSPEND_MIN = 5           # Neon Free scale-to-zero after 5 min idle (neon.com/docs/introduction/plans, 2026-11-11)
NEON_CU_LIMIT = 100       # CU-hours / project / month (same source)
RENDER_HOURS_LIMIT = 750  # instance-hours / workspace / month (render.com/docs/free, 2026-11-11)
NEON_TRANSFER_GB = 5      # GB / project / month (same Neon source)

# Behaviour assumptions (low / base / high) — NOT measured on production.
SCEN = {
    "low":  dict(sessions_per_day=1.0, session_min=10, window=(8, 22)),
    "base": dict(sessions_per_day=1.5, session_min=15, window=(8, 23)),
    "high": dict(sessions_per_day=2.5, session_min=20, window=(7, 24)),
}
READY_PROBE_EVERY_MIN = 60   # docs/09-DEPLOYMENT.md recommends 60 min; in-app cache 3600 s
ADMIN_LOAD_MB = 1.56         # measured: full IntelDataset text size on local wordos_dev (65 sessions, 462 words)
ADMIN_LOAD_PER_SESSION_KB = 12.0  # ~1.56 MB / 65 sessions + words share, used to scale with growth
LESSON_DB_KB = 40            # assumed DB->API bytes per lesson (session + items + words + writes)

def awake_hours_per_day(dau, s, rng):
    minutes = [False] * (24 * 60)
    def wake(start, length):
        for m in range(int(start), min(24 * 60, int(start + length + SUSPEND_MIN))):
            minutes[m] = True
    for m in range(0, 24 * 60, READY_PROBE_EVERY_MIN):
        wake(m, 1)
    lo, hi = s["window"]
    for _ in range(dau):
        n = int(s["sessions_per_day"]) + (1 if rng.random() < s["sessions_per_day"] % 1 else 0)
        for _ in range(n):
            start = rng.uniform(lo * 60, hi * 60 - s["session_min"])
            wake(start, s["session_min"])
    return sum(minutes) / 60

def main():
    rng = random.Random(SEED)
    rows = []
    for name, s in SCEN.items():
        for dau in [0, 5, 10, 20, 30, 50, 100, 300]:
            hours = sum(awake_hours_per_day(dau, s, rng) for _ in range(DAYS))
            cu = hours * CU
            lessons = dau * s["sessions_per_day"] * DAYS
            transfer_gb = lessons * LESSON_DB_KB / 1024 / 1024
            rows.append(dict(scenario=name, dau=dau, neon_awake_hours_month=round(hours, 1),
                             neon_cu_hours_month=round(cu, 1), neon_cu_limit=NEON_CU_LIMIT,
                             neon_cu_percent=round(100 * cu / NEON_CU_LIMIT, 1),
                             learner_lessons_month=int(lessons),
                             learner_db_transfer_gb=round(transfer_gb, 3),
                             render_instance_hours_month=24 * DAYS,
                             render_percent=round(100 * 24 * DAYS / RENDER_HOURS_LIMIT, 1)))
    with open("usage-forecast.csv", "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()) + ["source", "confidence"])
        w.writeheader()
        for r in rows:
            w.writerow({**r, "source": "budget_model.py seed 20261111", "confidence": "model, not measured on production"})
    # Admin egress: how many admin API calls fit in the 5 GB transfer at a given data size.
    admin = []
    for sessions in [65, 500, 1000, 3000, 10000]:
        mb = max(ADMIN_LOAD_MB, sessions * ADMIN_LOAD_PER_SESSION_KB / 1024)
        admin.append(dict(stored_sessions=sessions, mb_per_admin_call=round(mb, 1),
                          admin_calls_that_fill_5gb=int(NEON_TRANSFER_GB * 1024 / mb)))
    with open("admin-egress.csv", "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(admin[0].keys()))
        w.writeheader(); w.writerows(admin)
    for r in rows: print(r)
    for a in admin: print(a)

if __name__ == "__main__":
    main()
