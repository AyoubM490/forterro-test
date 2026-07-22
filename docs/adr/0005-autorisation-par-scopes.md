# ADR 0005 — Autorisation par scopes OAuth 2.0

**Statut** : accepté
**Date** : 2026-07-21

## Contexte

Ces APIs sont consommées par plusieurs lignes produit du groupe, chacune avec ses propres besoins : certaines lisent des factures, d'autres en émettent, d'autres encore n'ont accès qu'aux soldes de compte.

Une autorisation fondée sur les **rôles** de l'utilisateur final répond à la mauvaise question. Le rôle dit « qui est cette personne ». Ce dont on a besoin ici, c'est « qu'est-ce que cette application a le droit de faire, en son nom ». Un utilisateur `billing-admin` ne devrait pas donner à *toute* application qui obtient son jeton le droit d'émettre des factures.

## Décision

Autorisation par **scopes OAuth 2.0**, avec les rôles en complément et non en remplacement.

Scopes déclarés : `invoicing:read`, `invoicing:write`, `payments:read`, `payments:write`, `accounts:read`.

```csharp
builder.Services.AddAuthorizationBuilder()
    .AddScopePolicy(Policies.InvoicingRead, "invoicing:read", "invoicing:write")
    .AddScopePolicy(Policies.InvoicingWrite, "invoicing:write");
```

Points d'implémentation qui comptent :

- **Le scope se lit dans la claim `scope`, séparée par des espaces** (RFC 6749). `scp` est également accepté pour les jetons Azure AD. La comparaison est exacte, jamais par préfixe : `invoicing:read` ne satisfait pas `invoicing:read-all`.
- **L'audience est validée.** Un jeton émis pour un autre service est rejeté. Sans cette vérification, tout service du realm pourrait rejouer un jeton contre les APIs de facturation.
- **`ClockSkew` ramené à 30 secondes** au lieu des 5 minutes par défaut. Sur des services internes correctement synchronisés, une tolérance de 5 minutes ne fait que prolonger la validité d'un jeton révoqué.
- **Service à service : Client Credentials.** `Payments.Worker` obtient son propre jeton, avec sa propre identité et ses propres scopes. Le jeton de l'utilisateur final n'est **jamais** propagé : il n'a ni la bonne audience, ni les scopes nécessaires, et le propager étendrait sa portée bien au-delà de ce que l'utilisateur a consenti.
- **Les rôles Keycloak sont aplatis.** Keycloak les imbrique dans `realm_access.roles` et `resource_access.{client}.roles`, que ASP.NET Core ne lit pas nativement : sans aplatissement, `[Authorize(Roles = "...")]` ne matcherait jamais. Un JSON malformé n'échoue pas l'authentification — le principal ressort sans rôle, donc refusé (*fail closed*).

## Conséquences

**Ce que ça apporte** — une granularité alignée sur l'usage réel. Ajouter une ligne produit qui n'a besoin que de lire revient à lui accorder un seul scope, sans toucher au code des APIs.

**Ce que ça coûte** :

- **Le nombre de scopes croît avec les cas d'usage.** Le risque est d'arriver à une trentaine de scopes que plus personne ne sait attribuer correctement. Garde-fou : un scope par couple ressource/verbe, jamais par cas d'usage particulier.
- **Aucune autorisation au niveau de la donnée.** Un client porteur de `invoicing:read` peut lire *toutes* les factures, pas seulement les siennes. Le cloisonnement par locataire serait la couche suivante — probablement une claim `tenant_id` filtrée par un query filter global EF Core.
- **Dépendance forte au serveur d'autorisation.** S'il tombe, plus aucun jeton n'est émis. Mitigé par le cache des clés de signature (JWKS) et le cache de jeton côté service, mais ce n'est pas une élimination du risque.

## Alternatives écartées

- **Rôles uniquement** — insuffisant entre services, cf. contexte.
- **Clés d'API** — simple, mais ni révocation fine, ni expiration, ni granularité, ni traçabilité de l'appelant.
- **ABAC / OPA** — plus expressif, mais ajoute un moteur de politiques à exploiter pour un besoin que les scopes couvrent aujourd'hui. Justifié le jour où l'autorisation dépendra d'attributs de la donnée elle-même.
