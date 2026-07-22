# Forterro.Outbox.EntityFrameworkCore

Pattern Outbox sur Entity Framework Core.

## Le problème résolu

« J'écris la facture en base **et** je publie l'événement » ne peut pas être atomique :
PostgreSQL et Kafka n'ont pas de transaction commune. Les deux pannes possibles sont
aussi inacceptables l'une que l'autre — une facture émise que personne ne connaît, ou un
événement annonçant une facture qui n'existe pas.

```csharp
outbox.Enqueue(domainEvent);        // INSERT dans messaging.outbox_messages
await context.SaveChangesAsync();   // un seul commit : etat + evenement
```

Un `OutboxDispatcher` relit la table et publie via `IEventPublisher`.

## Ce que ça implique

Livraison **at-least-once**, pas exactly-once : le dispatcher peut publier puis mourir
avant de marquer le message traité. La déduplication côté consommateur (Inbox) est donc
**obligatoire**, pas optionnelle.

Concurrence entre réplicas gérée par un bail (`leased_until`) protégé par un jeton de
version — pas d'élection de leader à maintenir.

## Dépendances

Entity Framework Core et `Forterro.Messaging.Abstractions`. **Aucun transport** : ce
paquet ne connaît pas Kafka, il publie via `IEventPublisher`.
