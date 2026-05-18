FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/Harness.Common/Harness.Common.csproj src/Harness.Common/
COPY src/Harness.ScenarioPlayer/Harness.ScenarioPlayer.csproj src/Harness.ScenarioPlayer/
COPY src/Pacs.OperatorUi/Pacs.OperatorUi.csproj src/Pacs.OperatorUi/
RUN dotnet restore src/Pacs.OperatorUi/Pacs.OperatorUi.csproj

COPY src/Harness.Common/ src/Harness.Common/
COPY src/Harness.ScenarioPlayer/ src/Harness.ScenarioPlayer/
COPY src/Pacs.OperatorUi/ src/Pacs.OperatorUi/
RUN dotnet publish src/Pacs.OperatorUi/Pacs.OperatorUi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Pacs.OperatorUi.dll"]
