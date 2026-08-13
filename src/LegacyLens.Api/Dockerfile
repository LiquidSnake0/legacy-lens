# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Project files first: restore is cached until a dependency actually changes.
COPY src/LegacyLens.Api/LegacyLens.Api.csproj src/LegacyLens.Api/
RUN dotnet restore src/LegacyLens.Api/LegacyLens.Api.csproj

COPY src/ src/
RUN dotnet publish src/LegacyLens.Api/LegacyLens.Api.csproj \
        -c Release -o /app --no-restore

# Run
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Nothing here needs root, and the aspnet image already ships a non-root "app"
# user, so there is none to create.
#
# This RUN must come BEFORE the VOLUME below. Docker discards any change made to
# a volume-declared path in later layers, so a chown placed after VOLUME is
# silently thrown away — the container then starts fine and fails on the first
# request with "unable to open database file".
RUN mkdir -p /data && chown -R app:app /app /data

# The index is a file, so it needs somewhere to live that survives the container.
# A named volume inherits the ownership set above.
VOLUME /data

ENV INDEX_PATH=/data/index.db \
    ASPNETCORE_URLS=http://+:8080

USER app

EXPOSE 8080
ENTRYPOINT ["dotnet", "LegacyLens.Api.dll"]
