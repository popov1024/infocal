# ── Build stage ──
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore
COPY src/Infocal.Scraper/Infocal.Scraper.csproj src/Infocal.Scraper/
RUN dotnet restore src/Infocal.Scraper/Infocal.Scraper.csproj

# Publish
COPY src/Infocal.Scraper/ src/Infocal.Scraper/
RUN dotnet publish src/Infocal.Scraper/Infocal.Scraper.csproj -c Release -o /app

# ── Runtime stage ──
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

# Volume for future state persistence
VOLUME /app/data

ENV DOTNET_ENVIRONMENT=Production

COPY --from=build /app .

ENTRYPOINT ["dotnet", "Infocal.Scraper.dll"]
