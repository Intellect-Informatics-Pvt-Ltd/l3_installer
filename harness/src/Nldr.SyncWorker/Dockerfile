FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/Harness.Common/Harness.Common.csproj src/Harness.Common/
COPY src/Nldr.SyncWorker/Nldr.SyncWorker.csproj src/Nldr.SyncWorker/
RUN dotnet restore src/Nldr.SyncWorker/Nldr.SyncWorker.csproj

COPY src/Harness.Common/ src/Harness.Common/
COPY src/Nldr.SyncWorker/ src/Nldr.SyncWorker/
RUN dotnet publish src/Nldr.SyncWorker/Nldr.SyncWorker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Nldr.SyncWorker.dll"]
