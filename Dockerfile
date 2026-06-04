# Builds the MAUI-Blazor **Web head** (maui/src/AniSync.Web): a self-contained
# ASP.NET Core app that serves the Blazor UI AND hosts the /api/v1 JSON API in a
# single process. This replaces the old root ASP.NET MVC app (AniSync.dll) as the
# Fly.io deployment — the data contract (port 8080, ANISYNC_DATA_DIR=/data SQLite
# volume) is identical, so fly.toml / docker-compose.yml carry over unchanged.
#
# The maui heads target .NET 10 (the shared AniSync.Client RCL is net9.0, which a
# net10 SDK builds fine via restored reference packs), so the SDK/runtime images
# are 10.0 rather than the old app's 8.0.
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS build
WORKDIR /src

# Publish the Web head ONLY. Publishing the maui solution would drag in the MAUI
# app head (AniSync.Maui), which needs `dotnet workload install maui` + per-OS
# toolchains we don't have in this container. Targeting the project pulls in just
# its two references: the AniSync.Client RCL and the self-contained AniSync.Server
# library that supplies the API controllers/services.
ARG WEB_PROJECT=maui/src/AniSync.Web/AniSync.Web.csproj

# copy everything
COPY . ./
# restore as a distinct layer (maui/NuGet.config pins nuget.org only, applied by
# walking up from the project dir), then build and publish a release
RUN dotnet restore "$WEB_PROJECT"
RUN dotnet publish "$WEB_PROJECT" -c Release -o /app --no-restore

# final stage/image
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
# The SqliteConfigStore writes its DB file under ANISYNC_DATA_DIR. On Fly.io that's a
# mounted volume; in plain `docker run` we mkdir the same path so it works without one.
RUN mkdir -p /data
VOLUME [ "/data" ]
EXPOSE 8080
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT [ "dotnet", "AniSync.Web.dll" ]
