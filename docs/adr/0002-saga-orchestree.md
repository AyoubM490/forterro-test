# ADR 0002 — Saga orchestrée plutôt que chorégraphiée

**Statut** : accepté
**Date** : 2026-07-21

## Contexte

Le paiement d'une facture traverse trois services et un partenaire externe. Le processus n'est pas linéaire : la banque peut être indisponible (rejouable), refuser pour provision insuffisante (définitif), accepter sans exécuter immédiatement (attente), et la facture peut être annulée en cours de route.

Deux modèles s'offraient à nous.

**Chorégraphie** : chaque service réagit aux événements des autres, sans coordinateur. Simple à démarrer, mais la logique « on a tenté 3 fois, la banque est HS, on abandonne et on compense » se retrouve éclatée entre trois bases de code. Personne ne peut répondre à « où en est le paiement de la facture INV-2026-000042 ? » sans corréler trois journaux.

**Orchestration** : un service détient l'état du processus et décide de l'étape suivante.

## Décision

Orchestration, portée par `PaymentSaga` dans `Payments.Worker`.

L'état est **persisté en base**, pas en mémoire : un redémarrage du worker en plein milieu reprend exactement où il en était. La machine à états est explicite :

```
Started ──▶ AwaitingBank ──▶ Settled
   │             │
   │             ├──▶ AwaitingRetry ──▶ (retour à AwaitingBank)
   │             └──▶ Failed
   └──▶ Aborted (facture annulée avant transmission)
```

Deux points de conception non négociables :

1. **La tentative est persistée AVANT l'appel sortant.** Si le worker meurt pendant l'appel bancaire, on sait qu'une tentative était en cours, et la clé d'idempotence associée (`{sagaId}-{attempt}`) est identique à la reprise. Pas de double débit.
2. **Un planificateur de reprise dédié** (`SagaRetryService`). Kafka a déjà commité l'offset : l'événement ne reviendra jamais. Sans ce planificateur, une saga tombée sur une banque indisponible resterait bloquée pour toujours. C'est le point qu'on oublie le plus souvent en passant à l'asynchrone — le broker livre des messages, il ne rejoue pas de la logique métier.

## Conséquences

**Ce que ça apporte** — un seul endroit pour lire, tester et auditer le processus. La table `payment_sagas` répond directement à « où en est ce paiement, combien de tentatives, pourquoi ça a échoué ».

**Ce que ça coûte** :

- **Un point de couplage.** `Payments.Worker` connaît les étapes du processus. C'est assumé : c'est précisément son rôle. Il ne connaît en revanche ni le schéma de facturation, ni le dialecte de la banque.
- **La compensation a une limite réelle.** Si l'ordre est déjà transmis, un virement SEPA ne s'annule pas unilatéralement. La saga marque alors `compensation_required` et exige une intervention humaine. Le code le dit explicitement plutôt que de faire croire à un rollback automatique — un `try/catch` qui « annule » un virement exécuté serait un mensonge.
- **Le worker doit tourner en plusieurs réplicas** sans se marcher dessus. Résolu par la concurrence optimiste sur `xmin` et l'unicité de `invoice_id`.

## Alternatives écartées

- **Chorégraphie pure** — rejetée pour l'auditabilité.
- **MassTransit State Machine (Automatonymous)** — solide et éprouvé, mais fait disparaître la mécanique derrière un framework. Ici l'implémentation manuelle est délibérée : elle rend visible ce que le framework ferait. Sur un projet réel, MassTransit serait un choix parfaitement défendable.
- **Workflow durable (Temporal, Dapr)** — réponse la plus complète au problème, mais ajoute une brique d'infrastructure lourde à exploiter pour un seul processus métier.
