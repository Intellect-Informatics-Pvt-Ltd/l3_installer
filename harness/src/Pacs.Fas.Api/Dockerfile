FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/Harness.Common/Harness.Common.csproj src/Harness.Common/
COPY src/Pacs.Fas.Api/Pacs.Fas.Api.csproj src/Pacs.Fas.Api/
RUN dotnet restore src/Pacs.Fas.Api/Pacs.Fas.Api.csproj

COPY src/Harness.Common/ src/Harness.Common/
COPY src/Pacs.Fas.Api/ src/Pacs.Fas.Api/
RUN dotnet publish src/Pacs.Fas.Api/Pacs.Fas.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Pacs.Fas.Api.dll"]
