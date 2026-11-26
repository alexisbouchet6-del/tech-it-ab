# Historique des versions — Tech IT AB / PC Santé AB (ex-AMI·OPS)

Développé by alex.b

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
