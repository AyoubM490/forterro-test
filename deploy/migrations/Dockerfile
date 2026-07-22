# syntax=docker/dockerfile:1.7
#
# Image de migration de schema, une par service a etat.
#
# Contexte de build attendu : la racine du depot.
#   docker build -f deploy/migrations/Dockerfile \
#     --build-arg PROJECT_PATH=src/Services/Payments/Forterro.Payments.Worker \
#     -t ghcr.io/forterro/payments-migrations:1.0.0 .
#
# POURQUOI UN BUNDLE PLUTOT QUE L'APPLICATION ELLE-MEME
#
# `dotnet ef migrations bundle` produit un executable autonome qui ne contient QUE
# les migrations et le fournisseur de base. Consequences :
#   - le Job de migration n'embarque ni endpoint HTTP, ni client Kafka, ni
#     configuration OIDC : rien de tout cela n'a de sens pour appliquer un schema,
#     et tout cela pourrait echouer au demarrage pour des raisons sans rapport ;
#   - il n'exige pas le SDK .NET a l'execution ;
#   - il echoue avec un code de sortie non nul, ce dont depend le hook PreSync
#     d'ArgoCD pour interrompre la synchronisation.
#
# Un fichier partage plutot qu'un Dockerfile par service : la recette est identique,
# seul le projet change.

ARG PROJECT_PATH

FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
ARG PROJECT_PATH
WORKDIR /src

# Le manifeste d'outils porte dotnet-ef ; sans restauration, `dotnet ef` n'existe pas.
COPY .config/ .config/
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/

RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet tool restore

# linux-musl-x64 et non linux-x64 : l'image finale est Alpine, dont la libc est musl.
# Un binaire glibc s'y lance avec « no such file or directory » — message trompeur,
# le fichier existe, c'est son interpreteur dynamique qui manque.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet ef migrations bundle \
        --project "${PROJECT_PATH}" \
        --startup-project "${PROJECT_PATH}" \
        --configuration Release \
        --self-contained --runtime linux-musl-x64 \
        --output /efbundle \
        --force

# runtime-deps et non aspnet : le bundle est autonome, il n'a besoin que des
# dependances natives, pas d'un runtime .NET installe.
FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-alpine AS final
WORKDIR /app

RUN adduser --disabled-password --no-create-home --uid 10001 forterro
USER 10001

COPY --from=build --chown=10001:10001 /efbundle /app/efbundle

# Un executable mono-fichier extrait ses bibliotheques natives au demarrage, par
# defaut sous le repertoire personnel de l'utilisateur. Or celui-ci est cree avec
# --no-create-home : /home/forterro n'existe pas, et le bundle s'arrete sur
#   « Failure processing application bundle. »
# Le binaire est pourtant parfaitement valide — l'erreur ne survient qu'a l'EXECUTION,
# donc jamais au build de l'image.
#
# /tmp est monte en emptyDir par le Job, ce qui reste compatible avec un systeme de
# fichiers racine en lecture seule.
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/efbundle

# La chaine de connexion arrive par argument, depuis un Secret Kubernetes.
ENTRYPOINT ["/app/efbundle"]
