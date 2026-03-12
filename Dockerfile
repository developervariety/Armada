# --- Build stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (layer cache)
COPY src/Directory.Build.props src/Directory.Build.props
COPY src/Armada.sln src/Armada.sln
COPY src/Armada.Core/Armada.Core.csproj src/Armada.Core/
COPY src/Armada.Server/Armada.Server.csproj src/Armada.Server/
COPY src/Armada.Runtimes/Armada.Runtimes.csproj src/Armada.Runtimes/
COPY src/Armada.Helm/Armada.Helm.csproj src/Armada.Helm/
RUN dotnet restore src/Armada.sln

# Build
COPY src/ src/
RUN dotnet publish src/Armada.Server -c Release -f net10.0 -o /app

# --- Runtime stage ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Armada default ports: 7890 (Admiral), 7891 (WebSocket)
EXPOSE 7890 7891

ENTRYPOINT ["dotnet", "Armada.Server.dll"]
