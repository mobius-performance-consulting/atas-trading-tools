"""
kde_niveaux.py  -  Niveaux KDE Volume / Bid / Ask depuis Chart.csv (ATAS)
=========================================================================
Version : 1.3.0
Auteur  : Philippe L.

Lit    : Chart.csv    (export ATAS, heure locale Paris)
Ecrit  : niveaux.csv  (recharge par CsvLevelsImporter dans ATAS)

Format Chart.csv (separateur ;) :
  # Timeframe: 30s                          <- ligne commentaire optionnelle
  DateTime;Open;High;Low;Close;Volume;Bid;Ask;Delta
  07/06/2026 22:00:00;5900.25;5901.00;...   <- bougies 30s avec secondes

Detection des sessions CME (automatique, independant du fuseau horaire) :
  - Un gap > GAP_THRESHOLD_MIN (45 min) = pause CME quotidienne
  - Date de session = date(premiere bougie apres le gap) + 1 jour
    → fonctionne en hiver (pause 21h-22h) ET en ete (pause 22h-23h)
    → fonctionne aussi le week-end (gap vendredi->dimanche)

Algorithme KDE :
  - Bid  : LOW  pondere bid_volume  (vendeurs frappent les bids)
  - Ask  : HIGH pondere ask_volume  (acheteurs touchent les asks)
  - Vol  : HL combine pondere volume total

Sortie niveaux.csv (separateur ;) :
  Price;Price2;Note;Color;Line Type;Line Width;Text Alignment

Usage :
  python kde_niveaux.py [--input Chart.csv] [--output niveaux.csv]
                        [--k-days 3] [--tick-size 0.25]
                        [--kde-bandwidth-ticks 60] [--zone-ticks 30]
                        [--gap-threshold 45]
"""

from __future__ import annotations

import argparse
import csv
import math
from dataclasses import dataclass
from datetime import datetime, date, timedelta
from pathlib import Path
from typing import Dict, List, Optional, Tuple

# =============================================================================
# CONFIG
# =============================================================================

DATA_DIR = Path(r"C:\Users\phili\OneDrive\Traiding_PL\Codes\ATAS_lecture_csv")

GAP_THRESHOLD_MIN = 45   # gap > 45 min = pause CME

@dataclass
class Config:
    input_file:           Path  = DATA_DIR / "Chart.csv"
    output_file:          Path  = DATA_DIR / "niveaux.csv"
    k_days:               int   = 3        # nb journees d'entrainement (J-k .. J-1)
    tick_size:            float = 0.25
    kde_bandwidth_ticks:  float = 60.0
    kde_grid_size:        int   = 1000
    zone_ticks:           int   = 30
    max_niveaux:          int   = 20
    min_density_pct:      float = 10.0
    gap_threshold:        int   = GAP_THRESHOLD_MIN  # minutes


COLORS = {
    "bid":    "green",
    "ask":    "red",
    "volume": "cyan",
    "zone":   "yellow",
}

# =============================================================================
# CLI
# =============================================================================

def parse_args() -> Config:
    p = argparse.ArgumentParser(description="KDE Bid/Ask/Volume -> niveaux.csv (ATAS)")
    p.add_argument("--input",               type=Path,  default=None)
    p.add_argument("--output",              type=Path,  default=None)
    p.add_argument("--k-days",              type=int,   default=3)
    p.add_argument("--tick-size",           type=float, default=0.25)
    p.add_argument("--kde-bandwidth-ticks", type=float, default=60.0)
    p.add_argument("--kde-grid-size",       type=int,   default=1000)
    p.add_argument("--zone-ticks",          type=int,   default=30)
    p.add_argument("--max-niveaux",         type=int,   default=20,
                   help="Nombre max de niveaux par type (defaut 20)")
    p.add_argument("--min-density-pct",     type=float, default=10.0,
                   help="Densite min en %% du pic max pour garder un niveau (defaut 10)")
    p.add_argument("--gap-threshold",       type=int,   default=GAP_THRESHOLD_MIN,
                   help="Gap en minutes pour detecter la pause CME (defaut 45)")
    a = p.parse_args()

    cfg = Config()
    if a.input:               cfg.input_file          = a.input
    if a.output:              cfg.output_file         = a.output
    if a.k_days:              cfg.k_days              = a.k_days
    if a.tick_size:           cfg.tick_size           = a.tick_size
    if a.kde_bandwidth_ticks: cfg.kde_bandwidth_ticks = a.kde_bandwidth_ticks
    if a.kde_grid_size:       cfg.kde_grid_size       = a.kde_grid_size
    if a.zone_ticks:          cfg.zone_ticks          = a.zone_ticks
    if a.max_niveaux:         cfg.max_niveaux         = a.max_niveaux
    if a.min_density_pct:     cfg.min_density_pct     = a.min_density_pct
    cfg.gap_threshold = a.gap_threshold
    return cfg


# =============================================================================
# DETECTION SESSIONS CME PAR GAP
# =============================================================================

def assign_sessions(raw_rows: list, gap_threshold_min: int = 45) -> list:
    """
    Detecte les sessions CME en repérant les gaps > gap_threshold_min minutes.

    Regle :
      session_date = date(premiere_bougie_apres_gap) + 1 jour

    Exemples :
      Hiver (UTC+1) : pause 21h-22h lundi  -> premiere bougie = 22h lundi
                      -> session = mardi  ✓
      Ete  (UTC+2)  : pause 22h-23h lundi  -> premiere bougie = 23h lundi
                      -> session = mardi  ✓
      Week-end      : gap vendredi->dimanche 22h ou 23h
                      -> session = lundi  ✓

    La premiere session du fichier (sans gap precedent) utilise la meme regle :
      session = date(premiere_bougie) + 1 jour.
    Cette session peut etre incomplete mais elle est utilisee en entrainement
    et sera naturellement exclue par le critere k_days.
    """
    if not raw_rows:
        return raw_rows

    gap = timedelta(minutes=gap_threshold_min)
    result = []
    current_session = (raw_rows[0]["datetime"] + timedelta(days=1)).date()

    for i, row in enumerate(raw_rows):
        if i > 0:
            delta = row["datetime"] - raw_rows[i - 1]["datetime"]
            if delta > gap:
                current_session = (row["datetime"] + timedelta(days=1)).date()
        row["session"] = current_session
        result.append(row)

    return result


# =============================================================================
# LECTURE Chart.csv (format ATAS)
# =============================================================================

def _parse_atas_datetime(s: str) -> Optional[datetime]:
    """
    Supporte les formats exportes par OhlcExporter :
      dd/MM/yyyy HH:mm:ss  (timeframe < 60s, ex. bougies 30s)
      dd/MM/yyyy HH:mm     (timeframe >= 1m)
    Aussi les anciens formats pour compatibilite.
    """
    s = s.strip()
    for fmt in (
        "%d/%m/%Y %H:%M:%S",   # principal : 07/06/2026 22:00:30
        "%d/%m/%Y %H:%M",      # 07/06/2026 22:00
        "%Y-%d-%m %H:%M:%S",   # ancien ATAS inverse
        "%Y-%m-%d %H:%M:%S",   # ISO
        "%Y-%d-%m %H:%M",
        "%Y-%m-%d %H:%M",
    ):
        try:
            return datetime.strptime(s, fmt)
        except ValueError:
            continue
    return None


def _parse_float(s: str) -> Optional[float]:
    s = s.strip().replace(",", ".")
    try:
        return float(s)
    except ValueError:
        return None


def load_chart_csv(path: Path, cfg: Config) -> Tuple[List[Dict], int]:
    """
    Lit Chart.csv (separateur ;).
    Retourne (rows, timeframe_seconds).

    La detection des sessions CME se fait par gap (assign_sessions) :
    aucune heure fixe, fonctionne en hiver comme en ete.
    """
    raw_rows = []
    timeframe_sec = 0

    with path.open("r", encoding="utf-8-sig", newline="") as f:
        sample = f.read(4096)
        f.seek(0)
        sep = ";" if sample.count(";") >= sample.count(",") else ","

        reader = csv.reader(f, delimiter=sep)
        prev_dt: Optional[datetime] = None

        for raw in reader:
            if not raw or not raw[0].strip():
                continue

            first = raw[0].strip()

            # Ligne commentaire timeframe ex: "# Timeframe: 30s"
            if first.startswith("#"):
                txt = first.lstrip("# ").lower()
                if "timeframe:" in txt:
                    val = txt.split("timeframe:", 1)[1].strip()
                    try:
                        if val.endswith("s"):
                            timeframe_sec = int(val[:-1])
                        elif val.endswith("m"):
                            timeframe_sec = int(val[:-1]) * 60
                        elif val.endswith("h"):
                            timeframe_sec = int(val[:-1]) * 3600
                    except ValueError:
                        pass
                continue

            # Ligne d'en-tete
            if first.lower() in ("datetime", "date", "date time"):
                continue

            if len(raw) < 8:
                continue

            dt = _parse_atas_datetime(first)
            if dt is None:
                continue

            # Calcul du timeframe depuis les ecarts si pas en entete
            if timeframe_sec == 0 and prev_dt is not None:
                diff = int(round((dt - prev_dt).total_seconds()))
                if 0 < diff <= 3600:   # ignore les gros gaps (pauses CME)
                    timeframe_sec = diff
            prev_dt = dt

            o   = _parse_float(raw[1])
            h   = _parse_float(raw[2])
            l   = _parse_float(raw[3])
            c   = _parse_float(raw[4])
            vol = _parse_float(raw[5])
            bid = _parse_float(raw[6])
            ask = _parse_float(raw[7])

            if any(v is None for v in (o, h, l, c, vol, bid, ask)):
                continue

            raw_rows.append({
                "datetime": dt,
                "session":  None,   # sera rempli par assign_sessions
                "open": o, "high": h, "low": l, "close": c,
                "volume": vol, "bid": bid, "ask": ask,
            })

    if timeframe_sec == 0:
        timeframe_sec = 60  # fallback 1m

    # Attribution des sessions par detection de gap
    rows = assign_sessions(raw_rows, cfg.gap_threshold)

    return rows, timeframe_sec


# =============================================================================
# KDE (pur Python)
# =============================================================================

def _local_peaks(densities: List[float]) -> List[int]:
    n = len(densities)
    if n < 3:
        return []
    return [
        i for i in range(1, n - 1)
        if densities[i] > densities[i - 1] and densities[i] >= densities[i + 1]
    ]


def gaussian_kde(
    prices: List[float],
    weights: List[float],
    bandwidth: float,
    grid_size: int,
) -> Tuple[List[float], List[float]]:
    p_min, p_max = min(prices), max(prices)
    if p_min == p_max:
        return [p_min], [1.0]

    step = (p_max - p_min) / (grid_size - 1)
    grid = [p_min + i * step for i in range(grid_size)]

    total_w = sum(weights) or 1.0
    norm_w = [w / total_w for w in weights]

    density = []
    for gp in grid:
        s = sum(
            w * math.exp(-0.5 * ((gp - px) / bandwidth) ** 2)
            for px, w in zip(prices, norm_w)
        )
        density.append(s)

    return grid, density


def kde_peaks(
    prices: List[float],
    weights: List[float],
    bandwidth: float,
    grid_size: int,
    max_niveaux: int = 20,
    min_density_pct: float = 10.0,
) -> List[float]:
    if not prices:
        return []
    grid, density = gaussian_kde(prices, weights, bandwidth, grid_size)
    idxs = _local_peaks(density)
    if not idxs:
        return []

    # Filtre 1 : densité minimum (% du pic le plus haut)
    max_density = max(density[i] for i in idxs)
    threshold   = max_density * (min_density_pct / 100.0)
    idxs = [i for i in idxs if density[i] >= threshold]

    # Filtre 2 : top N par densité décroissante
    idxs = sorted(idxs, key=lambda i: density[i], reverse=True)[:max_niveaux]

    return [grid[i] for i in idxs]


def compute_levels(rows: List[Dict], cfg: Config) -> Dict[str, List[float]]:
    bw = cfg.kde_bandwidth_ticks * cfg.tick_size

    highs = [r["high"]   for r in rows]
    lows  = [r["low"]    for r in rows]
    vols  = [r["volume"] for r in rows]
    bids  = [r["bid"]    for r in rows]
    asks  = [r["ask"]    for r in rows]

    kw = dict(grid_size=cfg.kde_grid_size,
              max_niveaux=cfg.max_niveaux,
              min_density_pct=cfg.min_density_pct)

    return {
        "volume": kde_peaks(highs + lows, vols + vols, bw, **kw),
        "bid":    kde_peaks(lows,         bids,         bw, **kw),
        "ask":    kde_peaks(highs,        asks,         bw, **kw),
    }


# =============================================================================
# CLUSTERING
# =============================================================================

def build_clusters(
    levels: Dict[str, List[float]],
    tick_size: float,
    zone_ticks: int,
) -> List[Dict]:
    gap = tick_size * zone_ticks

    all_pts: List[Tuple[float, str]] = [
        (float(lv), kind)
        for kind, arr in levels.items()
        for lv in arr
    ]
    if not all_pts:
        return []

    all_pts.sort(key=lambda x: x[0])

    clusters: List[Dict] = []
    cur_prices = [all_pts[0][0]]
    cur_types  = {all_pts[0][1]}

    for price, kind in all_pts[1:]:
        if price - cur_prices[-1] <= gap:
            cur_prices.append(price)
            cur_types.add(kind)
        else:
            clusters.append({
                "p_lo":    min(cur_prices),
                "p_hi":    max(cur_prices),
                "types":   frozenset(cur_types),
                "is_zone": len(cur_types) >= 2,
            })
            cur_prices = [price]
            cur_types  = {kind}

    clusters.append({
        "p_lo":    min(cur_prices),
        "p_hi":    max(cur_prices),
        "types":   frozenset(cur_types),
        "is_zone": len(cur_types) >= 2,
    })
    return clusters


# =============================================================================
# EXPORT niveaux.csv
# =============================================================================

def write_niveaux_csv(levels: Dict[str, List[float]], cfg: Config) -> Tuple[int, int]:
    clusters = build_clusters(levels, cfg.tick_size, cfg.zone_ticks)
    rows: List[List] = []
    n_zones = 0

    for i, cl in enumerate(clusters, start=1):
        p_lo, p_hi, types, is_zone = (
            cl["p_lo"], cl["p_hi"], cl["types"], cl["is_zone"]
        )
        if is_zone:
            n_zones += 1
            def _abbrev(t: str) -> str:
                return "VOL" if t == "volume" else t.upper()
            label = "+".join(_abbrev(t) for t in sorted(types))
            rows.append([
                round(p_lo, 4),
                round(p_hi, 4),
                f"Zone {label} #{i}",
                COLORS["zone"],
                0, 2, 1,
            ])
        else:
            kind = next(iter(types))
            rows.append([
                round(p_lo, 4),
                "",
                f"{'VOL' if kind == 'volume' else kind.upper()} N{i}",
                COLORS[kind],
                1, 2, 1,
            ])

    cfg.output_file.parent.mkdir(parents=True, exist_ok=True)
    with cfg.output_file.open("w", encoding="utf-8", newline="") as f:
        writer = csv.writer(f, delimiter=";")
        writer.writerow(["Price", "Price2", "Note", "Color",
                         "Line Type", "Line Width", "Text Alignment"])
        writer.writerows(rows)

    return len(rows), n_zones


# =============================================================================
# MAIN
# =============================================================================

def _tf_label(s: int) -> str:
    if s < 60:    return f"{s}s"
    if s < 3600:  return f"{s // 60}m"
    return f"{s // 3600}h"


def main() -> None:
    cfg = parse_args()

    if not cfg.input_file.exists():
        raise FileNotFoundError(f"Fichier introuvable : {cfg.input_file}")

    # 1. Chargement
    all_rows, tf_sec = load_chart_csv(cfg.input_file, cfg)
    if not all_rows:
        raise ValueError("Chart.csv est vide ou aucune ligne valide.")

    print(f"Timeframe detecte : {_tf_label(tf_sec)}")
    print(f"Detection pause   : gap > {cfg.gap_threshold} min (auto hiver/ete)")

    days = sorted({r["session"] for r in all_rows})
    print(f"Sessions CME      : {len(days)}  |  bougies : {len(all_rows):,}")

    if len(days) < cfg.k_days + 1:
        train_days = set(days)
        print(f"Attention : seulement {len(days)} session(s), utilisation de toutes les donnees.")
    else:
        train_days = set(days[-(cfg.k_days + 1):-1])
        last_day   = days[-1]
        print(f"Session cible    : {last_day}")
        print(f"Entrainement     : {min(train_days)} .. {max(train_days)}  ({len(train_days)} j)")

    train_rows = [r for r in all_rows if r["session"] in train_days]
    print(f"Bougies utilisees : {len(train_rows):,}")

    # 2. KDE
    levels = compute_levels(train_rows, cfg)
    for kind, arr in levels.items():
        if arr:
            print(f"  {kind:>6} : {len(arr):>3} niveaux  |"
                  f"  min={min(arr):.2f}  max={max(arr):.2f}")
        else:
            print(f"  {kind:>6} : 0 niveaux")

    # 3. Export
    n_rows, n_zones = write_niveaux_csv(levels, cfg)
    print(f"\nniveaux.csv : {cfg.output_file}  ({n_rows} entrees dont {n_zones} zones)")
    print("Termine.")


if __name__ == "__main__":
    main()
