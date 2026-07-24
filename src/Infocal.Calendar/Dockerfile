# ── Build stage ──
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore
COPY src/Infocal.Calendar/Infocal.Calendar.csproj src/Infocal.Calendar/
RUN dotnet restore src/Infocal.Calendar/Infocal.Calendar.csproj

# Publish
COPY src/Infocal.Calendar/ src/Infocal.Calendar/
RUN dotnet publish src/Infocal.Calendar/Infocal.Calendar.csproj -c Release -o /app

# ── Runtime stage ──
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Persist SQLite database
VOLUME /app/data

ENV ConnectionStrings__Default="Data Source=/app/data/calendar.db"
ENV ASPNETCORE_URLS=http://+:80

EXPOSE 80

COPY --from=build /app .

ENTRYPOINT ["dotnet", "Infocal.Calendar.dll"]
