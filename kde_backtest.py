"""
kde_backtest.py
===============================================================================
Backtest rolling des niveaux KDE sur Chart.csv (ATAS, UTC+1)
Version : 1.1.0

Pour chaque session de test (a partir du jour k_days+1) :
  - KDE entraine sur les k_days sessions precedentes
  - Bougie haussiere (C >= O) : on teste si le HIGH touche un niveau a +/- eps
  - Bougie baissiere (C <  O) : on teste si le LOW  touche un niveau a +/- eps

Comparaison avec le meme nombre de niveaux aleatoires (Monte-Carlo ou analytique).

Usage :
  python kde_backtest.py [--input Chart.csv]
                         [--k-days 3]
                         [--tick-size 0.25]
                         [--eps-ticks 2]
                         [--kde-bandwidth-ticks 60]
                         [--zone-ticks 30]
                         [--n-trials 500]
                         [--pause-start 21]
                         [--session-start 22]
                         [--out-csv backtest_sessions.csv]
===============================================================================
"""

from __future__ import annotations

import argparse
import csv
import math
import random
import sys
from dataclasses import dataclass
from datetime import datetime, date, timedelta
from pathlib import Path
from typing import Dict, List, Optional, Tuple

# numpy optionnel (Monte-Carlo vectorise si disponible)
try:
    import numpy as np
    HAS_NUMPY = True
except ImportError:
    HAS_NUMPY = False

DATA_DIR = Path(r"C:\Users\phili\OneDrive\Traiding_PL\Codes\ATAS_lecture_csv")

# =============================================================================
# CONFIG
# =============================================================================

@dataclass
class Config:
    input_file:           Path  = DATA_DIR / "Chart.csv"
    out_csv:              Path  = DATA_DIR / "backtest_sessions.csv"
    k_days:               int   = 3
    tick_size:            float = 0.25
    eps_ticks:            float = 2.0      # tolerance +/- en ticks
    kde_bandwidth_ticks:  float = 60.0
    kde_grid_size:        int   = 1000
    zone_ticks:           int   = 30
    max_niveaux:          int   = 20
    min_density_pct:      float = 10.0
    n_trials:             int   = 500      # tirages Monte-Carlo


# =============================================================================
# CLI
# =============================================================================

def parse_args() -> Config:
    p = argparse.ArgumentParser()
    p.add_argument("--input",               type=Path,  default=None)
    p.add_argument("--out-csv",             type=Path,  default=None)
    p.add_argument("--k-days",              type=int,   default=3)
    p.add_argument("--tick-size",           type=float, default=0.25)
    p.add_argument("--eps-ticks",           type=float, default=2.0)
    p.add_argument("--kde-bandwidth-ticks", type=float, default=60.0)
    p.add_argument("--kde-grid-size",       type=int,   default=1000)
    p.add_argument("--zone-ticks",          type=int,   default=30)
    p.add_argument("--max-niveaux",         type=int,   default=20)
    p.add_argument("--min-density-pct",     type=float, default=10.0)
    p.add_argument("--n-trials",            type=int,   default=500)
    a = p.parse_args()
    cfg = Config()
    if a.input:    cfg.input_file          = a.input
    if a.out_csv:  cfg.out_csv             = a.out_csv
    cfg.k_days              = a.k_days
    cfg.tick_size           = a.tick_size
    cfg.eps_ticks           = a.eps_ticks
    cfg.kde_bandwidth_ticks = a.kde_bandwidth_ticks
    cfg.kde_grid_size       = a.kde_grid_size
    cfg.zone_ticks          = a.zone_ticks
    cfg.max_niveaux         = a.max_niveaux
    cfg.min_density_pct     = a.min_density_pct
    cfg.n_trials            = a.n_trials
    return cfg


# =============================================================================
# CHARGEMENT CSV + DETECTION SESSIONS PAR GAP
# =============================================================================

GAP_THRESHOLD = timedelta(minutes=45)   # pause CME = gap > 45 min


def _parse_dt(s: str) -> Optional[datetime]:
    for fmt in ("%d/%m/%Y %H:%M:%S", "%d/%m/%Y %H:%M",
                "%Y-%d-%m %H:%M:%S", "%Y-%m-%d %H:%M:%S",
                "%Y-%d-%m %H:%M",    "%Y-%m-%d %H:%M"):
        try:
            return datetime.strptime(s.strip(), fmt)
        except ValueError:
            pass
    return None


def _flt(s: str) -> Optional[float]:
    try:
        return float(s.strip().replace(",", "."))
    except ValueError:
        return None


def load_csv(path: Path, cfg: Config) -> Dict[date, List[dict]]:
    """
    Retourne un dict {session_date: [candles]}.

    Detection des sessions par gap :
      Un ecart > 45 min entre deux bougies = pause CME.
      session_date = date(premiere_bougie_apres_gap) + 1 jour.
      Fonctionne en hiver (UTC+1) ET en ete (UTC+2), sans parametre fixe.
    """
    raw_rows: List[dict] = []

    with path.open("r", encoding="utf-8-sig", newline="") as f:
        sample = f.read(4096); f.seek(0)
        sep = ";" if sample.count(";") >= sample.count(",") else ","
        reader = csv.reader(f, delimiter=sep)

        for raw in reader:
            if not raw or not raw[0].strip(): continue
            first = raw[0].strip()
            if first.startswith("#"): continue
            if first.lower() in ("datetime", "date", "date time"): continue
            if len(raw) < 8: continue

            dt = _parse_dt(first)
            if dt is None: continue

            vals = [_flt(raw[i]) for i in range(1, 8)]
            if any(v is None for v in vals): continue

            o, h, l, c, vol, bid, ask = vals
            raw_rows.append(dict(dt=dt, open=o, high=h, low=l, close=c,
                                 volume=vol, bid=bid, ask=ask))

    # Attribution des sessions par detection de gap
    sessions: Dict[date, List[dict]] = {}
    current_session: Optional[date] = None

    for i, row in enumerate(raw_rows):
        if i == 0 or (row["dt"] - raw_rows[i-1]["dt"]) > GAP_THRESHOLD:
            current_session = (row["dt"] + timedelta(days=1)).date()
        sessions.setdefault(current_session, []).append(row)

    return sessions


# =============================================================================
# KDE
# =============================================================================

def _local_peaks(density: List[float]) -> List[int]:
    return [i for i in range(1, len(density)-1)
            if density[i] > density[i-1] and density[i] >= density[i+1]]


def _gaussian_kde(prices: List[float], weights: List[float],
                  bw: float, grid_size: int) -> Tuple[List[float], List[float]]:
    pmin, pmax = min(prices), max(prices)
    if pmin == pmax:
        return [pmin], [1.0]
    step = (pmax - pmin) / (grid_size - 1)
    grid = [pmin + i * step for i in range(grid_size)]
    tw = sum(weights) or 1.0
    nw = [w / tw for w in weights]
    density = [sum(w * math.exp(-0.5 * ((gp - px) / bw)**2)
                   for px, w in zip(prices, nw))
               for gp in grid]
    return grid, density


def kde_peaks(prices, weights, bw,
              grid_size: int = 1000,
              max_niveaux: int = 20,
              min_density_pct: float = 10.0) -> List[float]:
    if not prices:
        return []
    grid, density = _gaussian_kde(prices, weights, bw, grid_size)
    idxs = _local_peaks(density)
    if not idxs:
        return []
    # Filtre 1 : densité minimum
    max_d = max(density[i] for i in idxs)
    threshold = max_d * (min_density_pct / 100.0)
    idxs = [i for i in idxs if density[i] >= threshold]
    # Filtre 2 : top N par densité
    idxs = sorted(idxs, key=lambda i: density[i], reverse=True)[:max_niveaux]
    return [grid[i] for i in idxs]


def compute_kde_levels(candles: List[dict], cfg: Config
                       ) -> List[Tuple[float, float, str]]:
    """
    Retourne une liste de (price_lo, price_hi, kind).
    kind in {bid, ask, volume, zone}.
    price_lo == price_hi pour les lignes simples.
    """
    bw = cfg.kde_bandwidth_ticks * cfg.tick_size

    highs = [c["high"]   for c in candles]
    lows  = [c["low"]    for c in candles]
    vols  = [c["volume"] for c in candles]
    bids  = [c["bid"]    for c in candles]
    asks  = [c["ask"]    for c in candles]

    kw = dict(grid_size=cfg.kde_grid_size,
              max_niveaux=cfg.max_niveaux,
              min_density_pct=cfg.min_density_pct)

    raw: Dict[str, List[float]] = {
        "volume": kde_peaks(highs + lows, vols + vols, bw, **kw),
        "bid":    kde_peaks(lows,         bids,         bw, **kw),
        "ask":    kde_peaks(highs,        asks,         bw, **kw),
    }

    # Clustering
    gap = cfg.tick_size * cfg.zone_ticks
    all_pts = sorted(
        [(p, k) for k, arr in raw.items() for p in arr],
        key=lambda x: x[0]
    )
    if not all_pts:
        return []

    clusters: List[Tuple[float, float, str]] = []
    cp = [all_pts[0][0]];  ct = {all_pts[0][1]}

    for price, kind in all_pts[1:]:
        if price - cp[-1] <= gap:
            cp.append(price); ct.add(kind)
        else:
            kinds = "+".join(sorted(ct))
            clusters.append((min(cp), max(cp),
                             "zone" if len(ct) >= 2 else next(iter(ct))))
            cp = [price]; ct = {kind}

    kinds = "+".join(sorted(ct))
    clusters.append((min(cp), max(cp),
                    "zone" if len(ct) >= 2 else next(iter(ct))))
    return clusters


# =============================================================================
# TEST DE HIT
# =============================================================================

def touches(test_price: float,
            levels: List[Tuple[float, float, str]],
            eps: float) -> bool:
    """True si test_price est a moins de eps d'un niveau (ou dans une zone)."""
    for lo, hi, _ in levels:
        if lo - eps <= test_price <= hi + eps:
            return True
    return False


def touches_kind(test_price: float,
                 levels: List[Tuple[float, float, str]],
                 eps: float) -> Dict[str, bool]:
    """Hit par type de niveau."""
    hits = {k: False for k in ("bid", "ask", "volume", "zone")}
    for lo, hi, kind in levels:
        if lo - eps <= test_price <= hi + eps:
            for k in kind.split("+"):
                if k in hits:
                    hits[k] = True
    return hits


# =============================================================================
# HIT RATE ALEATOIRE (Monte-Carlo vectorise si numpy disponible)
# =============================================================================

def random_hit_rate(test_prices: List[float],
                    price_min: float, price_max: float,
                    n_levels: int, eps: float,
                    n_trials: int) -> float:
    """
    Taux de hit moyen pour n_levels niveaux aleatoires dans [price_min, price_max].
    """
    if n_levels == 0 or price_max <= price_min or not test_prices:
        return 0.0

    pr = price_max - price_min

    if HAS_NUMPY:
        rand_lvl = np.random.uniform(price_min, price_max, (n_trials, n_levels))
        tp = np.array(test_prices)                              # (n_candles,)
        diffs = np.abs(tp[:, None, None] - rand_lvl[None, :, :])  # (C, T, L)
        hit = np.any(diffs <= eps, axis=2)                      # (C, T)
        return float(hit.mean())
    else:
        # Approximation analytique : P(aucun niveau ne touche) = ((pr-2eps)/pr)^n
        p_no_hit = max(0.0, (pr - 2 * eps) / pr) ** n_levels
        return 1.0 - p_no_hit


# =============================================================================
# BACKTEST PRINCIPAL
# =============================================================================

@dataclass
class SessionResult:
    session:       date
    n_bull:        int    # bougies haussières
    n_bear:        int    # bougies baissières
    hit_bull_kde:  int    # hits H sur KDE
    hit_bear_kde:  int    # hits L sur KDE
    hit_bull_rnd:  float  # hit rate aléatoire (bull)
    hit_bear_rnd:  float  # hit rate aléatoire (bear)
    n_levels:      int    # nb niveaux KDE
    # par type
    hit_bull_bid:  int = 0
    hit_bull_ask:  int = 0
    hit_bull_vol:  int = 0
    hit_bull_zone: int = 0
    hit_bear_bid:  int = 0
    hit_bear_ask:  int = 0
    hit_bear_vol:  int = 0
    hit_bear_zone: int = 0


def run_backtest(sessions_dict: Dict[date, List[dict]],
                 cfg: Config) -> List[SessionResult]:

    eps = cfg.eps_ticks * cfg.tick_size
    sorted_days = sorted(sessions_dict.keys())

    if len(sorted_days) < cfg.k_days + 1:
        print(f"Pas assez de sessions ({len(sorted_days)} < {cfg.k_days + 1})")
        return []

    results: List[SessionResult] = []

    for i, test_day in enumerate(sorted_days):
        if i < cfg.k_days:
            continue   # pas assez d'historique

        # Fenetre d'entrainement
        train_days = sorted_days[i - cfg.k_days : i]
        train_candles = [c for d in train_days for c in sessions_dict[d]]
        if not train_candles:
            continue

        # Niveaux KDE
        levels = compute_kde_levels(train_candles, cfg)
        if not levels:
            continue

        # Plage de prix pour l'aleatoire
        price_min = min(c["low"]  for c in train_candles)
        price_max = max(c["high"] for c in train_candles)

        # Bougies de test
        test_candles = sessions_dict[test_day]

        bull_prices: List[float] = []
        bear_prices: List[float] = []
        for c in test_candles:
            if c["close"] >= c["open"]:
                bull_prices.append(c["high"])
            else:
                bear_prices.append(c["low"])

        # Hits KDE
        hb_kde = sum(1 for p in bull_prices if touches(p, levels, eps))
        hd_kde = sum(1 for p in bear_prices if touches(p, levels, eps))

        # Hits par type
        hb_type = {k: 0 for k in ("bid", "ask", "volume", "zone")}
        hd_type = {k: 0 for k in ("bid", "ask", "volume", "zone")}
        for p in bull_prices:
            for k, v in touches_kind(p, levels, eps).items():
                if v: hb_type[k] += 1
        for p in bear_prices:
            for k, v in touches_kind(p, levels, eps).items():
                if v: hd_type[k] += 1

        # Hit rate aleatoire
        n_lvl = len(levels)
        rnd_bull = random_hit_rate(bull_prices, price_min, price_max,
                                   n_lvl, eps, cfg.n_trials)
        rnd_bear = random_hit_rate(bear_prices, price_min, price_max,
                                   n_lvl, eps, cfg.n_trials)

        results.append(SessionResult(
            session      = test_day,
            n_bull       = len(bull_prices),
            n_bear       = len(bear_prices),
            hit_bull_kde = hb_kde,
            hit_bear_kde = hd_kde,
            hit_bull_rnd = rnd_bull,
            hit_bear_rnd = rnd_bear,
            n_levels     = n_lvl,
            hit_bull_bid  = hb_type["bid"],
            hit_bull_ask  = hb_type["ask"],
            hit_bull_vol  = hb_type["volume"],
            hit_bull_zone = hb_type["zone"],
            hit_bear_bid  = hd_type["bid"],
            hit_bear_ask  = hd_type["ask"],
            hit_bear_vol  = hd_type["volume"],
            hit_bear_zone = hd_type["zone"],
        ))

        # Progression
        pct = (i + 1) / len(sorted_days) * 100
        print(f"\r  {pct:5.1f}%  session {test_day}  "
              f"niveaux={n_lvl}  "
              f"bull={hb_kde}/{len(bull_prices)}  "
              f"bear={hd_kde}/{len(bear_prices)}  ",
              end="", flush=True)

    print()
    return results


# =============================================================================
# STATISTIQUES
# =============================================================================

def z_score(k: int, n: int, p0: float) -> float:
    if n == 0 or p0 <= 0 or p0 >= 1:
        return float("nan")
    return (k / n - p0) / math.sqrt(p0 * (1 - p0) / n)


def p_value_approx(z: float) -> str:
    """Approximation normale."""
    if math.isnan(z):
        return "n/a"
    az = abs(z)
    if az > 4.4: return "< 0.00001"
    if az > 3.7: return "< 0.0001"
    if az > 3.1: return "< 0.001"
    if az > 2.6: return "< 0.005"
    if az > 2.0: return "< 0.05"
    if az > 1.6: return "< 0.10"
    return ">= 0.10 (non significatif)"


def print_stats(results: List[SessionResult], cfg: Config) -> None:
    eps = cfg.eps_ticks * cfg.tick_size

    total_bull = sum(r.n_bull       for r in results)
    total_bear = sum(r.n_bear       for r in results)
    hb_kde     = sum(r.hit_bull_kde for r in results)
    hd_kde     = sum(r.hit_bear_kde for r in results)

    rnd_bull_rate = (sum(r.hit_bull_rnd * r.n_bull for r in results)
                     / max(1, total_bull))
    rnd_bear_rate = (sum(r.hit_bear_rnd * r.n_bear for r in results)
                     / max(1, total_bear))

    rnd_bull_abs = rnd_bull_rate * total_bull
    rnd_bear_abs = rnd_bear_rate * total_bear

    rate_b = hb_kde / max(1, total_bull)
    rate_d = hd_kde / max(1, total_bear)
    lift_b = rate_b / max(1e-9, rnd_bull_rate)
    lift_d = rate_d / max(1e-9, rnd_bear_rate)
    zb = z_score(hb_kde, total_bull, rnd_bull_rate)
    zd = z_score(hd_kde, total_bear, rnd_bear_rate)

    avg_levels = sum(r.n_levels for r in results) / max(1, len(results))

    W = 60
    print("\n" + "=" * W)
    print("  BACKTEST KDE NIVEAUX")
    print("=" * W)
    print(f"  Fichier     : {cfg.input_file.name}")
    print(f"  Sessions    : {len(results)}  (de {results[0].session} a {results[-1].session})")
    print(f"  k-days      : {cfg.k_days}")
    print(f"  Eps         : ±{cfg.eps_ticks} ticks  (±{eps:.4f})")
    print(f"  Niveaux KDE : {avg_levels:.1f} en moyenne / session")
    mc = f"Monte-Carlo {cfg.n_trials} tirages" if HAS_NUMPY else "analytique"
    print(f"  Reference   : aleatoire ({mc})")
    print("-" * W)

    def row(label, kde, rnd, n, lift, z):
        pct_kde = 100 * kde / max(1, n)
        pct_rnd = 100 * rnd / max(1, n)
        print(f"  {label:<36} {kde:>6} / {n:<6} = {pct_kde:5.1f}%")
        print(f"    Aleatoire attendu           {rnd:>8.0f}              {pct_rnd:5.1f}%")
        print(f"    Lift                                               {lift:5.2f}x")
        print(f"    Z-score                                            {z:+.1f}")
        print(f"    p-value (unilateral)                 {p_value_approx(z)}")

    print("\n--- BOUGIES HAUSSIÈRES : H touche un niveau KDE ---")
    row("Hits", hb_kde, rnd_bull_abs, total_bull, lift_b, zb)

    print("\n--- BOUGIES BAISSIÈRES : L touche un niveau KDE ---")
    row("Hits", hd_kde, rnd_bear_abs, total_bear, lift_d, zd)

    # Par type de niveau
    print("\n--- DÉTAIL PAR TYPE DE NIVEAU ---")
    header = f"  {'Type':<8} {'Bull hits':>10} {'Bull %':>7} {'Bear hits':>10} {'Bear %':>7}"
    print(header)
    print("  " + "-" * (len(header) - 2))
    kind_attr = {"bid": "bid", "ask": "ask", "volume": "vol", "zone": "zone"}
    for kind, attr in kind_attr.items():
        bk = sum(getattr(r, f"hit_bull_{attr}") for r in results)
        dk = sum(getattr(r, f"hit_bear_{attr}") for r in results)
        print(f"  {kind:<8} {bk:>10} {100*bk/max(1,total_bull):>6.1f}%"
              f" {dk:>10} {100*dk/max(1,total_bear):>6.1f}%")

    print("=" * W)


# =============================================================================
# EXPORT CSV PAR SESSION
# =============================================================================

def save_sessions_csv(results: List[SessionResult], path: Path) -> None:
    with path.open("w", encoding="utf-8", newline="") as f:
        w = csv.writer(f, delimiter=";")
        w.writerow([
            "Session", "N_Bull", "N_Bear",
            "Hit_Bull_KDE", "Hit_Bear_KDE",
            "Rate_Bull_KDE%", "Rate_Bear_KDE%",
            "Rate_Bull_Rnd%", "Rate_Bear_Rnd%",
            "Lift_Bull", "Lift_Bear",
            "N_Levels",
            "Hit_Bull_Bid", "Hit_Bull_Ask", "Hit_Bull_Vol", "Hit_Bull_Zone",
            "Hit_Bear_Bid", "Hit_Bear_Ask", "Hit_Bear_Vol", "Hit_Bear_Zone",
        ])
        for r in results:
            rb = r.hit_bull_rnd
            rd = r.hit_bear_rnd
            w.writerow([
                r.session,
                r.n_bull, r.n_bear,
                r.hit_bull_kde, r.hit_bear_kde,
                f"{100*r.hit_bull_kde/max(1,r.n_bull):.1f}",
                f"{100*r.hit_bear_kde/max(1,r.n_bear):.1f}",
                f"{100*rb:.1f}", f"{100*rd:.1f}",
                f"{(r.hit_bull_kde/max(1,r.n_bull))/max(1e-9,rb):.2f}",
                f"{(r.hit_bear_kde/max(1,r.n_bear))/max(1e-9,rd):.2f}",
                r.n_levels,
                r.hit_bull_bid, r.hit_bull_ask, r.hit_bull_vol, r.hit_bull_zone,
                r.hit_bear_bid, r.hit_bear_ask, r.hit_bear_vol, r.hit_bear_zone,
            ])
    print(f"\n  CSV sessions : {path}")


# =============================================================================
# MAIN
# =============================================================================

def main() -> None:
    cfg = parse_args()

    if not cfg.input_file.exists():
        sys.exit(f"Fichier introuvable : {cfg.input_file}")

    print(f"Chargement : {cfg.input_file} ...")
    sessions = load_csv(cfg.input_file, cfg)
    n_sess = len(sessions)
    n_candles = sum(len(v) for v in sessions.values())
    print(f"  {n_sess} sessions CME  |  {n_candles:,} bougies")

    if n_sess < cfg.k_days + 1:
        sys.exit(f"Pas assez de sessions pour k-days={cfg.k_days}")

    print(f"\nBacktest rolling (k-days={cfg.k_days}, eps=±{cfg.eps_ticks} ticks) ...")
    if not HAS_NUMPY:
        print("  [numpy absent — comparaison aleatoire analytique]")

    results = run_backtest(sessions, cfg)

    if not results:
        sys.exit("Aucun resultat.")

    print_stats(results, cfg)
    save_sessions_csv(results, cfg.out_csv)


if __name__ == "__main__":
    main()
