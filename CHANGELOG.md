# Historique des versions — Tech IT AB / PC Santé AB (ex-AMI·OPS)

Développé by alex.b

## 1.7.0 — 27/11/2026

### Renforts cyber (page Cyber, Tech IT AB)
- Sysmon (Microsoft Sysinternals) : installation par winget + configuration de journalisation fine (processus, réseau, injection, accès lsass, Démarrage, clés Run) ; lecture des événements à fort signal des 3 derniers jours, classés MITRE ATT&CK (T1055 injection, T1003 vol d'identifiants LSASS, PowerShell encodé, persistance).
- Autoruns : `autorunsc` (signatures + VirusTotal), remontée des entrées non signées, en dossier utilisateur, orphelines ou détectées par plusieurs moteurs.
- Durcissement CIS Benchmark / ANSSI : audit de ~20 points de configuration avec note /100, références CIS et ANSSI et remédiation par point. Lecture seule (n'applique aucune modification).
- Les constats s'ajoutent au rapport cyber du client. Outils Sysinternals installés depuis winget, jamais embarqués.

### Tests
- `tests/DefenseTests.cs` (T17) : 23 contrôles (découpe CSV Autoruns, évaluation du démarrage, durcissement CIS/ANSSI, mapping Sysmon), exécutés sous Mono et sur Windows en CI.
- Contrôle d'isolation de PC Santé AB étendu aux types Sysmon / Autoruns / Cis.

## 1.6.0 — 26/11/2026

### Protection « sous mon nom » (anti-copie)
- Contrôle d'authenticité au démarrage : l'exe vérifie sa propre signature Authenticode (WinVerifyTrust) contre l'empreinte épinglée d'alex.b (`Ed.SignThumb`). États : authentique / non signée / altérée.
- Une copie modifiée, recompilée ou re-signée par un tiers (« rebranding via une IA ») passe en « copie non autorisée » : bandeau et désactivation des fonctions sensibles (Cyber, labo, supervision).
- Honnêteté : garde-fou contournable par un reverse-engineer déterminé ; la protection réelle reste la signature du certificat + le dépôt source privé.

### Supervision des postes clients (opt-in, modèle agent GLPI)
- PC Santé AB enrôlé par code client envoie un état technique (nom du PC, série, modèle, Windows, disque, antivirus, bilan) à l'endpoint d'inventaire GLPI d'alex.b, **sans aucun jeton d'admin** (comme un agent GLPI, étiquette = code client).
- Opt-in et visible : bandeau permanent « poste supervisé par alex.b » + acceptation de l'utilisateur ; sans enrôlement, aucune remontée (cloisonnement préservé).
- Battement quotidien au démarrage + envoi manuel depuis l'accueil.
- Tech IT AB : « Postes supervisés » dans Parc client relit les états reçus avec le jeton d'alex.b (sa machine uniquement).

### Interne
- Helper HTTP déplacé dans un fichier partagé par les deux éditions.

### Tests
- `tests/SupervTests.cs` (T16) : 19 contrôles (Tech IT AB) / 18 (PC Santé AB), exécutés sous Mono et sur Windows en CI.

## 1.5.0 — 26/11/2026

### Page « Jeu » (Tech IT AB et PC Santé AB)
- Analyse notée sur 100 : pilote graphique (version au format NVIDIA, âge), fréquence de l'écran sous son maximum, écran branché sur la carte mère alors qu'une carte dédiée est présente, mémoire (simple canal, XMP/EXPO), Mode Jeu, enregistrement en arrière-plan, HAGS, plan d'alimentation, souris, fichier d'échange, Visual C++, périphériques en erreur, jeux sur disque dur, applis ouvertes, Wi-Fi, pilotes Windows Update.
- Optimiser : seuls les réglages utiles sur le PC sont proposés, point de restauration, valeurs d'origine conservées (la première connue), annulation complète. Processeurs AMD : plan Équilibré conservé.
- Session de jeu (fermeture propre, liste des processus jamais fermés) et réouverture sans droits administrateur.
- Écran à sa fréquence maximale avec essai de 15 s et retour automatique.
- Pilotes (NVIDIA : température, lien PCIe), overclocking expliqué mais jamais appliqué, bibliothèque Steam/Epic, carte graphique par jeu, latence réseau, composants des jeux et outils officiels via winget.

### Scénarios dans PC Santé AB
- PC lent, écran bleu, problème réseau et nouveau scénario « Prêt à jouer » (aussi dans Tech IT AB) ; rapport texte dans `Documents\PC Santé AB`.

### Corrections
- Durcissement anti-scan (1.4) : la sauvegarde des réglages d'origine tenait sur plusieurs lignes dans `config.ini` ; seule la première survivait au redémarrage de l'appli, le retour arrière ne remettait alors que LLMNR. Valeur enregistrée en base64 sur une ligne, ancien format toujours lu.

### Tests
- `tests/GameTests.cs` (T15) : 61 contrôles par édition, exécutés sous Mono et sur Windows en CI (`build.ps1 -Tests`).

## 1.4.0 — 26/11/2026

### Deux éditions
- **Tech IT AB** remplace AMI·OPS (données reprises automatiquement, ancienne version désinstallée par le Setup).
- **PC Santé AB** : édition client compilée sans le code sensible (Nmap, clients, parc, GLPI, coffre, Multi-PC, AD, secours) ; découverte réseau sans test de ports ; vérifiée par la CI.

### Cyber (Tech IT AB)
- Scans Nmap encadrés : périmètre et autorisation écrite par client, journal des scans, profils fixes, scripts offensifs refusés.
- Règles de risque ANSSI/CIS/MITRE, comparaison avec le scan précédent, rapport PDF et envoi GLPI.
- Chasse aux menaces dans les journaux, persistance, connexions, mot de passe compromis (Have I Been Pwned), fichier suspect (VirusTotal).
- Labo défensif : analyse d'e-mail et de lien (hameçonnage, IP loggers), détection et surveillance des scans reçus, durcissement anti-scan réversible, test de sécurité d'un programme et comportement avant/après.

### Corrections
- Le mot de passe d'un programme lancé n'apparaît plus dans le journal (masquage).

## 1.3.0 — 26/11/2026

### Ajouts (projet GLPI Amiprotek)
- Page « Parc client » : recette de livraison, PV signé envoyé dans GLPI (ticket, PC lié, temps passé, PV en PDF, résolution), séquestre de la clé BitLocker dans le coffre.
- Enrôlement d'un PC existant : numéro libre calculé depuis GLPI, GLPI Agent avec l'étiquette du client (empreinte SHA-256 vérifiée), agent Tactical RMM, BitLocker, renommage.
- État du parc par client (GLPI + Tactical RMM), ticket par poste, rapport mensuel de conformité (PDF via Microsoft Edge), tickets GLPI ouverts (suivi, tâche avec temps, résolution).
- NAS → GLPI : contrôle de santé des NAS et ticket dans l'entité du client.
- Multi-PC : canal Tactical RMM pour les postes des clients.
- Santé des serveurs (HTTPS, expiration des certificats) avec contrôle quotidien.

### Améliorations
- « Annuler la tâche » arrête le programme en cours et ses processus enfants.
- Safety Scanner : signature vérifiée jusqu'à la racine Microsoft.
- Une intervention déjà liée à un ticket GLPI complète ce ticket au lieu d'en créer un autre.
- Mise à jour : empreinte du certificat de signature Amiprotek proposée dans les réglages.

## 1.2.0 — 26/11/2026
- Centre des tâches, scénarios, icône de notification, applis intégrées, Multi-PC (WinRM), connecteurs GLPI et Tactical RMM, mise à jour automatique, autotest, CI GitHub Actions.
- Sécurité : fichiers exécutés en administrateur préparés dans un dossier réservé, démarrage automatique réservé à la version installée, signature de mise à jour exigée valide.

## 1.1.0
- Optimisation notée sur 10, dépannage, diagnostic réseau, audit de sécurité, Serveur AD, script Mac.

## 1.0.0
- Première version : clients, interventions, PanneScan, réseau, messagerie, coffre, secours.
