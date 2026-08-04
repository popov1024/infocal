# ── Build stage ──
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Infocal.Admin/Infocal.Admin.csproj src/Infocal.Admin/
RUN dotnet restore src/Infocal.Admin/Infocal.Admin.csproj

COPY src/Infocal.Admin/ src/Infocal.Admin/
RUN dotnet publish src/Infocal.Admin/Infocal.Admin.csproj -c Release -o /app

# ── Runtime stage ──
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80

COPY --from=build /app .

ENTRYPOINT ["dotnet", "Infocal.Admin.dll"]
