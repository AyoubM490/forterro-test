# Forterro.Contracts

Contrats d'intégration des Business Services Forterro : les événements publiés par la
facturation et les paiements, avec leurs noms logiques et leurs topics.

Sa seule dépendance est `Forterro.Messaging.Abstractions`, qui n'en a aucune.

## Les trois règles qui rendent ce partage sûr

**1. Le nom logique du contrat n'est pas le nom de type .NET.**

```csharp
registry.Register<InvoiceIssued>("invoicing.invoice-issued.v1", "forterro.invoicing.v1");
```

Le header Kafka transporte `invoicing.invoice-issued.v1`. Renommer la classe C# est un
refactoring sans effet sur les consommateurs déployés.

**2. La désérialisation est tolérante.** Un producteur plus récent peut ajouter un champ
sans casser les consommateurs qui tournent déjà.

**3. Une rupture crée une V2, elle ne modifie jamais la V1.** Les deux coexistent le temps
que tous les consommateurs migrent. On ne modifie jamais un contrat en place en production.

## Utilisation

```csharp
services.AddIntegrationEvents(r => r.AddBusinessServicesContracts());
```

`ContractRegistration` est le point unique de déclaration, appelé par les trois services :
producteur et consommateur ne peuvent pas diverger sur un nom de contrat ou de topic.

## Ce que ce paquet ne doit jamais contenir

Aucune logique métier, aucune dépendance vers un service. Sinon il devient un vecteur de
couplage bien plus profond que le simple partage de forme — voir ADR 0004.
