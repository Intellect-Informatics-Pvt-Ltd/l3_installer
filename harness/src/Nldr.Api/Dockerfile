FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/Harness.Common/Harness.Common.csproj src/Harness.Common/
COPY src/Nldr.Api/Nldr.Api.csproj src/Nldr.Api/
RUN dotnet restore src/Nldr.Api/Nldr.Api.csproj

COPY src/Harness.Common/ src/Harness.Common/
COPY src/Nldr.Api/ src/Nldr.Api/
RUN dotnet publish src/Nldr.Api/Nldr.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nldr.Api.dll"]
