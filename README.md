# UrsaNAS

UrsaNAS est un NAS maison : une interface web (français / anglais) pour gérer, sur un Debian, les disques, le stockage ZFS, le partage de fichiers, les comptes, la surveillance et les sauvegardes sur disque USB.

Il est écrit en C# (.NET 10) avec MudBlazor, SQLite et Dapper.

## Ce que l'application permet de faire

Le menu est rangé en quatre groupes.

| Groupe | Pages | À quoi ça sert |
|---|---|---|
| **Storage** | Disks, Pools, Datasets, Backups | Voir les disques, créer des pools et des datasets ZFS, prendre des snapshots, sauvegarder sur un disque USB |
| **Sharing** | Shares, Folders, Services | Partager des dossiers en SMB, NFS et FTP, régler les permissions (ACL) par dossier, démarrer ou arrêter les services |
| **Accounts** | Users, Groups, Admins | Comptes du NAS, groupes, administrateurs de l'interface |
| **Monitoring** | Dashboard, Alerts, Audit log | État de santé, alertes (avec courriel), journal des actions |

### Partages

- **SMB (Samba)** : permissions fines par dossier (ACL, utilisateurs et groupes).
- **NFS** : par partage, avec la liste des clients autorisés.
- **FTP (vsftpd)** : avec les comptes du NAS, chacun enfermé dans son dossier. Le service s'active depuis la page *Services*.

Les ACL ne s'appliquent qu'à SMB. NFS et FTP donnent l'accès du partage entier.

### Sauvegardes

La page **Backups** copie un partage (ou certains dossiers) vers un **disque USB branché au NAS**. La copie est faite par le moteur [Ursacopy](https://github.com/FrankJacket/Ursacopy), inclus comme sous-module Git.

- **C'est une copie lisible, pas une image restaurable.** Les fichiers sont copiés tels quels, sans permissions ni ACL. Le disque se lit sur n'importe quel ordinateur (NTFS et exFAT conviennent).
- **Un travail = un partage** (ou quelques dossiers) copié dans un dossier du disque. Plusieurs travaux peuvent partager le même disque.
- **Planification** : à la main, tous les jours à une heure donnée, ou dès que le disque est branché.
- **Option snapshot ZFS** : pour un partage sur un dataset ZFS, la copie part d'un snapshot temporaire. Les fichiers qui bougent pendant la copie (bases de données, disques virtuels) sont donc copiés de façon cohérente. Le snapshot est supprimé ensuite.
- **Corbeille** : les fichiers supprimés ou remplacés sont gardés N jours (30 par défaut) sur le disque.
- **Vérification** : option pour relire chaque fichier copié.
- **Alertes** : une copie en échec, ou un travail planifié sans copie réussie depuis 3 jours, déclenche une alerte.

**Premier usage**

1. Branchez le disque et **montez-le à la main** sous `/mnt` ou `/media`. L'interface ne monte pas les disques.
2. Dans *Backups*, créez un travail et choisissez le disque.
3. Cliquez sur **Initialiser le disque**. Un petit fichier marqueur (`.ursacopy-target`) est écrit à la racine. Il évite d'écrire sur un autre disque monté au même endroit.
4. Cliquez sur **Lancer maintenant**.

Sécurité : l'agent n'accepte comme cible qu'un point de montage sous `/mnt`, `/media` ou `/run/media`. L'interface n'envoie jamais de chemin source : l'agent retrouve lui-même le dossier du partage.

## Architecture

```
Navigateur ──HTTP :8080──▶ Nas.Web ──socket unix──▶ Nas.Agent (root) ──▶ zfs, smb, nfs, vsftpd, ACL, copie…
                              │
                           SQLite (partages, historique, alertes, journal)
```

| Projet | Rôle |
|---|---|
| `src/Nas.Agent` | Service qui tourne en root. Exécute les commandes système et les copies. Écoute uniquement sur `/run/ursanas/agent.sock`. |
| `src/Nas.Web` | Interface et base de données. Ne touche pas au système : elle demande tout à l'agent. Gère aussi le planificateur de sauvegardes et les alertes. |
| `src/Nas.Contracts` | Types et règles de validation partagés (noms de partage, ACL, NFS, FTP, sauvegardes). |
| `external/Ursacopy` | Sous-module Git : moteur de copie. |
| `tests/` | Tests unitaires (xUnit), dont la vérification que tous les textes de l'interface ont leur traduction française. |
| `deploy/` | Script de déploiement, installation, services systemd. |

Principe : **l'agent est le seul à avoir les droits root**, et il vérifie tout ce que l'interface lui demande. Pendant la copie, l'agent garde l'avancement en mémoire. L'interface enregistre le résultat dans l'historique, puis demande à l'agent de l'oublier.

## Installer

### Prérequis sur la cible

Un Debian 13 avec ZFS (pour les pools et datasets) et un accès SSH avec `sudo`. Le script installe lui-même NFS, vsftpd et openssl, laissés arrêtés jusqu'à leur activation dans la page *Services*. Pour des disques de sauvegarde exFAT, installez `exfatprogs`.

### Déployer depuis Windows

Le script publie l'agent et l'interface pour `linux-x64` (sans .NET sur la cible), les copie par SSH et redémarre les services.

```powershell
git clone --recurse-submodules https://github.com/FrankJacket/UrsaNAS.git
cd UrsaNAS
./deploy/deploy.ps1                       # cible par défaut : ursanas@Ursa.mshome.net
./deploy/deploy.ps1 -Target user@mon-nas  # autre cible
```

L'interface est ensuite sur `http://<adresse-du-nas>:8080`. À la première visite, un formulaire crée le premier administrateur.

Si vous avez cloné sans `--recurse-submodules` :

```bash
git submodule update --init
```

Le dépôt d'Ursacopy est privé : il faut y avoir accès avec votre compte GitHub.

## Développer

```bash
dotnet test        # tous les tests
```

Mode simulé, sans système réel ni droits root (utile sous Windows) :

```powershell
$env:URSANAS_FAKE = '1'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Backup__Roots__0 = 'C:/temp/mnt'        # dossier qui tient lieu de /mnt
$env:Backup__RequireMountPoint = 'false'     # accepte un simple dossier comme disque
$env:ASPNETCORE_URLS = 'http://localhost:5190'
dotnet run --project src/Nas.Agent --no-launch-profile
# dans un autre terminal :
$env:ASPNETCORE_URLS = 'http://localhost:5199'; $env:Agent__Url = 'http://localhost:5190/'
dotnet run --project src/Nas.Web --no-launch-profile
```

En mode simulé, les commandes système répondent « OK » sans rien faire. Pour que la liste des disques de sauvegarde ne soit pas vide, définissez `Backup__MountInfo` : un fichier au format `/proc/self/mountinfo` qui déclare votre faux disque. `Backup__RequireMountPoint=false` seul suffit pour tester une copie, pas pour faire apparaître le disque dans la liste.

### Textes de l'interface

Les textes sont écrits en anglais dans le code, par exemple `L["Save"]`. Chaque texte doit avoir sa traduction dans `src/Nas.Web/Localization/French.cs`, sinon un test échoue.

## Configuration utile

| Clé | Défaut | Effet |
|---|---|---|
| `Backup:Roots` | `/mnt`, `/media`, `/run/media` | Dossiers sous lesquels un disque de sauvegarde peut être monté |
| `Backup:RequireMountPoint` | `true` | `false` accepte un simple dossier (développement seulement) |
| `Backups:IntervalSeconds` | `20` | Fréquence du planificateur de sauvegardes |
| `DataDir` | `/var/lib/ursanas-web` | Dossier de la base SQLite de l'interface |
