# syntax=docker/dockerfile:1.7
# Contexte de build attendu : la racine du depot.
#   docker build -f src/Services/Intelligence/Forterro.Intelligence.Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# Les fichiers de projet d'abord : tant qu'aucune dependance ne change,
# le restore reste en cache meme si tout le code source a change.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/BuildingBlocks/Forterro.Messaging.Abstractions/*.csproj src/BuildingBlocks/Forterro.Messaging.Abstractions/
COPY src/BuildingBlocks/Forterro.Diagnostics/*.csproj src/BuildingBlocks/Forterro.Diagnostics/
COPY src/BuildingBlocks/Forterro.Banking/*.csproj src/BuildingBlocks/Forterro.Banking/
COPY src/BuildingBlocks/Forterro.Messaging.Kafka/*.csproj src/BuildingBlocks/Forterro.Messaging.Kafka/
COPY src/BuildingBlocks/Forterro.Outbox.EntityFrameworkCore/*.csproj src/BuildingBlocks/Forterro.Outbox.EntityFrameworkCore/
COPY src/BuildingBlocks/Forterro.BuildingBlocks/*.csproj src/BuildingBlocks/Forterro.BuildingBlocks/
COPY src/Services/Intelligence/Forterro.Intelligence.Api/*.csproj src/Services/Intelligence/Forterro.Intelligence.Api/

# sharing=locked : les trois services se construisent en parallele et partagent
# ce cache. Sans verrou, deux restores concurrents se marchent dessus pendant
# l'extraction des paquets et l'un des deux echoue sur un fichier temporaire
# disparu ("Could not find file '/root/.nuget/packages/<pkg>/<version>/xxxx.yyy'").
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/Services/Intelligence/Forterro.Intelligence.Api/Forterro.Intelligence.Api.csproj \
        --runtime linux-musl-x64

COPY src/ src/

# --runtime linux-musl-x64 : SEUL service a en avoir besoin, et pas par gout de la
# precision. Ce service embarque PDFium et SkiaSharp, qui livrent leurs binaires pour
# sept plateformes. Un publish portable les copie TOUTES : image mesuree a 724 Mo,
# contre 191 Mo pour les trois autres services. En fixant le RID, seul le binaire
# musl-x64 est retenu — c'est le seul que ce conteneur pourra jamais charger.
#
# --self-contained false : on fixe la plateforme des natifs, pas le runtime .NET, qui
# reste celui de l'image de base et continue de recevoir ses correctifs de securite
# par simple rebuild.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Services/Intelligence/Forterro.Intelligence.Api/Forterro.Intelligence.Api.csproj \
        -c Release -o /app/publish --no-restore \
        --runtime linux-musl-x64 --self-contained false

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS final
WORKDIR /app

# Utilisateur non root : une CVE d'execution de code arbitraire ne donne pas root
# dans le conteneur. Exige par la plupart des Pod Security Standards.
RUN adduser --disabled-password --no-create-home --uid 10001 forterro
USER 10001

COPY --from=build --chown=10001:10001 /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1 \
    DOTNET_TieredPGO=1

EXPOSE 8080

# Sonde de secours pour docker-compose ; en Kubernetes ce sont les probes
# du manifest qui font foi.
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
    CMD wget --spider -q http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Forterro.Intelligence.Api.dll"]
