# Forterro.Banking

Validation d'IBAN selon ISO 13616 : longueur par pays, réarrangement, et contrôle
**modulo 97**.

**Aucune dépendance.** Deux `using` du BCL, rien d'autre.

```csharp
if (!Iban.IsValid(iban)) { /* refuser */ }
var normalise = Iban.Normalize(iban);   // sans espaces, en majuscules
```

## Pourquoi un paquet séparé

Un IBAN se valide de la même façon dans une API de facturation, un batch de
rapprochement ou un formulaire. Ce code n'a aucune raison d'imposer Kafka ou Entity
Framework à qui veut simplement vérifier une saisie.

Le contrôle mod-97 rejette les fautes de frappe et les chiffres transposés, pas
l'existence du compte : un IBAN valide peut parfaitement ne correspondre à rien.
