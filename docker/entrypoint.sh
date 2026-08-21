#!/usr/bin/env bash
#
# Starts the AI service and the API, and keeps the container honest about both
# (ADR-062).
#
# The important behaviour is at the bottom: if either process dies, this exits.
# A container that keeps running with half its services up is worse than one
# that restarts — the platform's health check passes, the learner's session
# fails, and nothing anywhere says why.

set -euo pipefail

# ── The AI service, on loopback only ─────────────────────────────────────────
#
# 127.0.0.1, never 0.0.0.0: it holds the Gemini key, and the only thing that
# should be able to reach it is the API sitting beside it in this container.
#
# One worker. Each is a whole Python interpreter, and on a 512 MiB instance the
# second one buys concurrency that the model's own rate limit will not allow
# anyway.
/opt/ai-venv/bin/uvicorn app.main:app \
    --app-dir /app/ai-service \
    --host 127.0.0.1 \
    --port 8099 \
    --workers 1 &
AI_PID=$!

# Wait for it before starting the API. Not for correctness — the API retries and
# falls back — but so the first learner of the day does not get the fallback
# content simply because they arrived during the boot.
# Tested with bash's own /dev/tcp rather than curl, which the runtime image does
# not carry — installing a whole HTTP client to check that a port is open is a
# layer nobody needs.
for _ in $(seq 1 30); do
    if (exec 3<>/dev/tcp/127.0.0.1/8099) 2>/dev/null; then
        exec 3>&-
        break
    fi
    sleep 1
done

# ── The API, on the port the platform gave us ────────────────────────────────
cd /app/api
dotnet WordOs.Api.dll --urls "http://0.0.0.0:${PORT:-8080}" &
API_PID=$!

# Pass a stop signal on rather than leaving orphans behind: the platform sends
# SIGTERM and expects the container to go, and a child that ignores it is a
# deploy that hangs for the full grace period every time.
terminate() {
    kill -TERM "$AI_PID" "$API_PID" 2>/dev/null || true
    wait "$AI_PID" "$API_PID" 2>/dev/null || true
    exit 0
}
trap terminate TERM INT

# Whichever exits first ends the container, and its status is the container's.
wait -n
STATUS=$?

echo "entrypoint: a service exited (status ${STATUS}) — stopping the container" >&2
kill -TERM "$AI_PID" "$API_PID" 2>/dev/null || true
exit "${STATUS}"
