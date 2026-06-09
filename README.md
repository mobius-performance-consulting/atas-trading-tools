# ATAS Lecture CSV — Indicateurs ATAS

**Version** : 1.0.2
**Statut**  : Production
**Auteur**  : Philippe L.
**Date**    : 2026-06-09

## Objectif

Suite d'indicateurs ATAS (C#) et de scripts Python pour la préparation de session de trading :

- **CsvLevelsImporter** : lit un fichier CSV local contenant des niveaux de prix et les trace sur le graphique (lignes et zones). Rechargement automatique via FileSystemWatcher.
- **KdeNiveauxAuto** : calcule automatiquement les niveaux KDE (Bid/Ask/Volume) au chargement et les affiche directement sur le graphique.
- **OhlcExporter** : exporte l'historique OHLC + Volume + Bid/Ask depuis ATAS vers Chart.csv.
- **kde_niveaux.py** : calcule les niveaux KDE depuis Chart.csv et écrit niveaux.csv.
- **kde_backtest.py** : backtest roll-forward des niveaux KDE vs aléatoire (Z-score, lift, Monte-Carlo).

## Résultats backtest (v1.0.2)

| Métrique | Valeur |
|---|---|
| Période | juil. 2025 → juin 2026 (243 sessions) |
| Instrument | MNQ Futures 5 min |
| Bull lift | **9,64×** (Z = +154) |
| Bear lift | **10,23×** (Z = +162) |
| p-value | < 0,00001 |
| Zones de confluence | 96% des hits |

Les rapports complets sont dans `docs/`.

## Format CSV (CsvLevelsImporter)

```
Price;Price2;Note;Color;LineType;LineWidth;TextAlignment
25000;;POC veille;gold;0;2;1
25120;25140;Zone volume;cyan;0;1;1
24950;;Support;#00FF88;1;2;0
```

- Séparateur : `;` ou `,` (détection automatique)
- Décimales françaises supportées : `25030,5` → `25030.5`
- `Price2` non vide → zone (rectangle)
- `LineType` : 0=solide, 1=tirets, 2=pointillés
- `TextAlignment` : 0=gauche, 1=droite, 2=centre
- Couleurs : nommées (`gold`, `cyan`, `red`…) ou hex (`#RRGGBB`, `#AARRGGBB`)

## Compilation et déploiement

```powershell
# Compiler et déployer la DLL dans ATAS
.\deploy.ps1
```

Ou manuellement : ouvrir `ATAS_lecture_csv\ATAS_lecture_csv.csproj` dans Visual Studio et compiler (net10.0).

## Paramètres KDE (KdeNiveauxAuto)

| Paramètre | Défaut | Description |
|---|---|---|
| K Days | 3 | Sessions d'entraînement |
| KDE Bandwidth (ticks) | 60 | Largeur de bande KDE |
| Zone Ticks | 80 | Distance max de fusion en zone |
| Max Niveaux | 15 | Nombre max de niveaux affichés |
| Densité min (%) | 15 | Seuil de densité minimum |
| Transparence | 70% | Transparence des zones |

## Historique des versions

| Version | Date | Statut | Description |
|---|---|---|---|
| 1.0.2 | 2026-06-09 | **Production** | Filtre densité KDE + MaxNiveaux — lift 10× |
| 1.0.1 | 2026-06-08 | Archivée | Effacement explicite des anciens niveaux avant rechargement |
| 1.0.0 | 2026-06-07 | Archivée | Passage en production — fonctionnalités v0.2.0 validées |
| 0.2.0 | 2026-06-06 | Archivée | Réécriture complète — fix parsing CSV, thread-safety, FileWatcher |
| 0.1.0 | 2026-05 | Archivée | Version initiale |
