# Historique des versions — AMI·OPS

Développé by alex.b

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
