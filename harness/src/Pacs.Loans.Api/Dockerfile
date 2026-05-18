FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/Harness.Common/Harness.Common.csproj src/Harness.Common/
COPY src/Pacs.Loans.Api/Pacs.Loans.Api.csproj src/Pacs.Loans.Api/
RUN dotnet restore src/Pacs.Loans.Api/Pacs.Loans.Api.csproj

COPY src/Harness.Common/ src/Harness.Common/
COPY src/Pacs.Loans.Api/ src/Pacs.Loans.Api/
RUN dotnet publish src/Pacs.Loans.Api/Pacs.Loans.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Pacs.Loans.Api.dll"]
