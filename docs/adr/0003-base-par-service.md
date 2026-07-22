# ADR 0003 — Une base de données par service

**Statut** : accepté
**Date** : 2026-07-21

## Contexte

Trois services manipulent des données liées : factures, sagas de paiement, ordres bancaires. La tentation d'une base partagée est forte — les jointures seraient immédiates, il n'y aurait aucune duplication, et la cohérence serait garantie par le SGBD.

Elle est écartée pour une raison précise : une base partagée transforme le schéma en API publique implicite. Ajouter une colonne `NOT NULL` dans `invoices` casse alors le service de paiement qui la lisait. Les services deviennent indissociables au déploiement — c'est un monolithe distribué, qui cumule les coûts des microservices sans leurs bénéfices.

## Décision

Chaque service possède **sa propre base**, avec son propre schéma, et personne d'autre n'y accède :

| Service | Base | Schéma |
|---|---|---|
| Invoicing.Api | `forterro_invoicing` | `invoicing` |
| Payments.Worker | `forterro_payments` | `payments` |
| OpenBanking.Api | *(aucune)* | — |

Les tables Outbox et Inbox vivent **dans la base du service**, pas dans une base de messagerie commune. C'est ce qui rend l'écriture transactionnelle possible.

`OpenBanking.Api` est volontairement **sans état** : c'est une couche anti-corruption devant les API bancaires. L'état du processus appartient à la saga, pas à la passerelle. Un service sans état se réplique, se redémarre et se teste sans précaution particulière.

## Conséquences

**Ce que ça apporte** :

- Chaque service migre son schéma indépendamment, sans coordonner de fenêtre de déploiement.
- Le choix technologique reste ouvert : rien n'empêche un futur service d'utiliser autre chose que PostgreSQL.
- Le rayon d'impact d'un incident est borné.

**Ce que ça coûte** :

- **Aucune jointure entre domaines.** Corréler une facture et son paiement demande deux appels ou une projection. C'est le prix de l'indépendance.
- **Cohérence à terme.** Entre l'émission de la facture et sa mise à jour en « payée », le système est temporairement incohérent. Le domaine l'accepte : un virement SEPA n'est de toute façon pas instantané.
- **Duplication assumée.** `PaymentSaga` recopie le montant et l'IBAN au lieu d'interroger la facturation. Ce n'est pas une erreur de normalisation : la saga doit pouvoir fonctionner même si le service de facturation est indisponible, et le montant à débiter est celui qui était valide **au moment de l'émission**, pas celui d'aujourd'hui.
- **Migrations en Job Kubernetes**, pas au démarrage des pods. Avec N réplicas, N pods tenteraient de migrer simultanément. Le hook `PreSync` d'ArgoCD garantit l'ordre. **Un Job par service** : les bases sont indépendantes, leurs migrations aussi, et l'une ne doit pas rester non migrée parce que l'autre a échoué.

## Alternatives écartées

- **Base partagée** — rejetée, cf. contexte.
- **Un schéma par service dans une base unique** — l'isolation logique est réelle, mais le point de défaillance et la contention restent communs. Envisageable en phase de démarrage pour réduire les coûts, à condition de ne jamais écrire une requête inter-schémas.
