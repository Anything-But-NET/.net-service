FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["Copilot.Gateway3.sln", "./"]
COPY ["Copilot.Contracts/Copilot.Contracts.csproj", "Copilot.Contracts/"]
COPY ["Copilot.IngestionWorker/Copilot.IngestionWorker.csproj", "Copilot.IngestionWorker/"]
COPY ["Copilot.TeamsBotApi/Copilot.TeamsBotApi.csproj", "Copilot.TeamsBotApi/"]

RUN dotnet restore "./Copilot.Gateway3.sln"

COPY . .
WORKDIR "/src/Copilot.IngestionWorker"
RUN dotnet build "./Copilot.IngestionWorker.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Copilot.IngestionWorker.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Copilot.IngestionWorker.dll"]