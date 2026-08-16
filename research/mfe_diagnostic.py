#!/usr/bin/env python3
"""Phase 0b. The cheapest experiment in the project, and the only one that can
retire the ladder BEFORE it is written.

If the MFE distribution is unimodal, rungs strictly destroy expectancy: every rung
truncates a winner that would have run further, and the losers are full size either
way. If it is bimodal -- many small pokes plus a fat tail -- the rungs pay, because
the near rungs harvest the pokes and the runner keeps the tail.

This does NOT use MeanTick's entry signal. It characterises the TAPE in the window,
which is what the ladder decision actually depends on. A tape whose 09:30 excursions
are unimodal will not be rescued by a better entry.

Run: python3 research/mfe_diagnostic.py [--start YYYY-MM-DD] [--end YYYY-MM-DD]
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np

PROPSIM = Path(__file__).resolve().parents[2] / "PropSim"
sys.path.insert(0, str(PROPSIM))
import engine as eng  # noqa: E402
import tape as tp  # noqa: E402

CONTRACT = "ALL"                                     # the 275/238-session NQ stitch
STOP_POINTS = 10.0
WINDOW_ET = (9 * 3600 + 30 * 60, 11 * 3600)           # 09:30-11:00 ET, sec-of-day
R_EDGES = np.array([0, 0.5, 1, 1.5, 2, 3, 4, 5, 6, 8, 10, 999])
RATIO_LO, RATIO_HI = (1.0, 2.0), (3.0, 8.0)           # the brief's fallback bands
RATIO_THRESHOLD = 0.15


def hypothetical_mfe(ctx) -> tuple[np.ndarray, dict]:
    """MFE in R units for a hypothetical entry at each 1-minute bar's close,
    both directions, in WINDOW_ET, walked forward until a STOP_POINTS adverse
    move or the session ends -- whichever comes first.

    Deliberately NOT `engine.resolve()`. That function holds ONE position at a
    time (a `free_at` cooldown gate built for a real strategy that cannot be in
    two trades at once); these entries OVERLAP on purpose, one per minute, so
    reusing it would silently skip almost every entry while the previous one's
    walk was still in flight. Everything else is the house convention: the same
    prepared tape, the same day-boundary bound as `resolve()`
    (`searchsorted(day, day[i0], 'right')`), and the same "a hole in the tape
    is not a price move" gap guard (`engine.MAX_GAP_S`) -- just no slippage or
    commission, because this characterises the TAPE, not a strategy's costs
    (see docs/validation.md).
    """
    tape, bars, dayi = ctx["tape"], ctx["bars"], ctx["dayi"]
    px, ts = tape["px"], tape["ts"]
    bday = tp.day_index(bars["t"])
    sod = tp.sec_of_day(bars["t"])
    in_window = np.flatnonzero((sod >= WINDOW_ET[0]) & (sod < WINDOW_ET[1]))

    gap_limit = int(eng.MAX_GAP_S * tp.TPS)
    gap_starts = np.flatnonzero(np.diff(ts) > gap_limit) + 1   # first tick AFTER a hole

    r_values: list[float] = []
    censored = 0
    by_session: dict[int, list[float]] = {}
    n = len(px)
    for i in in_window:
        start = int(bars["end"][i])
        if start >= n:
            continue
        session_end = int(np.searchsorted(dayi, bday[i], "right"))
        if start >= session_end:
            continue
        # A hole inside this session bounds the walk the same way `resolve`
        # bounds a trade: stop at the last real tick before it.
        pos = np.searchsorted(gap_starts, start)
        g = int(gap_starts[pos]) if pos < len(gap_starts) else n
        seg = px[start:min(session_end, g)]
        if not len(seg):
            continue
        fill = float(bars["c"][i])
        for d in (1, -1):
            level = fill - d * STOP_POINTS
            crossed = (seg <= level) if d > 0 else (seg >= level)
            hit = np.flatnonzero(crossed)
            if len(hit):
                window_seg = seg[: hit[0] + 1]
            else:
                window_seg = seg
                censored += 1
            mfe_pts = (float(window_seg.max()) - fill) if d > 0 else (fill - float(window_seg.min()))
            r = max(mfe_pts, 0.0) / STOP_POINTS
            r_values.append(r)
            by_session.setdefault(int(bday[i]), []).append(r)

    meta = dict(total_walks=len(r_values), censored=censored,
                bars_in_window=len(in_window), by_session=by_session)
    return np.asarray(r_values), meta


def histogram(r: np.ndarray) -> list[tuple[str, int, float]]:
    counts, edges = np.histogram(r, bins=R_EDGES)
    rows = []
    for k in range(len(counts)):
        lo, hi = edges[k], edges[k + 1]
        label = f"{lo:g}-{hi:g}R" if hi < 999 else f">{lo:g}R"
        rows.append((label, int(counts[k]), counts[k] / len(r)))
    return rows


def print_histogram(rows, width=50):
    peak = max(c for _, c, _ in rows) or 1
    for label, count, frac in rows:
        bar = "#" * max(1, round(width * count / peak)) if count else ""
        print(f"  {label:>8} | {count:6d} ({frac*100:5.1f}%) {bar}")


def modality_verdict(r: np.ndarray) -> dict:
    """Hartigan's dip test if a package provides it; otherwise the band-ratio
    fallback the brief pre-authorises. SciPy (1.16.2, verified installed) does
    NOT ship a native dip test, and the only PyPI package for it (`diptest`)
    is not installed -- this workspace has no `pip`, only
    `uv pip install --target ~/.local/...`, which shadows apt-managed system
    packages on this machine (`.claude/memory/wsl-python-uv-target-installs.md`
    documents a real breakage from exactly this). Adding a dependency for one
    diagnostic script is not worth that risk, and the task brief itself offers
    this fallback for precisely this situation.
    """
    try:
        from diptest import diptest as _dip  # type: ignore
        stat, pval = _dip(r)
        return dict(method="hartigan_dip", stat=float(stat), pval=float(pval),
                     bimodal=pval < 0.05)
    except ImportError:
        pass
    lo_mask = (r >= RATIO_LO[0]) & (r < RATIO_LO[1])
    hi_mask = (r >= RATIO_HI[0]) & (r < RATIO_HI[1])
    lo_dens = lo_mask.mean() / (RATIO_LO[1] - RATIO_LO[0])
    hi_dens = hi_mask.mean() / (RATIO_HI[1] - RATIO_HI[0])
    ratio = hi_dens / lo_dens if lo_dens > 0 else float("inf")
    return dict(method="band_ratio", lo_density=lo_dens, hi_density=hi_dens,
                 ratio=ratio, threshold=RATIO_THRESHOLD, bimodal=ratio >= RATIO_THRESHOLD)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--start")
    ap.add_argument("--end")
    args = ap.parse_args()

    ctx = eng.prepare(CONTRACT, tf_secs=60, start=args.start, end=args.end, rth_only=True)
    print(f"tape: {CONTRACT} {ctx['start']}..{ctx['end']}, {ctx['days']} RTH sessions, "
          f"{len(ctx['tape']['ts']):,} ticks, {ctx['n_holes']} holes ({ctx['hole_days']})")

    r, meta = hypothetical_mfe(ctx)
    print(f"walks: {meta['total_walks']} ({meta['bars_in_window']} bars x2 directions), "
          f"censored (never hit the stop before session close): {meta['censored']} "
          f"({meta['censored']/meta['total_walks']*100:.1f}%)")
    print(f"median R = {np.median(r):.3f}   mean R = {r.mean():.3f}   max R = {r.max():.1f}")

    print("\nhistogram:")
    rows = histogram(r)
    print_histogram(rows)

    verdict = modality_verdict(r)
    print(f"\nmodality: {verdict}")

    # Tail concentration: is the fat tail broad-based (recurs most sessions) or
    # a handful of trend days carrying it (a few outliers, not a real feature)?
    n_sessions = len(meta["by_session"])
    print(f"\ntail concentration, {n_sessions} sessions with a window bar:")
    for thr in (2.0, 3.0, 5.0, 8.0, 10.0, 20.0, 50.0):
        hit = sum(1 for vals in meta["by_session"].values() if max(vals) >= thr)
        print(f"  sessions with >=1 walk reaching {thr:>4g}R: {hit:4d} ({hit/n_sessions*100:5.1f}%)")

    # Density vs. shape: the ratio test answers "is there enough tail mass to
    # matter", not "are there two humps with a trough between". Report the
    # per-R density explicitly so a reader can see which claim the numbers
    # support -- see docs/validation.md.
    print("\ndensity per R-unit (count / bin width, NOT raw counts -- this is what")
    print("'unimodal vs bimodal' actually means: does it fall monotonically from 0,")
    print("or does it dip and rise again):")
    for label, count, frac in rows:
        lo, hi = label.replace("R", "").replace(">", "").split("-") if "-" in label else (label.replace(">", "").replace("R", ""), None)
        width = (float(hi) - float(lo)) if hi else None
        dens = f"{frac*100/width:6.1f}%/R" if width else "  (open bin)"
        print(f"  {label:>8} | {dens}")


if __name__ == "__main__":
    main()
