# Build context is the repository root:
#   docker build -f src/Services/Remit.Ledger/Dockerfile -t remit/ledger .
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY nuget.config Directory.Build.props ./
COPY src/BuildingBlocks/Remit.BuildingBlocks/Remit.BuildingBlocks.csproj src/BuildingBlocks/Remit.BuildingBlocks/
COPY src/Services/Remit.Ledger/Remit.Ledger.csproj src/Services/Remit.Ledger/
RUN dotnet restore src/Services/Remit.Ledger/Remit.Ledger.csproj
COPY src/ src/
RUN dotnet publish src/Services/Remit.Ledger/Remit.Ledger.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app .
USER app
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s CMD wget -qO- http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "Remit.Ledger.dll"]
