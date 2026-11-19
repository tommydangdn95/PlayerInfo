# ---- Admin UI (React + Vite) ----
FROM node:24-alpine AS web
WORKDIR /web

COPY src/PlayerInfo.Web/package.json src/PlayerInfo.Web/package-lock.json ./
RUN npm ci --no-fund --no-audit

COPY src/PlayerInfo.Web/ ./
RUN npm run build -- --outDir dist --emptyOutDir

# ---- API (.NET 9) ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["src/PlayerInfo.Api/PlayerInfo.Api.csproj", "src/PlayerInfo.Api/"]
RUN dotnet restore "src/PlayerInfo.Api/PlayerInfo.Api.csproj"

COPY src/PlayerInfo.Api/ src/PlayerInfo.Api/
WORKDIR "/src/src/PlayerInfo.Api"
RUN dotnet publish "PlayerInfo.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .
COPY --from=web /web/dist ./wwwroot
ENTRYPOINT ["dotnet", "PlayerInfo.Api.dll"]
