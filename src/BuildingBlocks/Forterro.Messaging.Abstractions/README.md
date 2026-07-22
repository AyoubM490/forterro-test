# Forterro.Messaging.Abstractions

Contrats de messagerie des Business Services Forterro : événement d'intégration,
publication, consommation, registre de contrats.

**Ce paquet n'a aucune dépendance tierce**, et c'est sa raison d'être. Référencez-le
si vous consommez les événements des Business Services sans vouloir hériter de Kafka,
Entity Framework ou OpenTelemetry.

## Contenu

| Type | Rôle |
|---|---|
| `IntegrationEvent` | Classe de base des événements — identifiant, horodatage, contexte de trace |
| `IEventPublisher` | Publication. **Depuis un handler métier, ne pas l'appeler directement** : passer par l'Outbox pour rester atomique avec le commit SQL |
| `IIntegrationEventHandler<T>` | Consommation |
| `IntegrationEventRegistry` | Association nom logique de contrat ↔ type .NET ↔ topic |
| `IProcessedEventStore` | Inbox — déduplication en livraison at-least-once |
| `MessagingJson` | Options de sérialisation, désérialisation tolérante aux champs inconnus |

## Règle de conception

Si une `PackageReference` devient nécessaire ici, c'est que le type n'a rien à y faire :
l'implémentation appartient à `Forterro.BuildingBlocks`.

## Namespace

`Forterro.BuildingBlocks.Messaging` — inchangé depuis la 1.0.0. Une frontière
d'assembly n'est pas une frontière de namespace : le code existant compile sans
modification, et `Forterro.BuildingBlocks` conserve des redirections de type
(`TypeForwardedTo`) pour les binaires déjà compilés.
