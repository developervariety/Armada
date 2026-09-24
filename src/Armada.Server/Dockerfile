# --- Mux runtime stage ---
# Build the Mux runtime (jchristn/Mux, .NET) from source and stage it to a fixed
# tool path. This is the CLI agent Armada drives via `mux print` -- NOT the
# unrelated npm "coder multiplexer" that also claims the `mux` name and whose
# CLI (`mux run --dir --quiet`) and headless behavior are incompatible.
#
# Mux is built against a patched Voltaic (its MCP client library). Voltaic's
# Streamable HTTP client reads a POST response only as a plain JSON body and
# requires a string `ping` result, so it loads no tool from a spec-correct
# server that answers with a text/event-stream body and `ping` -> {} (the Armada
# MCP endpoint does both). docker/patches/voltaic-mcp-http-client.patch reads an
# event-stream response as the event whose JSON-RPC id matches the request and
# accepts any JSON `ping` result. It is applied to the Voltaic revision Mux pins
# (VOLTAIC_REF), packed as VOLTAIC_VERSION, and Mux restores Voltaic only from
# that local package. The build fails when the patch does not apply or the
# staged Voltaic.dll is not the patched build.
#
# This stage must not depend on the Armada source tree beyond the patch file. A
# step placed after the source COPY is invalidated by every commit, and each
# rebuild leaves a fresh multi-gigabyte record in the builder cache. Keep
# source-independent work in a stage of its own so it is built once and reused.
# Change MUX_REF (an exact commit) to pick up a new Mux revision, and move
# VOLTAIC_REF to the Voltaic release that revision references; the layer is
# cached until an argument or the patch changes.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS mux
ARG MUX_REF=ff0e00b0b878d025958bd0696a4e8dbb0920e18a
ARG VOLTAIC_REF=ef7717c1e86a3c23c34423fae86804f728ce5e68
ARG VOLTAIC_BASE_VERSION=0.7.1
ARG VOLTAIC_VERSION=0.7.1-armada.1
COPY docker/patches/voltaic-mcp-http-client.patch /tmp/voltaic-mcp-http-client.patch
RUN set -eu \
    && apt-get update && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/* \
    && git init -q /tmp/Voltaic \
    && git -C /tmp/Voltaic fetch -q --depth 1 https://github.com/jchristn/Voltaic.git "${VOLTAIC_REF}" \
    && git -C /tmp/Voltaic checkout -q FETCH_HEAD \
    && git -C /tmp/Voltaic apply --verbose /tmp/voltaic-mcp-http-client.patch \
    && dotnet pack /tmp/Voltaic/src/Voltaic/Voltaic.csproj -c Release -p:GeneratePackageOnBuild=false -p:Version="${VOLTAIC_VERSION}" -o /tmp/voltaic-pkg \
    && git init -q /tmp/Mux \
    && git -C /tmp/Mux fetch -q --depth 1 https://github.com/jchristn/Mux.git "${MUX_REF}" \
    && git -C /tmp/Mux checkout -q FETCH_HEAD \
    && grep -q "Include=\"Voltaic\" Version=\"${VOLTAIC_BASE_VERSION}\"" /tmp/Mux/src/Mux.Core/Mux.Core.csproj \
    && sed -i "s|Include=\"Voltaic\" Version=\"${VOLTAIC_BASE_VERSION}\"|Include=\"Voltaic\" Version=\"${VOLTAIC_VERSION}\"|" /tmp/Mux/src/Mux.Core/Mux.Core.csproj \
    && printf '%s\n' \
        '<?xml version="1.0" encoding="utf-8"?>' \
        '<configuration>' \
        '  <packageSources>' \
        '    <clear />' \
        '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />' \
        '    <add key="voltaic-patched" value="/tmp/voltaic-pkg" />' \
        '  </packageSources>' \
        '  <packageSourceMapping>' \
        '    <packageSource key="voltaic-patched"><package pattern="Voltaic" /></packageSource>' \
        '    <packageSource key="nuget.org"><package pattern="*" /></packageSource>' \
        '  </packageSourceMapping>' \
        '</configuration>' > /tmp/Mux/NuGet.config \
    && dotnet pack /tmp/Mux/src/Mux.Cli/Mux.Cli.csproj -c Release -p:TargetFrameworks=net10.0 -o /tmp/mux-pkg \
    && dotnet tool install --tool-path /opt/mux --framework net10.0 --disable-parallel --add-source /tmp/mux-pkg Mux.Cli \
    && VOLTAIC_DLL="$(find /opt/mux/.store -name Voltaic.dll | head -n 1)" \
    && echo "Staged Voltaic: ${VOLTAIC_DLL}" \
    && grep -a -o "${VOLTAIC_VERSION}[+0-9a-f]*" "${VOLTAIC_DLL}" | head -n 1 | sed 's/^/Voltaic informational version: /' \
    && grep -a -q "${VOLTAIC_VERSION}" "${VOLTAIC_DLL}" \
    && rm -rf /tmp/Voltaic /tmp/voltaic-pkg /tmp/voltaic-mcp-http-client.patch /tmp/Mux /tmp/mux-pkg /root/.nuget/packages /tmp/NuGetScratch

# --- Build stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (layer cache) — restore individual projects, not the sln
# (sln references test projects we don't need in the image)
COPY src/Directory.Build.props src/Directory.Build.props
COPY src/Armada.Core/Armada.Core.csproj src/Armada.Core/
COPY src/Armada.Runtimes/Armada.Runtimes.csproj src/Armada.Runtimes/
COPY src/Armada.Server/Armada.Server.csproj src/Armada.Server/
RUN dotnet restore src/Armada.Server/Armada.Server.csproj

# Build
COPY src/Armada.Core/ src/Armada.Core/
COPY src/Armada.Runtimes/ src/Armada.Runtimes/
COPY src/Armada.Server/ src/Armada.Server/

# The source-revision embed target runs `git rev-parse` from the build context.
# The Docker build has no .git and no git, so the caller passes the commit here
# and it is embedded as SourceRevisionId, which the running admiral reports as
# its build commit for drift detection. An empty value keeps the pre-embed
# behaviour (an unknown running commit).
ARG GIT_SHA=""
RUN dotnet publish src/Armada.Server -c Release -f net10.0 -o /app --no-restore -p:_GitSha="$GIT_SHA"

# --- Runtime stage ---
# Captains run repository checks inside mission docks. Use the SDK image so
# .NET vessel gates can honor repo global.json files instead of failing after
# the agent has completed.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime
WORKDIR /app

# CLI_REFRESH busts this layer so @latest actually re-pulls. Pass
# --build-arg CLI_REFRESH=$(date +%s) to refresh the agent CLIs on a rebuild;
# omit it to keep the last-built versions (Docker caches this RUN otherwise).
#
# Database backup and the self-deploy preflight run the configured provider's
# native client tools inside this container. The PostgreSQL client major must
# be at least the server major, so it is pinned by package name to 16
# (pg_dump, pg_restore, psql, createdb, dropdb). The MySQL client is pinned to
# 8.0 (mysql, mysqldump). Both come from the base image's Ubuntu archive. SQL
# Server's ODBC 18 sqlcmd is not installed: it needs Microsoft's apt repository
# and an in-image EULA acceptance. Install mssql-tools18 in a derived image
# when the admiral uses SQL Server.
ARG CLI_REFRESH=0
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl default-jdk-headless gh git glab gnupg iproute2 \
        postgresql-client-16 mysql-client-8.0 \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key \
        | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_22.x nodistro main" \
        > /etc/apt/sources.list.d/nodesource.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends nodejs \
    && echo "cli-refresh=${CLI_REFRESH}" \
    && npm install -g @anthropic-ai/claude-code@latest @openai/codex@latest @google/gemini-cli@latest @nothumanwork/cursor-agent-cli@latest opencode-ai@latest \
    && apt-get purge -y --auto-remove gnupg \
    && rm -rf /var/lib/apt/lists/* /root/.npm

COPY --from=build /app .

# jchristn/Mux runtime (built in the mux stage). Expose it on PATH as `mux`
# so Armada's Mux runtime launches it. The npm `mux` (coder) is intentionally
# NOT installed above.
COPY --from=mux /opt/mux /opt/mux
RUN ln -sf /opt/mux/mux /usr/local/bin/mux

# Armada default ports: 7890 (Admiral + WebSocket at /ws), 7891 (MCP)
EXPOSE 7890 7891

ENTRYPOINT ["dotnet", "Armada.Server.dll"]
