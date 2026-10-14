# syntax=docker/dockerfile:1
# Multi-stage build producing two images from one source tree:
#   docker build --target api    -t ledgercore-api .
#   docker build --target worker -t ledgercore-worker .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached unless project files change).
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/LedgerCore.Domain/LedgerCore.Domain.csproj src/LedgerCore.Domain/
COPY src/LedgerCore.Application/LedgerCore.Application.csproj src/LedgerCore.Application/
COPY src/LedgerCore.Infrastructure/LedgerCore.Infrastructure.csproj src/LedgerCore.Infrastructure/
COPY src/LedgerCore.Api/LedgerCore.Api.csproj src/LedgerCore.Api/
COPY src/LedgerCore.Worker/LedgerCore.Worker.csproj src/LedgerCore.Worker/
RUN dotnet restore src/LedgerCore.Api/LedgerCore.Api.csproj \
 && dotnet restore src/LedgerCore.Worker/LedgerCore.Worker.csproj

COPY src/ src/
RUN dotnet publish src/LedgerCore.Api/LedgerCore.Api.csproj -c Release -o /out/api --no-restore \
 && dotnet publish src/LedgerCore.Worker/LedgerCore.Worker.csproj -c Release -o /out/worker --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api .
# Non-root user provided by the official .NET images.
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "LedgerCore.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS worker
WORKDIR /app
COPY --from=build /out/worker .
USER $APP_UID
ENTRYPOINT ["dotnet", "LedgerCore.Worker.dll"]
