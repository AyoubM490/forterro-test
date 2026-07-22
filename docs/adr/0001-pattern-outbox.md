# ADR 0001 — Pattern Outbox plutôt que publication directe

**Statut** : accepté
**Date** : 2026-07-21

## Contexte

À l'émission d'une facture, deux choses doivent se produire : l'état passe à `Issued` en base, et l'événement `InvoiceIssued` part sur Kafka pour déclencher la saga de paiement.

Ces deux systèmes n'ont pas de transaction commune. Écrire naïvement :

```csharp
await context.SaveChangesAsync();      // (1)
await publisher.PublishAsync(evt);     // (2)
```

expose à deux pannes symétriques et toutes deux inacceptables :

- **(2) échoue après (1)** — la facture est émise, personne ne le sait, elle ne sera jamais payée. Le bug est silencieux : rien dans les logs ne signale une facture orpheline.
- **Ordre inversé, (1) échoue après (2)** — on a annoncé au reste du système une facture qui n'existe pas. La saga ouvre un paiement contre un identifiant introuvable.

Le commit en deux phases (XA) est écarté : Kafka ne le supporte pas correctement, et il introduit un coordinateur qui devient un point de défaillance et de latence.

## Décision

Adopter le **pattern Outbox**.

L'événement est écrit dans une table `messaging.outbox_messages` **de la base du service**, dans la même transaction que le changement métier. Un `OutboxDispatcher` (BackgroundService) relit cette table et publie sur Kafka.

```csharp
outbox.Enqueue(domainEvent);        // INSERT dans outbox_messages
await context.SaveChangesAsync();   // un seul commit : état + événement
```

Concurrence entre réplicas : un bail (`leased_until`) protégé par un jeton de version. Deux réplicas qui visent le même lot se départagent par un `DbUpdateConcurrencyException` — pas de leader election à maintenir.

## Conséquences

**Ce que ça garantit** — l'atomicité. Il devient impossible d'observer une facture émise sans son événement, ou l'inverse.

**Ce que ça coûte** :

- **Livraison at-least-once, pas exactly-once.** Le dispatcher peut publier puis mourir avant de marquer le message traité. Il republiera. C'est pourquoi le pattern **Inbox** (table `processed_events`) est obligatoire côté consommateur, et pourquoi `Invoice.MarkAsPaid` est idempotent. Les deux protections sont volontairement redondantes.
- **Latence supplémentaire** : jusqu'à un cycle de polling (2 s par défaut) entre le commit et la publication. Acceptable ici ; un flux exigeant du temps réel demanderait du Change Data Capture (Debezium) sur le WAL PostgreSQL.
- **Une table qui grossit.** D'où `OutboxCleanupService` : sans purge, la table d'outbox devient le plus gros objet de la base en quelques mois et dégrade la requête du dispatcher, qui la scanne à chaque cycle.

## Alternatives écartées

- **Publication directe** — rejetée, c'est le problème décrit ci-dessus.
- **CDC / Debezium** — meilleure latence et aucun polling, mais ajoute Kafka Connect à exploiter et couple le contrat de messages au schéma physique des tables. Réévaluable si le volume l'impose.
- **Transactions Kafka** — ne couvrent pas l'écriture PostgreSQL, donc ne résolvent pas le problème posé.
