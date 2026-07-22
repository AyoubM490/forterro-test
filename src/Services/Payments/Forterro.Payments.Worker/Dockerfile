# syntax=docker/dockerfile:1.7
# Contexte de build : racine du depot.

FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/BuildingBlocks/Forterro.Messaging.Abstractions/*.csproj src/BuildingBlocks/Forterro.Messaging.Abstractions/
COPY src/BuildingBlocks/Forterro.Diagnostics/*.csproj src/BuildingBlocks/Forterro.Diagnostics/
COPY src/BuildingBlocks/Forterro.Banking/*.csproj src/BuildingBlocks/Forterro.Banking/
COPY src/BuildingBlocks/Forterro.Messaging.Kafka/*.csproj src/BuildingBlocks/Forterro.Messaging.Kafka/
COPY src/BuildingBlocks/Forterro.Outbox.EntityFrameworkCore/*.csproj src/BuildingBlocks/Forterro.Outbox.EntityFrameworkCore/
COPY src/BuildingBlocks/Forterro.BuildingBlocks/*.csproj src/BuildingBlocks/Forterro.BuildingBlocks/
COPY src/Contracts/Forterro.Contracts/*.csproj src/Contracts/Forterro.Contracts/
COPY src/Services/Payments/Forterro.Payments.Worker/*.csproj src/Services/Payments/Forterro.Payments.Worker/

# sharing=locked : cache partage avec les autres services construits en parallele.
# Sans verrou, les restores concurrents corrompent l'extraction des paquets.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Services/Payments/Forterro.Payments.Worker/Forterro.Payments.Worker.csproj

COPY src/ src/

RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Services/Payments/Forterro.Payments.Worker/Forterro.Payments.Worker.csproj \
        -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS final
WORKDIR /app

RUN adduser --disabled-password --no-create-home --uid 10001 forterro
USER 10001

COPY --from=build --chown=10001:10001 /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1

EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
    CMD wget --spider -q http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Forterro.Payments.Worker.dll"]
