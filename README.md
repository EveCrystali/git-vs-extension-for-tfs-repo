# Git for TFS — extension Visual Studio 2022

Une extension Visual Studio 2022 qui ajoute une **fenêtre d'outil Git autonome**, utilisable
même lorsque le contrôle de code source intégré de Git est désactivé parce que la solution
se trouve à l'intérieur d'un espace de travail **TFVC (TFS)**.

## Le problème

Sur un dépôt comme *Datamanager* (50+ projets versionnés en TFVC/TFS), Visual Studio lie
TFVC comme fournisseur de contrôle de code source actif. Or **VS n'autorise qu'un seul
fournisseur actif à la fois** : impossible donc d'avoir en parallèle le support Git intégré.
On se retrouve à utiliser TFVC pour les check-ins serveur et à piloter le dépôt git local
avec des outils externes (ligne de commande, GitKraken, etc.), sans intégration dans l'IDE.

## L'approche

Cette extension **ne s'enregistre pas** comme fournisseur de contrôle de code source
(pas de `ProvideSourceControlProvider`). C'est volontaire et c'est toute l'astuce : elle ne
rentre jamais en concurrence pour l'unique emplacement de fournisseur actif, donc **TFVC
continue de fonctionner sans être perturbé**.

À la place, elle expose une fenêtre d'outil WPF qui **pilote directement `git.exe`**
(via la ligne de commande) sur le dossier de dépôt git de votre choix. Vous gardez :

- **TFVC** pour les check-ins serveur et le suivi « lourd »,
- **Git** (via cette fenêtre) pour tout le travail local et les implémentations au fil de l'eau,

le tout sans quitter Visual Studio.

## Fonctionnalités

- Détection automatique du dépôt git à partir de la solution ouverte (ou saisie/collage
  manuel du chemin du dossier `.git`), mémorisée d'une session à l'autre.
- Onglet **Changes** : fichiers *staged* / *unstaged* / non suivis, stage/unstage
  (par fichier ou tout), *discard*, zone de message et boutons **Commit** / **Commit & Push**.
- Onglet **Branches** : liste des branches locales avec suivi amont (ahead/behind),
  *checkout*, création de branche (+ checkout).
- Onglet **History** : les 100 derniers commits (hash court, sujet, auteur, date relative).
- Barre de synchronisation : **Fetch**, **Pull** (`--ff-only`), **Push**
  (avec `--set-upstream` automatique si la branche n'a pas d'amont), **Refresh**.
- Toutes les commandes git exécutées sont tracées dans une sortie dédiée
  **Affichage → Sortie → « Git for TFS »**.

## Prérequis

- Visual Studio 2022 (17.x), charge de travail **Visual Studio extension development**
  (SDK VSSDK) pour compiler.
- **Git** installé et présent dans le `PATH`.

## Compilation

```
git clone <ce dépôt>
```

1. Ouvrir `GitForTfs.sln` dans Visual Studio 2022.
2. Restaurer les paquets NuGet (automatique au premier build) — cela récupère
   `Microsoft.VisualStudio.SDK` et `Microsoft.VSSDK.BuildTools`.
3. Compiler en `Release`. Le VSIX est produit dans
   `src/GitForTfs/bin/Release/GitForTfs.vsix`.

Pour déboguer, lancer simplement le projet (F5) : une **instance expérimentale** de Visual
Studio démarre (`/rootsuffix Exp`) avec l'extension chargée.

## Installation

Double-cliquer sur `GitForTfs.vsix` (VS fermé), ou passer par
**Extensions → Gérer les extensions**. Redémarrer Visual Studio.

## Utilisation

1. Ouvrir votre solution *Datamanager* (sous contrôle TFVC).
2. **Affichage → Git for TFS** pour afficher la fenêtre (elle se dock à côté de
   l'Explorateur de solutions).
3. Si le dépôt git n'est pas détecté automatiquement (par ex. le `.git` est dans un
   sous-dossier), coller le chemin du dossier git dans le champ **Repo** puis cliquer
   **Set**. Le choix est mémorisé.
4. Travailler normalement : stager, committer, changer de branche, push/pull — pendant
   que TFVC reste disponible pour les check-ins serveur.

## Structure du code

```
src/GitForTfs/
├── GitForTfsPackage.cs          Point d'entrée (AsyncPackage) — PAS un fournisseur SCC
├── GitForTfsPackage.vsct        Table de commandes (entrée du menu Affichage)
├── PackageIds.cs                GUID/IDs partagés code <-> vsct
├── Commands/                    Commande d'ouverture de la fenêtre d'outil
├── ToolWindows/                 Fenêtre d'outil + vue WPF (XAML)
├── ViewModels/                  MVVM : orchestration et éléments de liste
├── Mvvm/                        Base INotifyPropertyChanged, commandes, convertisseurs
└── Services/                    Wrapper git CLI, modèles, persistance, log
```

Le pilotage de git se fait dans `Services/GitCliService.cs`, qui lance `git.exe` de façon
asynchrone (sans jamais bloquer le thread UI) et parse la sortie *porcelain*.
```
