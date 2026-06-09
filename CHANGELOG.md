# CHANGELOG — CsvLevelsImporter

Format : [vX.Y.Z] - YYYY-MM-DD

---

## [1.0.3] - 2026-06-09

### Description
Ajustements visuels de l'indicateur KDE : suppression du texte d'état superposé,
ajout d'un décalage Y configurable pour les labels, et abréviation "VOL" pour les niveaux volume.

### Ajouté
- **Paramètre `LabelYOffsetTicks`** (défaut 0) : décale verticalement les labels en ticks
  (valeur positive = vers le haut, négative = vers le bas)

### Modifié
- **Labels volume** : "VOLUME" → "VOL" (zones : "ASK+BID+VOL", niveaux simples : "VOL N1", etc.)
- **`OnRender`** : suppression du bloc d'affichage du message de statut `_statusMsg`
  qui se superposait avec d'autres textes du graphique

### Fichiers modifiés
- `KdeNiveauxAuto.cs` — `OnRender()`, `DrawLabel()`, `ClustersToLevels()`,
  propriété `LabelYOffsetTicks`, champ `_labelYOffsetTicks`
- `ATAS_lecture_csv.csproj` — version 1.0.3

---

## [1.0.2] - 2026-06-09

### Description
Filtrage des niveaux KDE non significatifs. Avant ce correctif, `KdePeaks` retournait
tous les maxima locaux de la grille — sur 3 jours MNQ (~2800 pts de range), cela
produisait 30 à 40 lignes couvrant tout le graphique. Deux filtres ajoutés :
densité minimum et nombre maximum de niveaux.

### Ajouté
- **Paramètre `MaxNiveaux`** (défaut 20) : limite le nombre total de niveaux affichés ;
  seuls les N pics les plus denses sont conservés
- **Paramètre `MinDensityPct`** (défaut 10 %) : élimine les pics dont la densité KDE
  est inférieure à X % du pic le plus fort — filtre les petits pics non significatifs

### Modifié
- **`KdePeaks`** : après détection des maxima locaux, application du seuil `MinDensityPct`
  puis sélection des `MaxNiveaux` pics les plus denses (tri descendant par densité)
- **Grille KDE** : passée de 400 à 1000 points pour une meilleure résolution sur
  un large range de prix (MNQ 3 jours ≈ 2000-3000 pts)

### Fichiers modifiés
- `KdeNiveauxAuto.cs` — `KdePeaks()`, ajout propriétés `MaxNiveaux` et `MinDensityPct`
- `ATAS_lecture_csv.csproj` — version 1.0.2

---

## [1.0.1] - 2026-06-08

### Description
Effacement explicite des anciens niveaux avant le tracé des nouveaux, appliqué
aux deux indicateurs. Les anciens niveaux disparaissent immédiatement du graphique
sans attendre la fin du chargement ou du calcul.

### Corrigé
- **CsvLevelsImporter** — `LoadLevelsFromFile()` : `_levels.Clear()` + `RedrawChart()`
  en début de méthode, avant la lecture du fichier CSV — les niveaux supprimés
  disparaissent dès la sauvegarde du CSV
- **CsvLevelsImporter** — `RedrawChart()` ajouté après affectation de `_lastError`
  pour que le message d'erreur s'affiche immédiatement
- **KdeNiveauxAuto** — `Reset()` : `RedrawChart()` ajouté après `_levels.Clear()`,
  avant `RecalculateValues()` — les anciens niveaux KDE disparaissent dès
  qu'un paramètre est modifié, sans attendre la fin du recalcul

### Fichiers modifiés
- `CsvLevelsImporter.cs` — méthode `LoadLevelsFromFile()`
- `KdeNiveauxAuto.cs` — méthode `Reset()`

---

## [1.0.0] - 2026-06-07

### Description
Passage en version production. Toutes les fonctionnalités de v0.2.0 sont validées
et l'indicateur est considéré stable pour un usage réel dans ATAS.
Aucune modification fonctionnelle par rapport à v0.2.0 — uniquement le statut.

### Statut
- Version : **production**
- Archive v0.2.0 créée dans `versions/v0.2.0/`

---

## [0.2.0] - 2026-06-06

### Corrections (bugs)
- **Séparateur CSV** : détection automatique `;` ou `,` — le CSV `niveaux.csv` utilise `;`,
  le code v0.1 ne parsait que `,` → aucun niveau ne s'affichait
- **Décimales françaises** : `25030,5` → `25030.5` géré par regex ciblée
  (ne détruit pas les virgules séparateurs de champ)
- **Thread-safety** : ajout d'un `lock(_levelsLock)` sur toutes les lectures/écritures
  de `_levels` pour éviter les exceptions de concurrence entre `OnCalculate` et `OnRender`
- **Catch muet** : le bloc `catch {}` vide remplacé par logging fichier +
  affichage du message d'erreur sur le graphique en orange

### Nouveautés
- **FileSystemWatcher** : rechargement automatique du CSV à chaque sauvegarde,
  sans attendre l'intervalle de polling (paramètre `UseFileWatcher`, défaut=true)
- **LineType** : 0=solide, 1=tirets, 2=pointillés — géré via `DashStyle` dans `MakePen()`
- **Couleurs hex** : `#RRGGBB` et `#AARRGGBB` désormais supportés
- **Label zone centré** : le label d'une zone (Price2 non vide) est positionné
  au centre vertical `(yTop + yBottom) / 2` plutôt qu'au bord inférieur
- **TextAlignment** : 0=gauche, 1=droite (défaut), 2=centre — utilisé dans `DrawLabel()`
- **ShowPriceOnChart** : affiche les deux bornes pour les zones (hi et lo)
- **ReadFileWithRetry** : 3 tentatives avec 200ms de délai si le fichier est verrouillé
  (édition Excel/Notepad en cours)
- **Palette couleurs** étendue : salmon, turquoise, violet, silver, teal, hotpink...
- **Log d'erreurs** : `csv_levels_errors.log` dans le répertoire CSV

### Refactoring
- Fichier renommé `CsvLevelsImporter.cs` (sans numéro de version dans le nom)
- `Class1.cs` vide non utilisé (conserver dans le projet VS ou supprimer)
- Séparation claire des responsabilités :
  `ParseCsv` / `DetectSeparator` / `SplitLine` / `ParseColor` / `MakePen`
- `_isLoading` déclaré `volatile` pour visibilité inter-threads

---

## [0.1.0] - 2026-05 (version initiale — atas_csv_levels_importer_v0.1.cs)

### Implémenté
- Lecture CSV avec chemins configurables
- Tracé de lignes horizontales (ligne simple)
- Tracé de rectangles (zones, si Price2 non vide)
- Labels à droite avec police configurable
- Transparence globale
- Intervalle de rechargement paramétrable
- Palette de 15 couleurs nommées
