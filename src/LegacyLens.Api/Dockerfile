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

# The index is a file, so it needs somewhere to live that survives the container.
VOLUME /data
ENV INDEX_PATH=/data/index.db \
    ASPNETCORE_URLS=http://+:8080

# Unlike the CV generator, nothing here needs root.
RUN useradd --create-home --uid 1000 lens && chown -R lens:lens /app
USER lens

EXPOSE 8080
ENTRYPOINT ["dotnet", "LegacyLens.Api.dll"]
