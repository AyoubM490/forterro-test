# Forterro.Messaging.Kafka

Producteur et consommateur Kafka des Business Services Forterro.

- **Publication idempotente**, pour qu'un retour en erreur réseau ne duplique pas le message.
- **Contexte de trace W3C propagé** dans les en-têtes : une trace unique traverse
  HTTP → Outbox → Kafka → consommateur.
- **Déduplication par Inbox** côté consommateur, obligatoire en livraison *at-least-once*.

## Ne l'appelez pas directement depuis un handler métier

`IEventPublisher` publie immédiatement. Un événement publié puis un commit SQL en échec
annonce au reste du système quelque chose qui n'a pas eu lieu. Passez par
`Forterro.Outbox.EntityFrameworkCore`, qui écrit l'événement **dans la même transaction**
que le changement d'état. Voir ADR 0001.

## Dépendances

`Confluent.Kafka`, `Forterro.Messaging.Abstractions`, `Forterro.Diagnostics`. Ni
OpenTelemetry ni Serilog.
