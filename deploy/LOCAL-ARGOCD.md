# ArgoCD en local (k3d)

Monte un cluster jetable sur le poste et y fait tourner la boucle GitOps du dépôt :
un commit dans Git, et ArgoCD déploie. Rien ici n'est destiné à la production —
[deploy/argocd/application.yaml](argocd/application.yaml) reste la référence.

## Accès

| Quoi | Où | Identifiants |
|---|---|---|
| **Interface ArgoCD** | http://localhost:8081 | `admin` / voir commande ci-dessous |
| **Gitea** (dépôt source) | http://localhost:3000 | `forterro` / `forterro-local-1` |
| **Kibana** (logs centralisés) | `kubectl -n infra port-forward svc/kibana 5601:5601` | aucun (sécurité désactivée en local) |

```bash
kubectl -n argocd get secret argocd-initial-admin-secret -o jsonpath='{.data.password}' | base64 -d
```

## Ce qui tourne

```
┌─ Docker (hôte) ──────────────────┐     ┌─ cluster k3d 'forterro' ───────────┐
│  forterro-gitea  :3000           │◀────│  argocd  (host.k3d.internal:3000)  │
│    forterro/business-services    │     │     │ applique                     │
└──────────────────────────────────┘     │     ▼                              │
                                         │  namespace business-services       │
   C:\Users\PC\forterro-gitops\work       │   bff, invoicing, openbanking,     │
   ↑ dépôt de travail, push → Gitea      │   payments + NetworkPolicies       │
                                         └────────────────────────────────────┘
                                                  Traefik :8081 → UI ArgoCD
```

`host.k3d.internal` est injecté dans CoreDNS par k3d : c'est ce qui permet à un pod
d'atteindre un service qui tourne sur la machine hôte.

## Remonter la pile depuis zéro

```bash
# 1. Cluster
k3d cluster create forterro --agents 1 -p "8081:80@loadbalancer"
kubectl config set-cluster k3d-forterro --server=https://127.0.0.1:6898   # voir « Pièges »

# 2. ArgoCD  (--server-side : la CRD applicationsets dépasse la limite d'annotation)
kubectl create namespace argocd
kubectl apply -n argocd --server-side=true -f \
  https://raw.githubusercontent.com/argoproj/argo-cd/stable/manifests/install.yaml
kubectl -n argocd patch configmap argocd-cmd-params-cm --type merge -p '{"data":{"server.insecure":"true"}}'
kubectl -n argocd rollout restart deployment argocd-server

# 3. Images : construites en local, importées dans le cluster (pas de registre)
docker compose -f deploy/docker-compose.yml build
docker tag forterro-business-services-bff:latest             ghcr.io/forterro/bff:1.0.0
docker tag forterro-business-services-invoicing-api:latest   ghcr.io/forterro/invoicing-api:1.0.0
docker tag forterro-business-services-openbanking-api:latest ghcr.io/forterro/openbanking-api:1.0.0
docker tag forterro-business-services-payments-worker:latest ghcr.io/forterro/payments-worker:1.0.0
k3d image import -c forterro ghcr.io/forterro/{bff,invoicing-api,openbanking-api,payments-worker}:1.0.0

# 4. Gitea + dépôt source
docker run -d --name forterro-gitea -p 3000:3000 \
  -e GITEA__security__INSTALL_LOCK=true \
  -e GITEA__server__ROOT_URL=http://host.k3d.internal:3000/ gitea/gitea:1.22
docker exec -u git forterro-gitea gitea admin user create --admin \
  --username forterro --password 'forterro-local-1' --email gitops@forterro.local
curl -u forterro:forterro-local-1 -X POST http://localhost:3000/api/v1/user/repos \
  -H 'Content-Type: application/json' \
  -d '{"name":"business-services","private":false,"default_branch":"main"}'

# 5. Webhook Gitea → ArgoCD. Sans lui, un push n'est PAS detecte (voir plus bas).
#    Gitea doit joindre le cluster : on l'attache au reseau docker de k3d.
#    Type « gogs » et non « gitea » : c'est celui qu'ArgoCD sait interpreter.
docker network connect k3d-forterro forterro-gitea
curl -u forterro:forterro-local-1 -X POST \
  http://localhost:3000/api/v1/repos/forterro/business-services/hooks \
  -H 'Content-Type: application/json' \
  -d '{"type":"gogs","active":true,"events":["push"],
       "config":{"url":"http://k3d-forterro-serverlb/api/webhook","content_type":"json"}}'

# 6. L'Application
kubectl apply -f deploy/argocd/application-local.yaml

# 7. UI derrière Traefik
kubectl -n argocd create ingress argocd-server --class=traefik --rule="/*=argocd-server:80"
```

## Vérifier que la boucle marche

**selfHeal** — supprimer une ressource à la main, ArgoCD la recrée (~10 s) :

```bash
kubectl -n business-services delete service openbanking-api
sleep 15 && kubectl -n business-services get service openbanking-api
```

**Commit → déploiement** — modifier l'overlay, pousser, observer :

```bash
cd C:\Users\PC\forterro-gitops\work
# éditer deploy/k8s/overlays/local/kustomization.yaml
git commit -am "change" && git push gitea main
kubectl -n business-services get cm business-services-config -o jsonpath='{.data.frontend-origin}'
```

Mesuré sur cette installation : **37 s** entre le `git push` et la valeur à jour dans le
cluster, sans aucune action manuelle — grâce au webhook (voir étape 5 ci-dessus).

⚠️ **Sans webhook, ne comptez pas sur le polling.** Vérifié ici : après 8 minutes, ArgoCD
rejouait toujours la révision précédente. Le contrôleur rafraîchissait bien toutes les 2
minutes (`comparison expired, expiry: 2m0s` dans ses logs) mais re-servait une résolution
de `main` mise en cache, avec `git_ms: 8` — un cache, pas un `ls-remote`. Le polling est un
filet de sécurité, pas le mécanisme de déclenchement ; en production c'est le webhook qui
fait le travail. Pour forcer la main :

```bash
kubectl -n argocd annotate application business-services argocd.argoproj.io/refresh=hard --overwrite
```

**Ce qui n'est PAS ramené par selfHeal** : le nombre de replicas. C'est délibéré —
`ignoreDifferences: /spec/replicas` existe pour que le HPA puisse faire son travail
sans qu'ArgoCD n'annule chaque montée en charge.

## Ce que l'overlay `local` retire, et pourquoi

Voir [overlays/local/kustomization.yaml](k8s/overlays/local/kustomization.yaml) — chaque
suppression y est commentée.

| Retiré / modifié | Raison |
|---|---|
| `Ingress` (classe nginx) | k3d embarque Traefik, pas ingress-nginx |
| `ClusterIssuer` cert-manager | CRD absente → ArgoCD échoue sur un type inconnu |
| replicas → 1, HPA 1-3 | Deux nœuds k3d n'ont pas la marge de la production |
| `BankApi__UseSimulator` → `true` | La base le fixe à `false` (vraie banque). En local, le simulateur porte les scénarios reproductibles par IBAN. Sans ce patch **toutes les sagas échouent en `not_found`** |

### Le namespace `infra`

PostgreSQL, Kafka, Keycloak, Redis et Jaeger vivent dans un namespace séparé, déployés par
une **Application ArgoCD distincte**. Deux raisons : `business-services` applique
`pod-security.kubernetes.io/enforce: restricted`, que les images officielles ne respectent
pas — et relâcher cette contrainte sur le namespace applicatif pour y loger de l'infra
affaiblirait la barrière qui protège les services. En production cette infra est managée et
cette Application n'existe pas.

Ajouté : les 4 Secrets que la production tire d'External Secrets Operator, en clair et
assumés — leurs valeurs sont celles du realm de démonstration, déjà publiques dans
[forterro-realm.json](keycloak/forterro-realm.json).

## État actuel : tout est vert, scénario métier compris

Les deux Applications sont `Synced` + `Healthy`, les 4 services et les 5 composants
d'infra sont `Running`, et le scénario de bout en bout passe **en 10 secondes** :

```
Facture creee -> Emission HTTP 200
[5s]  facture=Issued  | saga=<aucune>
[10s] facture=Paid    | saga=Settled att=1 bank=f680cac601d84238aea77ca4e1f2a6e4
```

Soit toute la chaîne : écriture transactionnelle dans l'outbox → dispatcher → Kafka →
saga orchestrée → appel bancaire simulé → événement `PaymentSettled` → mise à jour de
la facture. Sur Kubernetes, pas en docker-compose.

### Reproduire le scénario

```bash
kubectl -n infra port-forward svc/keycloak 18443:8080 &
kubectl -n business-services port-forward svc/bff 15443:80 &

TOKEN=$(curl -s -X POST http://localhost:18443/realms/forterro/protocol/openid-connect/token \
  -d grant_type=client_credentials -d client_id=erp-product-line \
  -d client_secret=erp-product-line-secret | jq -r .access_token)

# Recuperer l'id par l'en-tete Location, PAS en grepant "id" dans le corps :
# le JSON contient aussi l'id de chaque ligne de facture, et une regex gourmande
# capture le dernier — l'emission repond alors 404 sur un id de ligne.
ID=$(curl -s -D- -o /dev/null -X POST http://localhost:15443/api/v1/invoices \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" -d '{...}' \
  | tr -d '\r' | sed -n 's|^location: /api/v1/invoices/||Ip')

curl -X POST "http://localhost:15443/api/v1/invoices/$ID/issue" -H "Authorization: Bearer $TOKEN"
```

Vérifier côté base :

```bash
kubectl -n infra exec deploy/postgres -- \
  psql -U forterro -d forterro_payments -c "select state, attempts, bank_payment_id from payments.payment_sagas;"
```

### Ce que le port-forward ne couvre pas

Le **chemin navigateur** du BFF (`/bff/login`) ne fonctionne pas depuis l'hôte :
`KC_HOSTNAME` vaut le nom de Service interne, que le navigateur ne résout pas. Le rendre
utilisable demanderait d'exposer Keycloak sur un nom identique dedans et dehors. Le chemin
machine à machine, lui, est complet et c'est celui vérifié ci-dessus.

## Logs centralisés : retrouver une requête à travers les trois services

Elasticsearch, Kibana et un DaemonSet Filebeat tournent dans le namespace `infra`.
Le raisonnement complet est dans l'[ADR 0007](../docs/adr/0007-logs-centralises-elk.md).

```bash
kubectl -n infra port-forward svc/kibana 5601:5601
```

Puis dans Kibana, créer une *data view* sur `forterro-logs-*` (champ temporel `@timestamp`).

**La requête qui justifie tout le dispositif** — prendre le `traceId` d'une réponse
(en-tête `X-Correlation-Id`, ou n'importe quelle ligne de log) et demander :

```
trace.id : "48da105fc40e00e0175c7a30faca245b"
```

Mesuré sur cette installation, pour une émission de facture : **21 documents sur les
4 services** — `payments-worker` (14), `invoicing-api` (4), `bff` (2),
`openbanking-api` (1). L'appel HTTP, l'écriture dans l'outbox, la publication Kafka,
la saga et l'appel bancaire, ordonnés dans le temps. Une seule requête, un seul écran.

⚠️ **Ce chiffre était de 3 documents sur 2 services avant correction.** La trace
cassait à la frontière de l'Outbox — voir [ADR 0007](../docs/adr/0007-logs-centralises-elk.md).
C'est la centralisation elle-même qui a rendu le défaut visible.

`span.id` affine au sein d'une trace ; `service.name` filtre par service ;
`event.code` (l'empreinte `@i` du gabarit Serilog) regroupe toutes les occurrences
d'un **même** événement quelles que soient ses valeurs.

### Le point qui fait échouer la plupart des intégrations

Elastic suit la norme ECS et attend **`trace.id`** / **`span.id`**. Serilog écrit
**`@tr`** / **`@sp`**. Sans le renommage effectué par Filebeat, tout fonctionne en
apparence — les logs arrivent, Kibana les affiche, la recherche plein texte marche —
mais la corrélation par trace est absente, et rien ne dit pourquoi.

Deux autres pièges traités dans [filebeat.yaml](k8s/overlays/local/infra/filebeat.yaml) :

- `/var/log/containers/*.log` ne contient que des **liens symboliques**. Sans
  `prospector.scanner.symlinks: true`, Filebeat ne lit rien et ne signale rien.
  Il faut aussi monter `/var/log/pods`, la cible des liens.
- **Toutes les lignes ne sont pas du JSON.** Une exception .NET non gérée part sur
  stderr en texte brut. Le traitement CLEF est donc conditionné à la présence de `@t` ;
  sans ce garde-fou, ces lignes arriveraient amputées de leur message — or ce sont
  précisément les plus utiles en incident.

Le DaemonSet n'a **aucun droit RBAC** : le filtrage par namespace se fait sur le nom de
fichier (`*_business-services_*.log`), donc il ne parle jamais à l'API Kubernetes. La
liste blanche de l'AppProject reste stricte.

## Deux défauts de production révélés par ce déploiement

Ils étaient invisibles en `docker compose` et le sont devenus à la première exécution
sur un vrai cluster.

**1. `payments-worker` ne pouvait démarrer sur aucun cluster.** `Program.cs:71` appelle
`AddForterroAuthentication`, qui exige `Oidc__Authority` et `Oidc__Audience`
(`[Required]` + `ValidateOnStart`). Le `docker-compose` les fournit, `base/payments-worker.yaml`
non — `invoicing-api` et `openbanking-api` les avaient, le worker était le seul oublié.
Résultat : `OptionsValidationException` au démarrage, CrashLoopBackOff.
**Corrigé** dans `base/`, pas dans l'overlay : le défaut était en production.

**2. La base `payments` n'était migrée nulle part — CORRIGÉ.** `base/migration-job.yaml`
ne contenait un Job que pour `invoicing`, et `Program.cs:98` ne migre qu'en
`IsDevelopment()`. En production le worker démarrait, puis échouait à **chaque** cycle
d'outbox sur `relation "payments.outbox_messages" does not exist` — sans jamais quitter
l'état `Running`, l'erreur étant journalisée et non fatale.

**Et l'image de migration n'existait pas non plus.** Le Job `invoicing-migration`
référençait `ghcr.io/forterro/invoicing-migrations`, qu'aucun Dockerfile ne produisait et
qu'aucun workflow ne publiait : le Job existant était tout aussi cassé que celui qui
manquait. `ImagePullBackOff` garanti, et un hook PreSync en échec **bloque toute** la
synchronisation.

Corrigé par [deploy/migrations/Dockerfile](migrations/Dockerfile) (`dotnet ef migrations
bundle`), un second Job dans `base/`, et la construction des deux images dans
`release.yml`. Le contournement `ASPNETCORE_ENVIRONMENT=Development` de l'overlay local a
été supprimé : les pods tournent en `Production`, comme en production.

## Après un redémarrage de Docker

Le cluster k3d survit, mais **CoreDNS perd l'entrée `host.k3d.internal`** que k3d injecte
à la création. ArgoCD échoue alors sur
`failed to list refs: dial tcp: lookup host.k3d.internal: no such host`, et plus rien ne
se déploie. À restaurer :

```bash
docker network inspect k3d-forterro --format '{{range .IPAM.Config}}{{.Gateway}}{{end}}'
# puis ajouter « <passerelle> host.k3d.internal » au champ NodeHosts :
kubectl -n kube-system edit configmap coredns
kubectl -n kube-system rollout restart deployment coredns
```

Penser aussi à `docker start forterro-gitea`.

## Pièges rencontrés

- **`host.docker.internal` résout vers l'IP LAN** sur ce poste, pas vers la boucle locale :
  le kubeconfig écrit par k3d est injoignable. Corrigé en pointant le serveur sur
  `https://127.0.0.1:<port>`.
- **`kubectl apply` échoue sur la CRD `applicationsets`** : `metadata.annotations: Too long`.
  Le server-side apply n'écrit pas `last-applied-configuration` et passe.
- **Un serveur Git statique ne suffit pas.** ArgoCD utilise go-git, qui exige le protocole
  HTTP *smart*. Un nginx servant un dépôt bare échoue en `failed to list refs: unexpected EOF`.
  D'où Gitea.
- **Kafka refuse de démarrer à cause d'un Service Kubernetes.** Le Service nommé `kafka`
  fait injecter `KAFKA_PORT=tcp://…` dans le pod (service links, héritage de `docker link`).
  L'image Confluent la lit comme l'ancien paramètre `port`, déprécié et incompatible avec
  KRaft : sortie en erreur juste après `port is deprecated`, sans autre message. Corrigé par
  `enableServiceLinks: false`. Le même broker démarre sans problème en docker-compose, qui
  ne génère pas ces variables.
- **La readiness de Kafka se bloquait elle-même.** `kafka-broker-api-versions` bootstrappe
  sur `localhost` puis se **reconnecte sur l'adresse annoncée**, qui passe par le Service —
  lequel n'a aucun endpoint tant que la sonde échoue. Le broker tournait mais n'était jamais
  `Ready`. Remplacé par une sonde TCP locale.
- **Images non téléchargeables depuis le nœud.** `lookup production.cloudfront.docker.com:
  Try again` sur Kafka et Keycloak. Contourné par `docker pull` sur l'hôte puis
  `k3d image import` — la même méthode que pour les images applicatives.
- **Un exécutable mono-fichier a besoin d'un répertoire inscriptible.** Le bundle EF
  extrait ses bibliothèques natives au démarrage, sous le répertoire personnel de
  l'utilisateur — qui n'existe pas avec `adduser --no-create-home`. L'image se construit
  parfaitement et échoue au **premier lancement** sur « Failure processing application
  bundle ». Corrigé par `DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/efbundle` et un `emptyDir`
  monté sur `/tmp`, compatible avec `readOnlyRootFilesystem`.
- **Une synchronisation sans changement ne rejoue pas les hooks PreSync.** Vérifié : un
  `sync` forcé sur une application déjà `Synced` se termine en « no more tasks » sans
  relancer les Jobs. Le Job de migration s'exécute quand il y a quelque chose à
  déployer, pas à chaque passage d'ArgoCD.
- **Un port occupé silencieusement.** Un processus tiers écoutait déjà sur `0.0.0.0:18080` ;
  le port-forward s'est lié sur `127.0.0.1:18080` sans erreur, et les requêtes atterrissaient
  sur l'autre programme (`Realm does not exist`). Vérifier avec `netstat -ano | grep :PORT`
  avant de conclure à une panne applicative.

## Tout démonter

```bash
k3d cluster delete forterro
docker rm -f forterro-gitea
rm -rf C:\Users\PC\forterro-gitops
```
