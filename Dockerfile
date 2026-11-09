# syntax=docker/dockerfile:1

# ---- Build the management web app -------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json VintageStoryServerManager.slnx ./
COPY src/VintageStoryServerManager.Core/*.csproj src/VintageStoryServerManager.Core/
COPY src/VintageStoryServerManager.Infrastructure/*.csproj src/VintageStoryServerManager.Infrastructure/
COPY src/VintageStoryServerManager.Web/*.csproj src/VintageStoryServerManager.Web/
# Restore from the project files alone so the package layer stays cached between source changes.
RUN dotnet restore src/VintageStoryServerManager.Web/VintageStoryServerManager.Web.csproj
COPY src/ src/
# Publish restores again (no --no-restore): with only .csproj files present, the restore above
# can't see the Razor components, so it leaves out the package that provides
# _framework/blazor.web.js. Without that file nothing in the UI becomes interactive.
RUN dotnet publish src/VintageStoryServerManager.Web/VintageStoryServerManager.Web.csproj \
        -c Release -o /app /p:UseAppHost=false \
    && test -f /app/wwwroot/_framework/blazor.web.js

# ---- Download the Vintage Story dedicated server ----------------------------
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS vintagestory
ARG VS_VERSION=1.22.7
ARG VS_CHANNEL=stable
# Optional: md5 from https://api.vintagestory.at/stable.json ("linuxserver" entry).
ARG VS_MD5=""
ADD https://cdn.vintagestory.at/gamefiles/${VS_CHANNEL}/vs_server_linux-x64_${VS_VERSION}.tar.gz /tmp/vs_server.tar.gz
RUN if [ -n "$VS_MD5" ]; then echo "$VS_MD5  /tmp/vs_server.tar.gz" | md5sum -c -; fi \
    && mkdir -p /opt/vintagestory \
    && tar -xzf /tmp/vs_server.tar.gz -C /opt/vintagestory \
    && printf '%s' "$VS_VERSION" > /opt/vintagestory/.vsversion \
    && rm /tmp/vs_server.tar.gz

# ---- Runtime: Vintage Story server + manager (both run on .NET 10) ----------
# The manager runs the game as a child process with the same dotnet host. Other server
# versions can be downloaded into /data/game at runtime from the Game versions page.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VS_VERSION=1.22.7
LABEL org.opencontainers.image.title="Vintage Story Server Manager" \
      org.opencontainers.image.description="Vintage Story dedicated server with a web management interface" \
      org.opencontainers.image.source="https://github.com/Kayrah87/VintageStoryServerManager"

COPY --from=vintagestory /opt/vintagestory /opt/vintagestory
COPY --from=build /app /app

ENV VS_VERSION=${VS_VERSION} \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DataPaths__Root=/data \
    DataPaths__GameInstallDirectory=/opt/vintagestory

RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME ["/data"]
COPY --chmod=755 docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh

# 8080: management web UI (publish as host port 80). 42420: game server.
EXPOSE 8080 42420/tcp 42420/udp

# Starts as root only to switch to PUID:PGID (default 1654, the image's app user); see
# docker-entrypoint.sh. Nothing else runs as root.
WORKDIR /app
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["dotnet", "/app/VintageStoryServerManager.Web.dll"]
