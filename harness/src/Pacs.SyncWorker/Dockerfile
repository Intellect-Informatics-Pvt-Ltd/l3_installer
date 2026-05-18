FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/Harness.Common/Harness.Common.csproj src/Harness.Common/
COPY src/Pacs.SyncWorker/Pacs.SyncWorker.csproj src/Pacs.SyncWorker/
RUN dotnet restore src/Pacs.SyncWorker/Pacs.SyncWorker.csproj

COPY src/Harness.Common/ src/Harness.Common/
COPY src/Pacs.SyncWorker/ src/Pacs.SyncWorker/
RUN dotnet publish src/Pacs.SyncWorker/Pacs.SyncWorker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Pacs.SyncWorker.dll"]
