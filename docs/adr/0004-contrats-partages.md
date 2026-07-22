# ADR 0004 — Contrats partagés dans un paquet versionné

**Statut** : accepté
**Date** : 2026-07-21

## Contexte

`Invoicing.Api` publie `InvoiceIssued`, `Payments.Worker` le consomme. Il faut que les deux s'accordent sur la forme du message, le nom du contrat et le topic.

L'école « partagez rien » recommande que chaque consommateur redéfinisse sa propre vue du message. C'est la bonne réponse quand les services appartiennent à des équipes ou des organisations différentes, avec des cycles de livraison indépendants.

Ce n'est pas notre situation : un monorepo, une équipe Business Services, des services livrés ensemble. Ici, la duplication ne protège de rien et garantit surtout qu'un renommage de champ sera oublié dans un consommateur.

## Décision

Un paquet `Forterro.Contracts`, versionné et publié en NuGet, contenant les événements, les noms de contrats et les topics.

Trois règles rendent ce partage sûr :

**1. Le nom logique du contrat n'est pas le nom de type .NET.**

```csharp
registry.Register<InvoiceIssued>("invoicing.invoice-issued.v1", "forterro.invoicing.v1");
```

Le header Kafka transporte `invoicing.invoice-issued.v1`. Renommer la classe C# est un refactoring sans effet sur les consommateurs déployés.

**2. La désérialisation est tolérante.** `UnmappedMemberHandling.Skip` : un producteur plus récent peut ajouter un champ sans casser les consommateurs qui tournent déjà.

**3. Une rupture de contrat crée une V2, elle ne modifie jamais la V1.** Nouveau nom logique, nouveau topic si nécessaire. Les deux coexistent le temps que tous les consommateurs migrent. On ne modifie jamais un contrat en place en production.

## Conséquences

**Ce que ça apporte** — impossible qu'un producteur et un consommateur divergent sur le nom d'un contrat ou d'un topic : `ContractRegistration` est le point unique de déclaration, appelé par les trois services.

**Ce que ça coûte** :

- **Un couplage au moment de la compilation.** Publier une nouvelle version du paquet oblige, à terme, les consommateurs à monter de version. Acceptable dans un monorepo mono-équipe ; ce serait un frein réel entre organisations distinctes.
- **La discipline du versionnage repose sur les personnes.** Rien n'empêche techniquement d'ajouter un champ `required` à un contrat existant et de casser tout le monde. Un **schema registry** (Avro/Protobuf avec vérification de compatibilité) automatiserait ce garde-fou — c'est la suite logique si le nombre de consommateurs augmente.
- **Les contrats doivent rester des DTO purs.** Aucune logique métier, aucune dépendance vers un service : sinon le paquet devient un vecteur de couplage bien plus profond que le simple partage de forme.

## Alternatives écartées

- **Duplication par consommateur** — meilleur découplage, mais garantit la dérive silencieuse dans notre contexte.
- **Schema registry (Confluent + Avro)** — la réponse la plus robuste, écartée pour l'instant au titre du coût d'exploitation. À reconsidérer dès que des équipes externes consomment ces topics.
