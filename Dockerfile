# Adjust DOTNET_OS_VERSION as desired
ARG DOTNET_OS_VERSION="-alpine"
ARG DOTNET_SDK_VERSION=8.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION}${DOTNET_OS_VERSION} AS build
WORKDIR /src

# copy everything
COPY . ./
# restore as distinct layers
RUN dotnet restore
# build and publish a release
RUN dotnet publish -c Release -o /app

# final stage/image
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_SDK_VERSION}
ENV ASPNETCORE_URLS http://+:8080
ENV ASPNETCORE_ENVIRONMENT Production
# The SqliteConfigStore writes its DB file under ANISYNC_DATA_DIR. On Fly.io that's a
# mounted volume; in plain `docker run` we mkdir the same path so it works without one.
RUN mkdir -p /data
VOLUME [ "/data" ]
EXPOSE 8080
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT [ "dotnet", "AniSync.dll" ]
