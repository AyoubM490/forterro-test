# ADR 0007 — Logs centralisés dans Elastic, corrélés par trace

**Statut** : accepté
**Date** : 2026-07-22

## Contexte

Une requête traverse trois services et un broker. Quand elle échoue, la question n'est jamais « que dit le log de `invoicing-api` ? » mais « que s'est-il passé, dans l'ordre, sur les trois services, pour **cette** requête ? ».

`kubectl logs` ne répond pas à ça. Il faut se souvenir de quels pods interroger, les journaux disparaissent au redémarrage, et rien ne relie une ligne d'`invoicing-api` à la saga qui en découle dans `payments-worker`.

**La corrélation, elle, était déjà résolue.** `RenderedCompactJsonFormatter` écrit `@tr` et `@sp` dans chaque événement dès qu'une `Activity` OpenTelemetry est ouverte — et l'instrumentation ASP.NET Core, HttpClient et Kafka en ouvre une pour tout ce qui compte. Chaque ligne porte donc déjà son `traceId` et son `spanId` :

```json
{"@t":"2026-07-21T21:23:06.80Z","@m":"Health check postgres ...","@l":"Error",
 "@tr":"48da105fc40e00e0175c7a30faca245b","@sp":"c98d2956db6e6db8",
 "service.name":"invoicing-api"}
```

Le problème n'était donc pas d'instrumenter le code, mais de **stocker et interroger** ces lignes. C'est un choix d'infrastructure, pas de développement.

## Décision

**Elastic Stack** : les services écrivent du JSON sur `stdout`, un DaemonSet **Filebeat** le collecte, **Elasticsearch** l'indexe, **Kibana** l'interroge.

### Ce qui rend la corrélation possible

Elastic suit la norme **ECS**, qui nomme ces champs `trace.id` et `span.id`. Serilog les nomme `@tr` et `@sp`. Filebeat fait la traduction :

```yaml
- rename:
    fields:
      - { from: '@tr', to: 'trace.id' }
      - { from: '@sp', to: 'span.id' }
```

**Sans ce renommage, tout fonctionne en apparence** : les logs arrivent, Kibana les affiche, les recherches plein texte marchent. Seule la corrélation par trace est absente — et rien dans l'interface n'indique pourquoi. C'est le point de cette décision, et le seul qui compte vraiment.

Répondre à « tout ce qui s'est passé pour cette requête » devient alors :

```
trace.id : "48da105fc40e00e0175c7a30faca245b"
```

Trois services, ordonnés dans le temps, une seule requête.

### Collecteur, et non sink applicatif

`Serilog.Sinks.Elasticsearch` permettrait d'écrire directement depuis les services. Écarté :

- **les événements bufferisés sont perdus quand le process meurt** — c'est-à-dire exactement au moment où on en a besoin. Le crash Redis du BFF est justement le genre de log qu'un sink en mémoire n'aurait jamais transmis ;
- ça couple chaque service au backend et impose d'y distribuer des identifiants ;
- une lenteur d'Elasticsearch devient une latence applicative.

Écrire sur `stdout` déplace la durabilité vers le nœud : les fichiers restent lisibles même si Elasticsearch est indisponible, et Filebeat reprend où il s'était arrêté grâce à son registre.

### Ce que la centralisation a révélé : la trace cassait à l'Outbox

L'affirmation du README — « un seul `traceId` traverse les trois services » — était **fausse**, et personne ne pouvait s'en apercevoir sans agréger les logs. Mesuré avant correction : une requête d'émission produisait **3 lignes sur 2 services**. `payments-worker` journalisait sous des `traceId` entièrement différents.

La rupture se situait précisément à la frontière que l'[ADR 0001](0001-pattern-outbox.md) rend asynchrone :

| Étape | Comportement |
|---|---|
| `OutboxWriter` | enregistre `TraceParent = Activity.Current?.Id` — le contexte d'origine **est** persisté ✅ |
| `OutboxDispatcher.BuildHeaders` | le publie dans `x-correlation-id`, **pas** dans `traceparent` |
| `KafkaEventPublisher` | remplit `traceparent` depuis sa propre activité — celle d'un `BackgroundService` sans parent, donc une trace neuve |
| `KafkaConsumerService` | lit `traceparent` et rattache la saga… au dispatcher |

Le contexte était donc correctement sauvegardé, puis publié sous un en-tête que personne ne lit pour tracer. Un `BackgroundService` n'hérite d'aucun contexte ambiant : `Activity.Current` y est nulle ou sans rapport avec la requête qui a écrit l'événement. **Le seul lien possible est le contexte persisté en base.**

Correction : `PublishRawAsync` accepte un `parentTraceParent`, et le dispatcher lui passe celui de la ligne d'outbox. Le paramètre a été inséré **avant** `cancellationToken` pour que les appels positionnels existants cessent de compiler plutôt que de se rattacher silencieusement au mauvais argument — le compilateur a d'ailleurs immédiatement signalé les deux autres sites d'appel.

Mesuré après correction, pour la même opération : **21 lignes sur les 4 services**.

C'est l'argument le plus solide en faveur de ce chantier : la centralisation n'a pas seulement rendu les logs consultables, elle a prouvé qu'une propriété que la documentation affirmait n'était pas vraie.

### Deux changements applicatifs

1. **`CompactJsonFormatter` → `RenderedCompactJsonFormatter`.** Le premier n'écrit que le gabarit (`@mt` : `"Health check {HealthCheckName}"`, placeholders non substitués) ; dans un agrégateur, un message non rendu est illisible et ne se cherche pas. Le regroupement par type d'événement n'est pas perdu pour autant : les deux formats émettent `@i`, l'empreinte du gabarit.

2. **Suppression de la propriété `TraceId`** poussée par `CorrelationIdMiddleware`, redondante avec `@tr` — deux champs pour la même valeur, indexés deux fois, avec le risque qu'un tableau de bord filtre sur celui qui n'est pas alimenté. `CorrelationId` reste : il peut venir du client et relier **plusieurs** traces d'un même parcours, là où `@tr` n'en couvre qu'une.

## Conséquences

**Ce que ça apporte** — une seule recherche répond à « que s'est-il passé pour cette requête », à travers les trois services. Les logs survivent aux pods. La recherche plein texte et les agrégations deviennent possibles sur l'ensemble du parc.

**Ce que ça coûte** :

- **Elasticsearch est lourd.** Conçu pour un cluster de plusieurs nœuds avec 4 à 32 Go de heap ; ici 768 Mo sur un nœud unique. Sur le cluster local, Elasticsearch et Kibana pèsent **plus que les quatre services métier réunis**. C'est le vrai prix de ce choix, et il est payé pour indexer en plein texte des données dont on connaît déjà la clé de recherche.
- **Un prérequis d'hôte invisible** : `vm.max_map_count >= 262144`. En dessous, Elasticsearch refuse de démarrer.
- **Aucune rétention configurée.** ILM est désactivé : l'index grossit indéfiniment. Acceptable pour un environnement jetable, à corriger avant tout usage durable.
- **Sécurité désactivée en local** (`xpack.security.enabled: false`). En production c'est l'inverse qu'il faut : TLS, authentification, et droits par index.
- **Une seconde source de vérité.** Traces dans Jaeger, logs dans Kibana, et le saut de l'une à l'autre reste manuel — on copie un `traceId`. Elastic APM le rendrait cliquable, au prix d'un agent APM dans chaque service.

## Alternatives écartées

- **Grafana Loki + Tempo** — nettement plus léger (Loki n'indexe que des labels), corrélation trace↔logs native et cliquable, et Tempo consomme l'OTLP déjà exporté. **Techniquement le meilleur choix pour un système de cette taille**, et il faut le dire clairement. Écarté ici au profit d'Elastic pour sa cohérence avec un existant d'entreprise et pour la puissance de recherche de Kibana.
- **`kubectl logs` / `stern`** — suffisant pour déboguer un pod, inutilisable pour corréler trois services, et perd tout au redémarrage.
- **Sink Elasticsearch dans l'application** — cf. ci-dessus, perd les logs au crash.
- **OpenSearch** — équivalent fonctionnel sous licence Apache 2.0. Un choix défendable si la licence Elastic pose problème ; aucune différence sur le sujet traité ici.
- **Elastic APM à la place d'OpenTelemetry** — donnerait la corrélation logs↔traces sans effort dans Kibana, mais remplacerait une instrumentation standard et portable par une instrumentation propriétaire. Le coût de sortie n'en vaut pas le confort.
