"""
compare_kde.py  -  Comparaison niveaux KDE : C# (ATAS) vs Python
=================================================================
Version : 1.0.0
Auteur  : Philippe L.

Lit :
  niveaux_cs.csv   -> export du C# (KdeNiveauxAuto, renommer niveaux.csv ici)
  niveaux_py.csv   -> export Python kde_niveaux.py avec --output niveaux_py.csv

Compare les prix (colonne Price) et les labels (Note) entre les deux sources.

Usage :
  # 1. Lancer KdeNiveauxAuto dans ATAS -> copier niveaux.csv -> niveaux_cs.csv
  # 2. python kde_niveaux.py --output niveaux_py.csv
  # 3. python compare_kde.py [--cs niveaux_cs.csv] [--py niveaux_py.csv] [--tol 1]

Options :
  --cs   <fichier>   Fichier C#     (defaut : niveaux_cs.csv dans le dossier data)
  --py   <fichier>   Fichier Python (defaut : niveaux_py.csv dans le dossier data)
  --tol  <ticks>     Tolerance d'ecart de prix en ticks (defaut : 2)
  --tick <float>     Taille d'un tick (defaut : 0.25)
"""

import argparse
import csv
from pathlib import Path

# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------
DATA_DIR = Path(__file__).parent

TICK_SIZE_DEFAULT = 0.25
TOL_TICKS_DEFAULT = 2


# ---------------------------------------------------------------------------
# Lecture CSV
# ---------------------------------------------------------------------------

def read_niveaux(path: Path) -> list[dict]:
    rows = []
    with open(path, encoding="utf-8-sig") as f:  # utf-8-sig gere le BOM eventuel
        reader = csv.DictReader(f, delimiter=";")
        for row in reader:
            try:
                price = float(row["Price"])
                price2_str = row.get("Price2", "").strip()
                price2 = float(price2_str) if price2_str else None
                note = row.get("Note", "").strip()
                rows.append({"price": price, "price2": price2, "note": note})
            except (ValueError, KeyError):
                continue
    return rows


# ---------------------------------------------------------------------------
# Comparaison
# ---------------------------------------------------------------------------

def compare(cs_rows: list[dict], py_rows: list[dict],
            tol_ticks: int, tick_size: float) -> None:

    tol_price = tol_ticks * tick_size

    print(f"\n{'='*60}")
    print(f"  Comparaison KDE  C# ({len(cs_rows)} niveaux) vs Python ({len(py_rows)} niveaux)")
    print(f"  Tolerance : ±{tol_ticks} ticks ({tol_price:.4f} pts)")
    print(f"{'='*60}\n")

    # -- Niveaux C# matches dans Python
    cs_matched = []
    cs_unmatched = []
    match_details = []

    for cs in cs_rows:
        best = None
        best_dist = float("inf")
        for py in py_rows:
            dist = abs(cs["price"] - py["price"])
            if dist < best_dist:
                best_dist = dist
                best = py
        if best_dist <= tol_price:
            cs_matched.append(cs)
            match_details.append((cs, best, best_dist))
        else:
            cs_unmatched.append(cs)

    # -- Niveaux Python sans correspondance C#
    py_unmatched = []
    for py in py_rows:
        found = any(abs(py["price"] - cs["price"]) <= tol_price for cs in cs_rows)
        if not found:
            py_unmatched.append(py)

    # -- Rapport
    pct = 100 * len(cs_matched) / len(cs_rows) if cs_rows else 0
    print(f"Correspondances C# -> Python : {len(cs_matched)}/{len(cs_rows)}  ({pct:.0f}%)")
    print()

    if match_details:
        print("MATCHES (C# prix -> Python prix  |  ecart  |  labels)")
        print("-" * 60)
        for cs, py, dist in sorted(match_details, key=lambda x: x[0]["price"], reverse=True):
            tick_dist = dist / tick_size
            same_label = "OK" if cs["note"] == py["note"] else f"DIFF py={py['note']}"
            print(f"  {cs['price']:>10.4f} -> {py['price']:>10.4f}  "
                  f"|  {tick_dist:+.1f} ticks  |  cs={cs['note']}  {same_label}")

    if cs_unmatched:
        print(f"\nNIVEAUX C# sans correspondance Python ({len(cs_unmatched)}) :")
        print("-" * 60)
        for r in sorted(cs_unmatched, key=lambda x: x["price"], reverse=True):
            print(f"  {r['price']:>10.4f}   {r['note']}")

    if py_unmatched:
        print(f"\nNIVEAUX Python sans correspondance C# ({len(py_unmatched)}) :")
        print("-" * 60)
        for r in sorted(py_unmatched, key=lambda x: x["price"], reverse=True):
            print(f"  {r['price']:>10.4f}   {r['note']}")

    # -- Statistiques labels
    label_ok = sum(1 for cs, py, _ in match_details if cs["note"] == py["note"])
    if match_details:
        pct_lbl = 100 * label_ok / len(match_details)
        print(f"\nCoherence labels (matches) : {label_ok}/{len(match_details)}  ({pct_lbl:.0f}%)")

    print(f"\n{'='*60}")
    if not cs_unmatched and not py_unmatched:
        print("  RESULTAT : COHERENT - tous les niveaux se correspondent")
    else:
        total_diff = len(cs_unmatched) + len(py_unmatched)
        print(f"  RESULTAT : {total_diff} ecart(s) detecte(s)")
    print(f"{'='*60}\n")


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    p = argparse.ArgumentParser(description="Comparaison niveaux KDE C# vs Python")
    p.add_argument("--cs",   type=Path, default=DATA_DIR / "niveaux_cs.csv",
                   help="Fichier export C# (defaut : niveaux_cs.csv)")
    p.add_argument("--py",   type=Path, default=DATA_DIR / "niveaux_py.csv",
                   help="Fichier export Python (defaut : niveaux_py.csv)")
    p.add_argument("--tol",  type=int,  default=TOL_TICKS_DEFAULT,
                   help="Tolerance en ticks (defaut : 2)")
    p.add_argument("--tick", type=float, default=TICK_SIZE_DEFAULT,
                   help="Taille d'un tick (defaut : 0.25)")
    args = p.parse_args()

    for f, label in [(args.cs, "C#"), (args.py, "Python")]:
        if not f.exists():
            print(f"ERREUR : fichier {label} introuvable : {f}")
            print(f"  -> Renommer niveaux.csv en niveaux_cs.csv pour le C#")
            print(f"  -> Lancer : python kde_niveaux.py --output niveaux_py.csv")
            return

    cs_rows = read_niveaux(args.cs)
    py_rows = read_niveaux(args.py)

    if not cs_rows:
        print(f"ERREUR : aucune ligne lue depuis {args.cs}")
        return
    if not py_rows:
        print(f"ERREUR : aucune ligne lue depuis {args.py}")
        return

    compare(cs_rows, py_rows, args.tol, args.tick)


if __name__ == "__main__":
    main()
