FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/Harness.Common/Harness.Common.csproj src/Harness.Common/
COPY src/Harness.ScenarioPlayer/Harness.ScenarioPlayer.csproj src/Harness.ScenarioPlayer/
COPY src/Nldr.DashboardUi/Nldr.DashboardUi.csproj src/Nldr.DashboardUi/
RUN dotnet restore src/Nldr.DashboardUi/Nldr.DashboardUi.csproj

COPY src/Harness.Common/ src/Harness.Common/
COPY src/Harness.ScenarioPlayer/ src/Harness.ScenarioPlayer/
COPY src/Nldr.DashboardUi/ src/Nldr.DashboardUi/
RUN dotnet publish src/Nldr.DashboardUi/Nldr.DashboardUi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nldr.DashboardUi.dll"]
