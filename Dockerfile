# PGPM's image (docs/03 §2.4), as PNMTD's.
#
# Both build stages run on the build platform: the web UI is plain files, and the SDK stage publishes one portable,
# framework-dependent build that runs on amd64 and arm64 alike, restored in locked mode from the committed lock files.
# A multi-arch image compiles nothing under emulation.

# --- 1. the web UI (PGPM.WebUI → dist/, served at /) ------------------------------------------------------------------
# Node runs here, or in the containers of start.sh, run-tests.sh and tools/npm.sh: never on a host. Node 24 (LTS) on
# Debian rather than Alpine: musl's resolver fails a whole lookup when Podman's DNS answers AAAA with SERVFAIL. npm ci
# installs exactly the lock file, without install scripts. The build itself runs without a network (supply chain): the
# build tools can neither fetch nor send anything from there.
FROM --platform=$BUILDPLATFORM docker.io/library/node:24-slim AS web
WORKDIR /web
ENV NPM_CONFIG_UPDATE_NOTIFIER=false NPM_CONFIG_FUND=false NPM_CONFIG_AUDIT=false
COPY PGPM.WebUI/package.json PGPM.WebUI/package-lock.json ./
RUN npm ci --ignore-scripts --loglevel=error
COPY PGPM.WebUI/ ./
RUN --network=none npm run build

# --- 2. the .NET app ---------------------------------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
# The projects and their lock files first, restored in a layer of their own: a change to the code alone reuses it.
COPY PGPM/PGPM.csproj PGPM/packages.lock.json PGPM/
COPY PGPM.Lib/PGPM.Lib.csproj PGPM.Lib/packages.lock.json PGPM.Lib/
RUN dotnet restore PGPM/PGPM.csproj --locked-mode
COPY PGPM/ PGPM/
COPY PGPM.Lib/ PGPM.Lib/
# The configuration packages' schema, embedded into the app with the built-in packages (docs/03 §17).
COPY spec/config-package.schema.json spec/
COPY --from=web /web/dist PGPM/wwwroot/
# The restore brought everything the publish needs; the publish, where the analyzers and source generators run, gets
# no network either.
RUN --network=none dotnet publish PGPM/PGPM.csproj -c Release -o /app --no-restore -p:UseAppHost=false \
 && rm -f /app/*.pdb

# --- 3. the runtime ----------------------------------------------------------------------------------------------------
# Ubuntu, Microsoft's default .NET 10 image – not Alpine: GGITSV found musl failing to resolve
# login.microsoftonline.com under Podman's DNS (an AAAA SERVFAIL fails the whole lookup).
FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="PGPM" \
      org.opencontainers.image.description="PGPM: offline-first innovation portfolio – ideas, leads, projects and maps."
# wget for the health check. libgssapi-krb5-2: Npgsql probes for it at every start and complains loudly when it is
# missing. /var/lib/pgpm belongs to the image's non-root user (app, uid 1654), who runs the app: keys/ is the Data
# Protection key ring (a volume), files/ the mount point of the attachments' folder on the host. `pgpm …` is the app's
# command line.
RUN apt-get update \
 && apt-get install -y --no-install-recommends wget libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /var/lib/pgpm/keys /var/lib/pgpm/files \
 && chown -R app:app /var/lib/pgpm \
 && printf '#!/bin/sh\nexec dotnet /app/PGPM.dll "$@"\n' > /usr/local/bin/pgpm \
 && chmod 0755 /usr/local/bin/pgpm
COPY --from=build /app /app
USER app
WORKDIR /app
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8080
# 200 while the database answers. The compose file repeats it: Podman's OCI images drop HEALTHCHECK.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD wget -q -O /dev/null "http://127.0.0.1:${ASPNETCORE_HTTP_PORTS:-8080}/healthz" || exit 1
ENTRYPOINT ["dotnet", "/app/PGPM.dll"]
