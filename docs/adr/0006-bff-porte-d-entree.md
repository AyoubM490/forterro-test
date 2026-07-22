# ADR 0006 — Un BFF comme porte d'entrée, avec deux schémas d'authentification

**Statut** : accepté
**Date** : 2026-07-21

## Contexte

Jusqu'ici les trois services étaient exposés directement : `5001`, `5002`, `5003` en développement, autant de `Service` ClusterIP en Kubernetes. Aucun point d'entrée commun, donc :

- **aucune terminaison TLS ni limitation de débit** mutualisées ;
- **la topologie interne fuit vers les clients.** Un écran de suivi de facture doit savoir que la facture vient d'un service et l'avancement du paiement d'un autre. Découper les services autrement devient un changement cassant pour chaque frontal ;
- **rien pour un navigateur.** Le realm déclare pourtant déjà un client public PKCE (`forterro-swagger`), et un jeton manipulé par du JavaScript est volable par XSS.

Deux publics coexistent et n'ont pas les mêmes contraintes :

| Public | Exemple | Menace principale |
|---|---|---|
| Écran, dans un navigateur | portail de facturation | XSS, CSRF |
| Ligne produit, en machine à machine | `erp-product-line` | ni l'un ni l'autre : pas de navigateur |

## Décision

Un service `Forterro.Bff` (YARP + ASP.NET Core), seul exposé à l'extérieur, qui **fait cohabiter les deux schémas** et choisit par requête.

### Sélection du schéma

Un en-tête `Authorization: Bearer` explicite désigne un appelant machine — un navigateur ne le pose jamais de lui-même. Sinon, le cookie de session s'applique.

```csharp
options.ForwardDefaultSelector = context =>
    IsMachineRequest(context.Request) ? MachineScheme : SessionScheme;
```

Ce prédicat est **volontairement partagé** avec le filtre anti-CSRF. S'ils divergeaient, une requête authentifiée par cookie pourrait être classée « machine » par le filtre : contournement complet de la protection.

### Chemin navigateur

- **Authorization code + PKCE**, mené par le BFF, qui est un client confidentiel.
- **Les jetons ne quittent jamais le serveur.** Ils vivent dans le ticket d'authentification, rangé dans un `ITicketStore` adossé à Redis. Le navigateur ne reçoit qu'un cookie `HttpOnly`, `SameSite=Strict`, préfixé `__Host-`, contenant une clé opaque.
- **Révocation immédiate** : supprimer l'entrée du store tue la session partout, y compris pour un cookie exfiltré. Un ticket sérialisé dans le cookie, lui, reste valide jusqu'à expiration quoi qu'on fasse.
- **Anti-CSRF par en-tête applicatif** (`X-Forterro-Csrf`), contrôle d'`Origin`, et `SameSite=Strict`. Trois barrières indépendantes, parce qu'aucune n'est complète seule.
- **Rafraîchissement transparent** du jeton avant expiration : sinon l'utilisateur serait déconnecté toutes les cinq minutes.

### Chemin machine

Le jeton entrant est validé puis **propagé tel quel**, avec les scopes de l'appelant. Le BFF n'échange pas le jeton contre une identité de service plus large : ce serait le chemin le plus court pour transformer chaque endpoint en escalade de privilèges.

### Agrégation

`GET /bff/invoices/{id}/overview` appelle la facturation et l'état de la saga **en parallèle**, et compose une réponse taillée pour l'écran — avec une phrase de synthèse déduite des deux états, dont le cas `compensation_required` qui doit appeler un humain plutôt qu'inviter à réessayer.

La facture est la ressource principale : son absence produit un 404. Le paiement est secondaire : son indisponibilité **dégrade** la réponse (`paymentAvailability`) sans la supprimer. Un scope `payments:read` manquant se traduit de la même façon — c'est le cas d'`erp-product-line`, qui n'a que les scopes de facturation.

### Ce qui reste à l'infrastructure

L'Ingress NGINX assure TLS (certificats cert-manager renouvelés automatiquement), HSTS, limite de débit par IP et plafond de taille de corps. Le BFF fait ce qu'un Ingress ne peut pas faire : il connaît l'identité de l'appelant. Sa limite de débit est donc par `client_id` ou par session, pas par IP — une IP partagée par toute une entreprise ne doit pas être limitée comme un client unique.

## Conséquences

**Ce que ça apporte**

- Un seul point d'entrée à protéger, journaliser et limiter.
- Les jetons hors de portée du JavaScript pour les écrans.
- Les services métier restent de purs serveurs de ressources OAuth, appelables aussi bien par le BFF que par une autre ligne produit. Ils **revalident** jeton, audience et scopes : le BFF n'est pas un point de confiance unique, et le contourner ne donne rien.

**Ce que ça coûte**

- **Un service de plus** à déployer, surveiller et faire évoluer, sur le chemin critique de tout le trafic.
- **Deux surfaces d'authentification à tester.** Le risque n'est pas théorique : le piège est qu'un chemin devienne une porte dérobée de l'autre. D'où le prédicat partagé et les tests qui verrouillent chaque cas.
- **Un état partagé, donc une dépendance à Redis.** Sessions *et* clés de protection des données. Sans clés partagées, un cookie émis par un replica est illisible pour les autres, et l'utilisateur est renvoyé au login une requête sur deux — symptôme intermittent dont la cause n'est pas là où on la cherche.
- **Le rafraîchissement concurrent n'est pas verrouillé.** Bénin avec le réglage Keycloak par défaut (`Revoke Refresh Token` désactivé). Activer la rotation exigerait un verrou distribué, pas un `lock` en mémoire.
- **Un couplage à la configuration du serveur d'autorisation.** Les URL de redirection sont lues dans le document de découverte : Keycloak doit annoncer une adresse frontale joignable par le navigateur (`KC_HOSTNAME` + `KC_HOSTNAME_BACKCHANNEL_DYNAMIC`).

## Alternatives écartées

- **Ingress NGINX seul.** Couvre TLS et le routage, mais ne sait ni agréger, ni tenir une session, ni raisonner sur l'identité de l'appelant. Complémentaire, pas substituable : les deux sont déployés.
- **Passthrough bearer sans session.** Beaucoup plus léger, et suffisant tant qu'aucun navigateur n'appelle. Écarté parce que le realm déclare déjà un client navigateur, et que greffer des sessions sur une passerelle en production revient à réécrire sa couche d'authentification.
- **Jetons dans le `localStorage` du navigateur, BFF simple proxy.** C'est le défaut de beaucoup de SPA, et c'est précisément ce que le BCP « OAuth 2.0 for Browser-Based Apps » déconseille : une seule dépendance npm compromise suffit à exfiltrer les jetons.
- **Échange de jeton (token exchange) vers une identité de service.** Simplifie les appels en aval, mais fait perdre la traçabilité de l'appelant réel et ouvre l'escalade de privilèges.
