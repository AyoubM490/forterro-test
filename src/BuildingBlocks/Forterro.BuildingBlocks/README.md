# Forterro.BuildingBlocks

Briques transverses des Business Services Forterro : authentification OIDC, messagerie
Kafka, pattern Outbox, résilience, observabilité, validation IBAN.

## Ce paquet est lourd, et c'est assumé

Il embarque Kafka, Entity Framework, JwtBearer, OpenTelemetry et Serilog. Si vous ne
voulez qu'une partie, prenez le paquet ciblé :

| Besoin | Paquet |
|---|---|
| Consommer les événements des Business Services | `Forterro.Contracts` |
| Publier/consommer, sans implémentation | `Forterro.Messaging.Abstractions` *(zéro dépendance)* |
| Tout le reste | ce paquet |

## Contenu

| Espace de noms | Rôle |
|---|---|
| `Api` | ProblemDetails RFC 7807, corrélation, idempotence |
| `Banking` | Validation IBAN mod-97 |
| `Messaging.Kafka` | Producteur et consommateur |
| `Observability` | OpenTelemetry + Serilog |
| `Outbox` | Écriture transactionnelle, dispatcher, purge |
| `Persistence` | Inbox, conventions snake_case |
| `Resilience` | Pipelines Polly |
| `Security` | OIDC, scopes, rôles Keycloak |

## Compatibilité

Depuis l'extraction de `Forterro.Messaging.Abstractions`, les types de messagerie
vivent dans un autre assembly. Ce paquet les **réexporte** et conserve des redirections
`TypeForwardedTo` : le code source compile sans modification, et les binaires déjà
compilés contre la 1.0.0 continuent de fonctionner sans être recompilés.

## Règle de l'Outbox

`IEventPublisher` ne s'appelle **pas** directement depuis un handler métier : passer par
`IOutboxWriter`, pour que l'événement soit écrit dans la même transaction que le
changement d'état. Voir ADR 0001.
