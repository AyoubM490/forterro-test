# Forterro.Diagnostics

`ActivitySource` et instruments de mesure partagés par tous les Business Services
Forterro.

**Aucune dépendance** — uniquement `System.Diagnostics`. Ni OpenTelemetry, ni Serilog :
la configuration de l'exportation appartient à `Forterro.BuildingBlocks`.

| Membre | Rôle |
|---|---|
| `Telemetry.ActivitySource` | Source de traces commune, celle qui relie les services |
| `Telemetry.BusinessEvents` | Compteur d'événements **métier**, ventilé par contrat et résultat |
| `Telemetry.ExternalCallDuration` | Durée des appels sortants vers les partenaires bancaires |

## Pourquoi ce paquet existe

Produire une trace et savoir l'exporter sont deux choses distinctes. Sans cette
séparation, `Forterro.Messaging.Kafka` — qui a juste besoin d'ouvrir une `Activity` —
traînait toute la pile OpenTelemetry et Serilog.
