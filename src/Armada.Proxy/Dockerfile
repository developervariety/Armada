# --- Build stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (layer cache) using only the projects needed for the proxy image.
COPY src/Directory.Build.props src/Directory.Build.props
COPY src/Armada.Core/Armada.Core.csproj src/Armada.Core/
COPY src/Armada.Proxy/Armada.Proxy.csproj src/Armada.Proxy/
RUN dotnet restore src/Armada.Proxy/Armada.Proxy.csproj

# Build
COPY src/Armada.Core/ src/Armada.Core/
COPY src/Armada.Proxy/ src/Armada.Proxy/
RUN dotnet publish src/Armada.Proxy -c Release -f net10.0 -o /app --no-restore

# --- Runtime stage ---
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Armada.Proxy default port: 7893
EXPOSE 7893

ENTRYPOINT ["dotnet", "Armada.Proxy.dll"]
