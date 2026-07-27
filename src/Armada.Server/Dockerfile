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
    && npm install -g @anthropic-ai/claude-code @openai/codex @google/gemini-cli @nothumanwork/cursor-agent-cli opencode-ai mux \
    && apt-get purge -y --auto-remove gnupg \
    && rm -rf /var/lib/apt/lists/* /root/.npm

COPY --from=build /app .

# Armada default ports: 7890 (Admiral + WebSocket at /ws), 7891 (MCP)
EXPOSE 7890 7891

ENTRYPOINT ["dotnet", "Armada.Server.dll"]
