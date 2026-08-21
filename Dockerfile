# WordOS — one image, both services (ADR-062).
#
# The API and the AI service run in the same container on purpose, and it is
# worth saying why, because the architecture keeps them apart everywhere else.
#
#   * **The free tier is measured in instance-hours.** Two always-on services
#     burn twice the allowance and run out halfway through the month. One does
#     not.
#   * **The AI service holds the Gemini key.** As its own service it would get
#     a public URL, reachable by anyone who found it and guarded only by a
#     shared token. Here it binds to loopback and is not reachable from the
#     internet at all — a stronger boundary than the one it replaces.
#   * One container is one cold start, not two.
#
# Splitting them again later is a deployment change, not a code change: the API
# already reaches the AI service over HTTP at a configured address.

# ── Build the API ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore before copying the rest, so a change to a .cs file does not re-download
# every package.
COPY backend/src/WordOs.Domain/*.csproj        backend/src/WordOs.Domain/
COPY backend/src/WordOs.Application/*.csproj   backend/src/WordOs.Application/
COPY backend/src/WordOs.Infrastructure/*.csproj backend/src/WordOs.Infrastructure/
COPY backend/src/WordOs.Api/*.csproj           backend/src/WordOs.Api/
RUN dotnet restore backend/src/WordOs.Api/WordOs.Api.csproj

COPY backend/src/ backend/src/
RUN dotnet publish backend/src/WordOs.Api/WordOs.Api.csproj \
    -c Release -o /app/api --no-restore

# ── The image that actually runs ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Python for the AI service. `--no-install-recommends` and the cleanup keep the
# layer from carrying a compiler toolchain nobody runs.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip python3-venv \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# A virtual environment rather than --break-system-packages: Debian's Python is
# also the system's, and pip writing into it is how a base image stops booting.
RUN python3 -m venv /opt/ai-venv
ENV PATH="/opt/ai-venv/bin:$PATH"

COPY ai-service/requirements.txt /app/ai-service/
RUN pip install --no-cache-dir -r /app/ai-service/requirements.txt

COPY --from=build /app/api /app/api
COPY ai-service/app /app/ai-service/app
COPY docker/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

# Workstation GC, not server GC. Server GC sizes its heaps per core and assumes
# the machine is the app's — on a 512 MiB shared instance that is how a .NET
# process gets killed for memory it never actually needed.
ENV DOTNET_gcServer=0 \
    DOTNET_GCConserveMemory=5 \
    ASPNETCORE_ENVIRONMENT=Production

# Where the API looks for the AI service. Loopback: it is in this container and
# must not be anywhere else.
#
# The section is `AiService`, not `Ai`. A key that binds to nothing leaves the
# token empty and every AI call refused, with nothing at startup to say why.
ENV AiService__BaseUrl=http://127.0.0.1:8099

# The platform tells us which port to listen on; 8080 is only the local default.
ENV PORT=8080
EXPOSE 8080

# Not root. Nothing here needs to be.
RUN useradd --create-home --uid 10001 wordos \
    && chown -R wordos:wordos /app /opt/ai-venv
USER wordos

ENTRYPOINT ["/app/entrypoint.sh"]
