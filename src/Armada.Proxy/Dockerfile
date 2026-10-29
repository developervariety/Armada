# Base images. The defaults are exact patch tags. Pass a build argument to override a pin, including a digest:
#   --build-arg SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:<patch>-noble
#   --build-arg RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:<patch>-noble
#   --build-arg NODE_IMAGE=node:<patch>-alpine<version>
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.403-noble
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0.14-noble
ARG NODE_IMAGE=node:22.23.4-alpine3.24

# --- Dashboard build stage ---
FROM ${NODE_IMAGE} AS dashboard-build
WORKDIR /dashboard

COPY src/Armada.Dashboard/package.json src/Armada.Dashboard/package-lock.json* ./
RUN npm ci

COPY src/Armada.Dashboard/ ./
RUN npm run build

# --- Build stage ---
FROM ${SDK_IMAGE} AS build
WORKDIR /src

# Restore first (layer cache) using only the projects needed for the proxy image.
COPY src/Directory.Build.props src/Directory.Build.props
COPY src/Armada.Core/Armada.Core.csproj src/Armada.Core/
COPY src/Armada.Proxy/Armada.Proxy.csproj src/Armada.Proxy/
RUN dotnet restore src/Armada.Proxy/Armada.Proxy.csproj

# Build
COPY src/Armada.Core/ src/Armada.Core/
COPY src/Armada.Proxy/ src/Armada.Proxy/
COPY --from=dashboard-build /dashboard/dist/ src/Armada.Dashboard/dist/
RUN dotnet publish src/Armada.Proxy -c Release -f net10.0 -o /app --no-restore

# --- Runtime stage ---
# runtime-base holds the whole image. The final stage, runtime, adds nothing, so a
# build without --target produces the root image. runtime-nonroot is the opt-in
# variant; stage order keeps runtime last.
FROM ${RUNTIME_IMAGE} AS runtime-base
WORKDIR /app
COPY --from=build /app .

# Armada.Proxy default port: 7893
EXPOSE 7893

ENTRYPOINT ["dotnet", "Armada.Proxy.dll"]

# --- Non-root runtime stage (opt-in: --target runtime-nonroot) ---
# Runs the proxy as ARMADA_UID:ARMADA_GID instead of root. The account owns
# /app/data (state and logs). The application files under /app stay root-owned
# and read-only to the account. The container UID must equal ARMADA_UID at run
# time, and the data volumes must be owned by it (docs/DOCKER.md, Non-root
# containers). An existing account at ARMADA_UID is reused; ARMADA_UID 0 is rejected.
FROM runtime-base AS runtime-nonroot
ARG ARMADA_UID=1654
ARG ARMADA_GID=1654
RUN set -eu \
    && if [ "${ARMADA_UID}" = "0" ] || [ "${ARMADA_GID}" = "0" ]; then echo "ARMADA_UID and ARMADA_GID must not be 0" >&2; exit 1; fi \
    && if ! getent group "${ARMADA_GID}" >/dev/null; then groupadd --gid "${ARMADA_GID}" armada; fi \
    && if getent passwd "${ARMADA_UID}" >/dev/null; then \
         usermod --home /home/armada "$(getent passwd "${ARMADA_UID}" | cut -d: -f1)"; \
       else \
         useradd --no-log-init --uid "${ARMADA_UID}" --gid "${ARMADA_GID}" --no-create-home --home-dir /home/armada --shell /usr/sbin/nologin armada; \
       fi \
    && mkdir -p /home/armada /app/data/logs \
    && chmod 0700 /home/armada \
    && chown -R "${ARMADA_UID}:${ARMADA_GID}" /home/armada /app/data
ENV HOME=/home/armada
USER ${ARMADA_UID}:${ARMADA_GID}

# --- Default runtime stage (root) ---
# Last stage, so a build without --target selects it. Its content is runtime-base unchanged.
FROM runtime-base AS runtime
