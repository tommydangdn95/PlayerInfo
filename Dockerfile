FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["src/PlayerInfo.Api/PlayerInfo.Api.csproj", "src/PlayerInfo.Api/"]
RUN dotnet restore "src/PlayerInfo.Api/PlayerInfo.Api.csproj"

COPY src/ src/
WORKDIR "/src/src/PlayerInfo.Api"
RUN dotnet publish "PlayerInfo.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "PlayerInfo.Api.dll"]
