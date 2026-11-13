# --- Frontend ---------------------------------------------------------------------------------------
FROM node:24-alpine AS web
WORKDIR /src/web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci
COPY src/web/ ./
RUN npx tsc --noEmit && npx vite build --outDir /out/wwwroot --emptyOutDir

# --- Server -----------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/AIOPatcher.Contracts/AIOPatcher.Contracts.csproj src/AIOPatcher.Contracts/
COPY src/AIOPatcher.Server/AIOPatcher.Server.csproj src/AIOPatcher.Server/
RUN dotnet restore src/AIOPatcher.Server
COPY src/AIOPatcher.Contracts/ src/AIOPatcher.Contracts/
COPY src/AIOPatcher.Server/ src/AIOPatcher.Server/
RUN dotnet publish src/AIOPatcher.Server -c Release -o /app --no-restore

# --- Agents -----------------------------------------------------------------------------------------
# linux-x64 wird mit Native AOT gebaut und braucht dafür clang. Das Image muss daher auf x64 gebaut werden.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS agents
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/AIOPatcher.Contracts/ src/AIOPatcher.Contracts/
COPY src/AIOPatcher.Agent/ src/AIOPatcher.Agent/
RUN dotnet publish src/AIOPatcher.Agent -c Release -r linux-x64   -o /out/linux-x64 \
 && dotnet publish src/AIOPatcher.Agent -c Release -r linux-arm64 -o /out/linux-arm64 \
 && dotnet publish src/AIOPatcher.Agent -c Release -r win-x64     -o /out/win-x64 \
 && find /out -type f ! -name 'aiopatcher-agent' ! -name 'aiopatcher-agent.exe' -delete

# --- Runtime ----------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
COPY --from=web /out/wwwroot ./wwwroot
COPY --from=agents /out ./agents
ENV AIOPatcher__DataDirectory=/data \
    AIOPatcher__DashboardPort=9443 \
    AIOPatcher__AgentPort=8443
VOLUME /data
EXPOSE 8443 9443
# Nicht als Root laufen (UID 1654 = "app" im .NET-Image).
USER app
ENTRYPOINT ["dotnet", "AIOPatcher.Server.dll"]
