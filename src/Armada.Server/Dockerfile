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
RUN dotnet publish src/Armada.Server -c Release -f net10.0 -o /app --no-restore

# Build the Mux runtime (jchristn/Mux, .NET) from source and stage it to a fixed
# tool path. This is the CLI agent Armada drives via `mux print` -- NOT the
# unrelated npm "coder multiplexer" that also claims the `mux` name and whose
# CLI (`mux run --dir --quiet`) and headless behavior are incompatible.
RUN apt-get update && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/* \
    && git clone --depth 1 https://github.com/jchristn/Mux.git /tmp/Mux \
    && dotnet pack /tmp/Mux/src/Mux.Cli/Mux.Cli.csproj -c Release -p:TargetFrameworks=net10.0 -o /tmp/mux-pkg \
    && dotnet tool install --tool-path /opt/mux --framework net10.0 --disable-parallel --add-source /tmp/mux-pkg Mux.Cli \
    && rm -rf /tmp/Mux /tmp/mux-pkg

# --- Runtime stage ---
# Captains run repository checks inside mission docks. Use the SDK image so
# .NET vessel gates can honor repo global.json files instead of failing after
# the agent has completed.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gh git glab gnupg \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key \
        | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_22.x nodistro main" \
        > /etc/apt/sources.list.d/nodesource.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install -g @anthropic-ai/claude-code @openai/codex @google/gemini-cli @nothumanwork/cursor-agent-cli opencode-ai \
    && apt-get purge -y --auto-remove gnupg \
    && rm -rf /var/lib/apt/lists/* /root/.npm

COPY --from=build /app .

# jchristn/Mux runtime (built in the build stage). Expose it on PATH as `mux`
# so Armada's Mux runtime launches it. The npm `mux` (coder) is intentionally
# NOT installed above.
COPY --from=build /opt/mux /opt/mux
RUN ln -sf /opt/mux/mux /usr/local/bin/mux

# Armada default ports: 7890 (Admiral + WebSocket at /ws), 7891 (MCP)
EXPOSE 7890 7891

ENTRYPOINT ["dotnet", "Armada.Server.dll"]
