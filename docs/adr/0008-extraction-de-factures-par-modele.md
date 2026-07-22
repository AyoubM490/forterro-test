# ADR 0008 — Extraction de factures par modèle, derrière une couche anti-corruption

**Statut** : accepté — squelette implémenté, modèle réel non branché
**Date** : 2026-07-22

## Contexte

Il manque à cette plateforme une brique d'intelligence artificielle : intégration d'un fournisseur, inférence, orchestration de modèles.

La façon évidente de cocher la case serait de brancher un assistant conversationnel sur le dépôt. Ce serait un mauvais signal : ça démontrerait qu'on sait câbler un SDK, pas qu'on sait faire de l'ingénierie. Un modèle n'a d'intérêt que là où le déterminisme échoue.

Le besoin réel, dans un ERP, est la **saisie des factures fournisseurs**. Un PDF arrive, quelqu'un retape l'émetteur, l'IBAN, les lignes, la TVA. Volume élevé, valeur ajoutée nulle, taux d'erreur non négligeable. Et surtout : chaque fournisseur a sa propre mise en page, ce qui est précisément le cas où les règles s'effondrent et où l'inférence paie.

Mais c'est aussi le domaine décrit en tête de ce dépôt, celui où l'à-peu-près coûte cher. Une facture enregistrée avec le mauvais IBAN, c'est un virement au mauvais bénéficiaire. **Un modèle peut proposer ; il ne peut pas décider.**

## Décision

> **État d'implémentation.** `Forterro.Intelligence.Api` existe : `IModelConnector`,
> le simulateur déterministe, la validation métier, le routage par confiance,
> l'endpoint et 8 tests. Le modèle réel est **open source et auto-hébergé** —
> `OllamaModelConnector` est écrit et compilé, inactif tant qu'aucun modèle n'est
> configuré. Voir la section « Fournisseur retenu » en fin de document.

Un quatrième service, `Forterro.Intelligence`, sans état, **couche anti-corruption devant les fournisseurs de modèles** — exactement ce que `OpenBanking.Api` est devant les banques ([ADR 0003](0003-base-par-service.md)). Ajouter un fournisseur, c'est écrire une implémentation d'`IModelConnector`, pas modifier le métier.

Huit règles rendent ce choix tenable.

**1. La sortie est contrainte par un schéma JSON**, pas analysée depuis du texte libre. Un modèle à qui on demande « rends-moi du JSON » finit toujours par rendre autre chose.

**2. Le modèle propose, le domaine dispose.** La sortie est traitée comme une **entrée non fiable**, au même titre qu'un formulaire utilisateur : IBAN revalidé au modulo 97 par `Forterro.BuildingBlocks.Banking.Iban`, totaux recalculés, TVA vérifiée, devise contrôlée. Aucune donnée extraite n'entre dans le domaine sans repasser par ses invariants.

**3. Routage par confiance.** En dessous d'un seuil, la facture part en file de revue humaine. Le service ne produit **jamais** qu'un brouillon (`draft`) : l'émission reste un acte humain ou une règle explicite. C'est le même réflexe que `compensation_required` dans la saga — le code dit qu'il faut un humain plutôt que de faire semblant.

**4. La couche anti-corruption est SYNCHRONE ; c'est l'orchestration qui est asynchrone.**

*Corrigé en cours d'implémentation.* La rédaction initiale exigeait un `202` et une
publication par l'Outbox **depuis ce service**. C'était une erreur : elle confondait
« l'utilisateur ne doit pas attendre » et « l'ACL doit être asynchrone », et elle aurait
imposé une base de données à un service explicitement décrit comme sans état.

Le dépôt tranche déjà la question. `OpenBanking.Api` est une ACL **synchrone et sans
état** devant les banques ; c'est `Payments.Worker` — la saga — qui porte l'asynchronisme,
l'état, les reprises et la publication par l'Outbox. La symétrie s'applique telle quelle :
`Intelligence.Api` expose un appel synchrone, et le workflow documentaire qui l'appelle
possède l'état et publie les événements.

Conséquence assumée : **l'appelant bloque pendant l'inférence**. C'est acceptable parce
que cet appelant est un worker de tâche de fond, jamais une requête d'utilisateur — comme
la saga bloque déjà sur l'appel bancaire. Le worker documentaire reste à écrire ; tant
qu'il n'existe pas, il n'y a pas de chemin utilisateur qui attende.

**5. Un connecteur simulé, déterministe, par défaut.** Même principe que les IBAN réservés de `SimulatedBankConnector` : le comportement dépend du contenu du document, pas d'un tirage. **La CI n'appelle jamais un vrai modèle** — ce serait non déterministe *et* facturé.

**6. Le prompt est un contrat versionné**, avec la discipline de l'[ADR 0004](0004-contrats-partages.md) : nom logique, version, et on ne modifie jamais un prompt en production — on crée une V2. Un prompt est une interface, même s'il ressemble à de la prose.

**7. Idempotence par empreinte du document.** Le même PDF soumis deux fois ne déclenche pas deux inférences. C'est une garantie de cohérence *et* une ligne de facture en moins.

**8. Un scope dédié**, `documents:extract`, conforme à la règle de l'[ADR 0005](0005-autorisation-par-scopes.md) : un scope par couple ressource/verbe. Extraire n'est pas écrire une facture.

L'observabilité ne demande aucun travail supplémentaire : les conventions sémantiques GenAI d'OpenTelemetry (modèle, tokens, latence) alimentent Jaeger et Kibana déjà en place ([ADR 0007](0007-logs-centralises-elk.md)).

## Conséquences

**Ce que ça apporte** — la saisie fournisseur cesse d'être manuelle, sans que le système ne fasse jamais confiance au modèle. Changer de fournisseur devient une implémentation, pas une migration.

**Ce que ça coûte** :

- **Le non-déterminisme est irréductible.** Même à température 0, aucun fournisseur ne garantit une sortie identique d'une version de modèle à l'autre. Tout test qui affirme le texte produit finira par casser. On teste donc le comportement de la **couche de validation**, jamais les mots du modèle.
- **Un coût variable au volume**, ce qui n'existe nulle part ailleurs dans ce système. Il faut un plafond de tokens par document, un budget, et un cache par empreinte. Sans ça, une boucle de reprise mal réglée se lit sur une facture fournisseur.
- **Des données qui sortent.** Une facture contient une raison sociale, un IBAN, des montants. Même sous contrat de sous-traitance, les envoyer à un tiers est une décision, pas un réglage. Un modèle auto-hébergé est la seule façon de l'éliminer — au prix de GPU à exploiter.
- **Un modèle se périme.** Les fournisseurs déprécient leurs versions. Épingler une version est obligatoire, et en changer exige de rejouer un corpus de référence. C'est un coût récurrent que la plupart des projets découvrent trop tard.
- **Sans corpus annoté, on ne mesure rien.** Décider si un changement de prompt améliore quoi que ce soit demande un jeu de factures étiquetées. Cette dette-là est le vrai prix d'entrée, bien plus que le code.
- **Un service de plus** à déployer et surveiller.

## Alternatives écartées

- **Un assistant conversationnel sur le dépôt** — sans rapport avec le domaine. Démontre qu'on sait appeler une API.
- **Un modèle pour trier les échecs de paiement dans la saga** — tentant, car ça toucherait le code le plus intéressant. Rejeté : les codes de rejet ISO 20022 forment un ensemble **fini et normalisé**. Une table de correspondance est plus rapide, gratuite, déterministe et auditable. Savoir où ne pas mettre de modèle fait partie de la compétence.
- **OCR classique + règles par fournisseur** — robuste et peu coûteux sur des mises en page stables, s'effondre sur leur variabilité, qui est le problème posé. Reste un complément pertinent en amont, pour fournir du texte propre au modèle.
- **Un service spécialisé document** (Azure Document Intelligence, AWS Textract) — **objectivement meilleur** sur les tableaux et la mise en page qu'un modèle généraliste, et ce serait le choix pragmatique en production. Écarté ici parce qu'il déplace l'essentiel vers un service managé et démontre moins l'orchestration. À reconsidérer sans état d'âme sur un vrai déploiement.
- **Affiner un modèle (fine-tuning)** — prématuré : sans corpus annoté il n'y a rien pour affiner, et sortie structurée plus validation métier couvrent le besoin. À réévaluer quand le jeu d'évaluation existera.
- **Requêtes en langage naturel sur les factures** — extension naturelle, et le vrai sujet d'orchestration (appel d'outils). Volontairement hors de cette décision : traduire du langage naturel en SQL est un piège connu, et la version défendable passe par un DSL de requête contraint. Ce sera l'ADR suivant, s'il a lieu.


## Fournisseur retenu : un modèle ouvert, auto-hébergé

Le choix s'est porté sur un **modèle à poids ouverts servi par Ollama**, plutôt que sur
une API managée.

**Ce que ça règle.** Le coût « des données qui sortent », listé plus haut en
contrepartie, disparaît : une facture porte une raison sociale, un IBAN et des
montants, et rien ne quitte l'infrastructure. Ce n'est plus un contrat de
sous-traitance à négocier, c'est une propriété du déploiement. Le coût variable au
volume disparaît aussi — il devient un coût fixe de GPU.

**Licences — « open source » recouvre des régimes très différents.** Qwen2.5-VL et
Mistral/Pixtral sont sous **Apache 2.0**, compatibles sans réserve avec le MIT du
dépôt. Les poids Llama sont sous *Llama Community License*, qui **n'est pas** une
licence open source au sens OSI et impose des restrictions d'usage. Pour un dépôt qui
publie sept paquets sous MIT, la distinction est structurante.

**Ce que ça coûte en plus, et qui n'était pas dans l'analyse initiale :**

- **Pas de lecture native du PDF.** Une API managée lit un PDF directement ; les
  modèles ouverts prennent des images. Il faut rastériser — donc introduire la seule
  dépendance **native** du dépôt (PDFium, via `PDFtoImage`, MIT). Résolu :
  `PdfiumRasterizer` rend chaque page en PNG à 200 ppp, plafonné à 3 pages, et
  `OllamaModelConnector` soumet une image par page au lieu d'échouer.

  La question qui accompagne toute dépendance native est « se charge-t-elle sur
  l'image de production ? », et elle a été tranchée par mesure, pas par supposition :
  les paquets livrent bien un binaire `linux-musl-x64`, la suite de tests passe dans
  un conteneur Alpine, et une rastérisation exécutée dans l'image `aspnet:9.0-alpine`
  finale — en utilisateur non root — rend la page en 833×555 px. Aucune image Debian
  séparée n'est nécessaire.

  Le coût réel s'est révélé ailleurs, sur le **poids de l'image** : les deux paquets
  natifs livrent sept plateformes, qu'un publish portable copie toutes. Mesuré à
  **724 Mo** contre 191 Mo pour les trois autres services. Un publish fixé sur
  `linux-musl-x64` ne retient que le binaire que ce conteneur peut charger : **185 Mo**.
  C'est le seul service du dépôt à figer son RID, et le Dockerfile dit pourquoi.
- **Le GPU n'est plus optionnel.** Mesuré sur le poste de développement (Intel Iris Xe,
  pas de CUDA) : une inférence de vision se compte en minutes. Utilisable comme banc
  de vérification, pas comme chaîne de production.
- **Le pipeline de résilience existant est inutilisable.** `AddBankingResilience` fixe
  un délai d'expiration de 8 s par tentative — parfaitement calibré pour une API
  bancaire, et fatal pour une inférence. Le réutiliser tuerait chaque appel, avec un
  symptôme ressemblant à une panne de modèle. Une charge liée à des entrées/sorties
  longues a besoin de son propre pipeline. Résolu par `AddInferenceResilience` : délais
  en minutes, `HttpClient` en expiration infinie pour que Polly soit seul arbitre, une
  seule reprise — rejouer une inférence coûte du calcul, pas une socket — et un
  disjoncteur à `MinimumThroughput` de 2 sur une fenêtre de 10 min, sans quoi le seuil
  de 10 du pipeline bancaire ne serait jamais atteint avec des appels de plusieurs
  minutes. Délibérément **non** remonté dans `Forterro.BuildingBlocks` : un seul service
  s'en sert, et généraliser maintenant refigerait des constantes sur un unique cas
  d'usage — l'erreur exacte qui a produit `AddBankingResilience`.

**Ce qui ne change pas** : la sortie reste contrainte par un schéma JSON — Ollama
l'accepte dans son champ `format`, vLLM via son décodage guidé. La garantie « la sortie
ne peut pas être malformée » est identique à celle d'une API managée, et c'est elle qui
distingue cette intégration d'un `prompt` suivi d'un `JSON.parse` optimiste.
