#!/usr/bin/env python3
"""Phase 0c. Decides whether amendment A1 (spec 4.6) ships ON, OFF or inverted.

For every 09:30-11:00 candidate level, measure the signed distance to the
NEXT nearest array of the same direction. Histogram against the 10-point
stop.

If the median gap is under ~10 points, WHICH array is chosen sits inside the
noise of the stop, and no backtest of 'the 50% of the chosen array' can
distinguish the model from tape-fitting -- which is the failure the source
diagnosed on his own money (V6, -$980, 'oh man, I picked the wrong order
block').

The inverse reading is equally testable and is his own claim elsewhere: a
coincident level is 'a double level... a lot more confluence' (V3 [4:22]).
Report BOTH -- the win rate of clustered candidates versus isolated ones --
and let the numbers pick.

THE APPROXIMATION, stated once here rather than at every callsite: MeanTick's
real Gate 1 (design.md S4.1) admits three 4H array types -- fair value gap,
order block (itself anchored to an FVG) and rejection block. The C# detector
for the last two does not exist yet (Task 9 ports the Python mirror). This
script uses ONLY the 4H fair value gap -- PropSim's own `fvg` strategy
(engine.py, class FVG, currently around lines 511-591; re-verify before
trusting the line numbers, they drift) -- as a proxy for "a PD array formed
here". That understates array density: order blocks and rejection blocks are
MORE candidates, not fewer, so a real multi-type median would be TIGHTER
(more crowded) than what this script reports. Every number below is an
OPTIMISTIC bound on clustering, i.e. a pessimistic bound on how safe it is to
ignore A1.

Run: python3 research/cluster_histogram.py [--start YYYY-MM-DD] [--end YYYY-MM-DD]
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

CONTRACT = "ALL"
TF_4H = 4 * 3600
STOP_POINTS = 10.0
CLUSTER_SKIP_POINTS = STOP_POINTS         # A1's own default, design.md S4.6
FRESH_BARS_4H = 5                         # design.md S4.1's own freshness window
SINGLE_TARGET_R = 4.0                     # design.md S5.1, the control arm
WINDOW_ET = (9 * 3600 + 30 * 60, 11 * 3600)
TRAIN_FRACTION = 0.60                     # brief S4: 60/40 train/test split
HOUSE_MIN_TRADES = 100                    # strategy-profitability-gates.md


def detect_4h_arrays(bars, tick: float, min_ticks: float = 8.0):
    """Every qualifying 3-bar FVG on `bars`, as (bar_idx, direction, level).

    Mirrors `engine.FVG.entries()`'s own gap-detection arrays (`bull_gap`,
    `bear_gap`) verbatim -- same formula, same threshold -- because that is
    the tested, house definition of a fair value gap and reinventing it would
    risk a second, silently different one. What differs: `FVG.entries()` also
    scans forward for a retrace FILL and returns only levels price actually
    revisited, which is the wrong population for a DENSITY question (an
    unfilled array is still an array a real trade could have been routed
    to). `level` here is the 50% consequent encroachment (design.md S4.1's
    own definition for the real detector), not `FVG.entries()`'s near-edge
    retrace price.
    """
    h, l = bars["h"].astype(np.float64), bars["l"].astype(np.float64)
    n = len(h)
    if n < 3:
        return (np.array([], int), np.array([], np.int8), np.array([]))
    gap = min_ticks * tick
    bull_gap = l[2:] - h[:-2]
    bear_gap = l[:-2] - h[2:]
    cand = np.flatnonzero((bull_gap >= gap) | (bear_gap >= gap))
    bar_idx, direction, level = [], [], []
    for k in cand:
        i = k + 2
        if bull_gap[k] >= gap:
            bar_idx.append(i); direction.append(1); level.append((h[i - 2] + l[i]) / 2.0)
        else:
            bar_idx.append(i); direction.append(-1); level.append((h[i] + l[i - 2]) / 2.0)
    return (np.array(bar_idx), np.array(direction, np.int8), np.array(level))


def first_retrace_fill(bars, tape, bar_idx: int, direction: int, level: float,
                        expiry_bars: int) -> int | None:
    """The first tick, after `bar_idx` closes, at which price re-enters the
    gap -- i.e. the tick MeanTick's resting limit at this array's level would
    have filled. Mirrors `FVG.entries()`'s retrace scan, minus its day-based
    expiry clamp: this runs on the FULL (non-RTH) tape, which has no RTH
    session gaps for that clamp to guard against.
    """
    px = tape["px"]
    a = int(bars["end"][bar_idx])
    b = int(bars["end"][min(bar_idx + expiry_bars, len(bars["end"]) - 1)])
    if b <= a:
        return None
    seg = px[a:b]
    hit = np.flatnonzero(seg <= level) if direction > 0 else np.flatnonzero(seg >= level)
    return a + int(hit[0]) if len(hit) else None


def nearest_neighbor_gap(bar_idx: np.ndarray, direction: np.ndarray, level: np.ndarray,
                          fresh_bars: int = FRESH_BARS_4H) -> np.ndarray:
    """For each array, the absolute price distance to the nearest OTHER
    array of the same direction formed within `fresh_bars` of it -- i.e. the
    other array that would ALSO still be "fresh" (design.md S4.1) at the same
    moment this one is. `nan` where no such neighbor exists.

    O(n^2) over the candidate list, deliberately: the whole tape's 4H FVG
    count is in the hundreds (measured), and a spatial index would be a
    second thing to get right for no measurable speed gain at this size.
    """
    out = np.full(len(bar_idx), np.nan)
    for k in range(len(bar_idx)):
        same_dir = direction == direction[k]
        near = np.abs(bar_idx - bar_idx[k]) <= fresh_bars
        other = np.arange(len(bar_idx)) != k
        mask = same_dir & near & other
        if mask.any():
            out[k] = np.abs(level[mask] - level[k]).min()
    return out


def simulate_outcome(tape, day, entry_tick: int, direction: int, level: float,
                      costs: eng.Costs, stop_points: float = STOP_POINTS,
                      target_r: float = SINGLE_TARGET_R) -> eng.Trade | None:
    """One candidate's outcome under design.md S5.1's control arm -- a resting
    limit at the array's level, a `stop_points` stop, a single `target_r`
    target -- via `engine.resolve()`. Each candidate is its own isolated call
    (length-1 arrays): unlike the MFE diagnostic these candidates are the
    actual proposed trades, not overlapping counterfactuals, so `resolve()`'s
    one-position-at-a-time semantics is the right tool here, not the wrong
    one.
    """
    stop = level - direction * stop_points
    target = level + direction * stop_points * target_r
    trades = eng.resolve(tape, np.array([entry_tick]), np.array([direction]),
                          np.array([stop]), np.array([target]), costs,
                          limit_px=np.array([level]), day=day, timeout_min=240.0)
    return trades[0] if trades else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--start")
    ap.add_argument("--end")
    args = ap.parse_args()

    ctx4h = eng.prepare(CONTRACT, tf_secs=TF_4H, start=args.start, end=args.end, rth_only=False)
    pv, tick = eng.instrument(CONTRACT)
    bars, tape4h = ctx4h["bars"], ctx4h["tape"]
    print(f"tape (full-session, 4H bars): {CONTRACT} {ctx4h['start']}..{ctx4h['end']}, "
          f"{len(bars['t'])} 4H bars")

    bar_idx, direction, level = detect_4h_arrays(bars, tick)
    print(f"4H FVG arrays detected: {len(bar_idx)} "
          f"({int((direction > 0).sum())} bullish, {int((direction < 0).sum())} bearish)")

    gaps = nearest_neighbor_gap(bar_idx, direction, level)
    n_isolated_total = int(np.isnan(gaps).sum())
    finite = gaps[~np.isnan(gaps)]
    print(f"\nnearest same-direction array, ALL {len(bar_idx)} detected arrays "
          f"({n_isolated_total} with no same-direction neighbor within "
          f"{FRESH_BARS_4H} bars -- reported separately, not averaged in):")
    if len(finite):
        print(f"  median gap = {np.median(finite):.2f} pts   "
              f"mean = {finite.mean():.2f} pts   "
              f"n <= {CLUSTER_SKIP_POINTS:g} pts (stop) = {(finite <= CLUSTER_SKIP_POINTS).sum()} "
              f"({(finite <= CLUSTER_SKIP_POINTS).mean()*100:.1f}%)")
    edges = [0, 5, 10, 15, 20, 30, 50, 100, 1e9]
    counts, _ = np.histogram(finite, bins=edges)
    for k in range(len(counts)):
        lo, hi = edges[k], edges[k + 1]
        label = f"{lo:g}-{hi:g}pt" if hi < 1e9 else f">{lo:g}pt"
        print(f"  {label:>9} | {counts[k]:4d}")

    # Now find fills and restrict to the 09:30-11:00 candidates MeanTick would
    # actually see, for the sample arithmetic and the win-rate split.
    sod_all = tp.sec_of_day(tape4h["ts"])
    fills = [first_retrace_fill(bars, tape4h, int(bar_idx[k]), int(direction[k]),
                                 float(level[k]), FRESH_BARS_4H) for k in range(len(bar_idx))]
    day4h = tp.day_index(tape4h["ts"])
    costs = eng.Costs(point_value=pv, tick_size=tick)

    rows = []   # (fill_tick, direction, level, gap, session_day, outcome)
    for k, ft in enumerate(fills):
        if ft is None:
            continue
        if not (WINDOW_ET[0] <= int(sod_all[ft]) < WINDOW_ET[1]):
            continue
        trade = simulate_outcome(tape4h, day4h, ft, int(direction[k]), float(level[k]), costs)
        rows.append(dict(day=int(day4h[ft]), gap=gaps[k], trade=trade))

    cand_gaps = np.array([r["gap"] for r in rows])
    cand_finite = cand_gaps[~np.isnan(cand_gaps)]
    print(f"\nnearest same-direction array, JUST the {len(rows)} in-window (09:30-11:00 ET) "
          f"candidates -- this is the headline number, the other one above is the whole-tape "
          f"population:")
    if len(cand_finite):
        print(f"  median gap = {np.median(cand_finite):.2f} pts   "
              f"n <= {CLUSTER_SKIP_POINTS:g} pts (stop) = {(cand_finite <= CLUSTER_SKIP_POINTS).sum()} "
              f"of {len(rows)} ({(cand_finite <= CLUSTER_SKIP_POINTS).sum()/len(rows)*100:.1f}%), "
              f"no same-dir neighbor at all = {int(np.isnan(cand_gaps).sum())}")
    else:
        print("  no in-window candidates with a finite neighbor distance")

    n_sessions_total = ctx4h["days"]
    # "Session" for MeanTick means an RTH trading day, not this 4H tape's own
    # (full-session) day count -- fetch that separately, cheaply (cached).
    ctx_rth = eng.prepare(CONTRACT, tf_secs=60, start=args.start, end=args.end, rth_only=True)
    n_sessions_rth = ctx_rth["days"]
    days_with_candidate = {r["day"] for r in rows}
    print(f"\nsample arithmetic (FVG-only proxy, so this is an UPPER bound on candidate days):")
    print(f"  total sessions in tape: {n_sessions_rth} RTH trading days "
          f"({ctx_rth['start']}..{ctx_rth['end']}) "
          f"[note: design.md cites 275 for the continuous stitch's own session count, "
          f"which counts overnight-session boundaries, not RTH calendar days -- "
          f"measured here, not assumed: {n_sessions_rth}]")
    print(f"  sessions with >=1 in-window (09:30-11:00 ET) FVG candidate: {len(days_with_candidate)} "
          f"({len(days_with_candidate)/n_sessions_rth*100:.1f}%)")
    print(f"  total in-window candidates: {len(rows)}")
    n_test = int(round(len(rows) * (1 - TRAIN_FRACTION)))
    print(f"  after a {TRAIN_FRACTION:g}/{1-TRAIN_FRACTION:g} train/test split: "
          f"~{n_test} out-of-sample candidates "
          f"({'CLEARS' if n_test >= HOUSE_MIN_TRADES else 'BELOW'} the house gate of "
          f"{HOUSE_MIN_TRADES})")

    # The two readings, side by side.
    clustered = [r for r in rows if not np.isnan(r["gap"]) and r["gap"] <= CLUSTER_SKIP_POINTS]
    isolated = [r for r in rows if np.isnan(r["gap"]) or r["gap"] > CLUSTER_SKIP_POINTS]

    def _summarise(label, group):
        wins = [r for r in group if r["trade"] and r["trade"].pnl > 0]
        with_trade = [r for r in group if r["trade"]]
        wr = len(wins) / len(with_trade) if with_trade else float("nan")
        print(f"  {label}: n={len(group)}, filled+resolved={len(with_trade)}, "
              f"win rate={wr*100:.1f}%" if with_trade else
              f"  {label}: n={len(group)}, filled+resolved=0")

    print(f"\noutcome split at the {CLUSTER_SKIP_POINTS:g}pt stop "
          f"(hazard reading: clustered should LOSE more if 'wrong array' is real; "
          f"confluence reading: clustered should WIN more if coincidence is real):")
    _summarise("clustered (nearest same-dir array <= stop)", clustered)
    _summarise("isolated  (nearest same-dir array >  stop, or none found)", isolated)


if __name__ == "__main__":
    main()
