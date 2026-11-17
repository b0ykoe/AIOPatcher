# syntax=docker/dockerfile:1
#
# Build-Argumente:
#   VERSION            Versionsnummer für Server und Agents (z. B. 0.2.0, im CI aus dem Git-Tag)
#   AGENT_RELEASE_KEY  öffentlicher Schlüssel (Base64) für signierte Agent-Self-Updates
# Build-Secret (optional):
#   agent_signing_key  privater Schlüssel (PEM); damit werden die Agent-Binaries signiert (*.sig)
# Ohne Schlüssel entsteht ein funktionierendes Image, nur ohne automatische Agent-Updates.

ARG VERSION=0.1.0

# --- Frontend ---------------------------------------------------------------------------------------
FROM node:24-alpine AS web
WORKDIR /src/web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci
COPY src/web/ ./
RUN npx tsc --noEmit && npx vite build --outDir /out/wwwroot --emptyOutDir

# --- Server -----------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/AIOPatcher.Contracts/AIOPatcher.Contracts.csproj src/AIOPatcher.Contracts/
COPY src/AIOPatcher.Server/AIOPatcher.Server.csproj src/AIOPatcher.Server/
RUN dotnet restore src/AIOPatcher.Server
COPY src/AIOPatcher.Contracts/ src/AIOPatcher.Contracts/
COPY src/AIOPatcher.Server/ src/AIOPatcher.Server/
RUN dotnet publish src/AIOPatcher.Server -c Release -o /app --no-restore -p:Version=$VERSION

# --- Agents -----------------------------------------------------------------------------------------
# Alle Agents als Self-contained Single-File (ohne Native AOT): laufen ab glibc 2.27 (Ubuntu 18.04, Debian 10),
# brauchen beim Bau keine native Toolchain und deutlich weniger Arbeitsspeicher.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS agents
ARG VERSION
ARG AGENT_RELEASE_KEY=""
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/AIOPatcher.Contracts/ src/AIOPatcher.Contracts/
COPY src/AIOPatcher.Agent/ src/AIOPatcher.Agent/
COPY tools/AIOPatcher.Signer/ tools/AIOPatcher.Signer/
RUN for rid in linux-x64 linux-arm64 win-x64; do \
      dotnet publish src/AIOPatcher.Agent -c Release -r $rid -o /out/$rid -p:Version=$VERSION -p:AgentReleaseKey="$AGENT_RELEASE_KEY" || exit 1; \
    done \
 && find /out -type f ! -name 'aiopatcher-agent' ! -name 'aiopatcher-agent.exe' -delete
# Signieren nur, wenn der private Schlüssel als Build-Secret mitgegeben wurde.
RUN --mount=type=secret,id=agent_signing_key,required=false \
    if [ -s /run/secrets/agent_signing_key ]; then \
      dotnet build tools/AIOPatcher.Signer -c Release -o /signer -v q \
      && for f in /out/*/aiopatcher-agent*; do dotnet /signer/aiopatcher-signer.dll sign "$f" /run/secrets/agent_signing_key || exit 1; done; \
    else \
      echo "No agent_signing_key secret: agents are not signed, self-update stays off."; \
    fi

# --- Runtime ----------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION
LABEL org.opencontainers.image.title="AIOPatcher" \
      org.opencontainers.image.version=$VERSION
WORKDIR /app
COPY --from=build /app ./
COPY --from=web /out/wwwroot ./wwwroot
COPY --from=agents /out ./agents
ENV AIOPatcher__DataDirectory=/data \
    AIOPatcher__DashboardPort=9443 \
    AIOPatcher__AgentPort=8443
# /data gehört dem Dienst-Benutzer: ein neues Docker-Volume (z. B. Portainer-Stack) übernimmt das, sonst gehört es root
RUN mkdir -p /data && chown app:app /data
VOLUME /data
EXPOSE 8443 9443
# Gesundheitsprüfung ohne curl im Image: der Server fragt sich selbst unter /healthz.
HEALTHCHECK --interval=60s --timeout=10s --start-period=90s --retries=3   CMD ["dotnet", "AIOPatcher.Server.dll", "healthcheck"]
# Nicht als Root laufen (UID 1654 = "app" im .NET-Image).
USER app
ENTRYPOINT ["dotnet", "AIOPatcher.Server.dll"]
