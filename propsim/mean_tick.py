#!/usr/bin/env python3
"""The MeanTick mirror.

PropSim's resolve() (`PropSim/engine.py:1085`) is scalar stop/target and produces
exactly one Trade per entry; it cannot express partial exits. Rather than extend
engine.py -- which would touch the ledger and fingerprint machinery of an
already-validated project -- this calls resolve() K times on the SAME entry_idx,
each call carrying that rung's contracts slice and its own target, and sums the
results.

EXACT: P&L, win rate, per-rung fill statistics, exit reasons.

WRONG, KNOWINGLY AND BOUNDEDLY: intra_mdd, and mae with it. Each call computes
its own worst excursion -- fall from ITS OWN running peak for intra_mdd, worst
print against ITS OWN entry for mae -- over ITS OWN window (entry to that rung's
own exit), scaled by that rung's own contracts. Summing across rungs adds
together excursions that were each maximised independently, over windows of
different lengths (the runner's window outlives rung 1's). That is a sum of
per-leg maxima, not the single simultaneous worst instant the real, one
position, stepping-down-in-size ladder actually lived through -- and a sum of
maxima can only be >= the true combined figure, never <. So BOTH intra_mdd and
mae are overstated, in the same direction, by the same mechanism. The prop
breach test is max(hwm - balance - mae, intra_mdd) (PropSim/engine.py:1067), and
since both of its inputs are inflated or exact, never deflated, this mirror is
CONSERVATIVE ON BREACH and must never be used to argue that a variant is safe.

This is a declared reduction, which is the house standard -- see PatternZone,
which shipped with no mirror at all and said so. An undeclared reduction is how
a false safety claim gets made; this one is written down here and again next to
any number derived from it (docs/validation.md, "Mirror fidelity").

Scope: this mirrors design.md S7, the ladder EXIT only. It does not port S4's
detection (4H PD arrays, the 15m rejection block) to Python -- that stays
C#-only; nothing in this project's task list ports it.
"""
import math
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))


def round_to_tick(px, tick):
    """MtMath.RoundToTick, character for character.

    `math.floor(px / tick + 0.5) * tick` -- deliberately NOT round(), whose
    banker's-rounding behaviour differs from this at exact ties. Every price in
    the ladder passes through here before comparison, same as the C# side.
    """
    if tick <= 0:
        return px
    # C#'s Math.Floor(NaN) returns NaN, silently -- Python's math.floor() returns
    # an int and RAISES on NaN (int(nan) is undefined). NaN is a real input here
    # (the "no level" sentinel for structural_price/runner_price), so this guard
    # is what keeps the two engines agreeing on the one case Python's stdlib
    # diverges on, rather than one throwing where the other returns a sentinel.
    if math.isnan(px):
        return px
    return math.floor(px / tick + 0.5) * tick


def build_ladder(direc, entry, stop_points, rung1_r, rung2_r, rung3_fallback_r,
                  structural_candidates, runner_price, contracts, tick_size, min_rung_ticks):
    """Port of MtLadder.BuildLadder (ninjascript/MeanTickExits.cs), same arithmetic
    in the same order -- this is what golden_ladder.csv holds to 1e-9 against.

    `direc` is +1 long / -1 short (PropSim's convention, not MtDir's -- this
    module bridges into resolve(), which wants a sign). `structural_candidates`
    is a list of levels, nearest-to-entry first (mirrors
    MtDetect.FindStructuralCandidates' ordering) -- None or [] means no
    candidate at all. `runner_price` uses float('nan') for "no level", matching
    the C# double.NaN sentinel used by the golden fixtures.

    Returns a dict: valid, stop_price, total_quantity, rungs (list of dict:
    index, price, quantity, is_structural, is_runner).
    """
    plan = dict(valid=False, stop_price=0.0, rungs=[], total_quantity=0)
    if direc == 0 or stop_points <= 0.0 or contracts <= 0:
        return plan

    sign = 1 if direc > 0 else -1
    plan["stop_price"] = round_to_tick(entry - sign * stop_points, tick_size)

    p1 = round_to_tick(entry + sign * stop_points * rung1_r, tick_size)
    p2 = round_to_tick(entry + sign * stop_points * rung2_r, tick_size)
    fallback = round_to_tick(entry + sign * stop_points * rung3_fallback_r, tick_size)

    # A caller that hands over only ONE already-filtered price could only ever accept
    # or reject it -- never fall through to the next-best level once the nearest
    # candidate turned out to be short of rung 2. Walking the list in order and taking
    # the first that clears p2 also gives the nearest candidate to p2 itself, since
    # every candidate beyond p2 is further from entry than p2 is, so distance-from-entry
    # order and distance-from-p2 order agree there. Round before comparing, never after
    # -- a raw candidate that lies fractionally beyond p2 but ROUNDS onto p2 (or short
    # of it) must not be treated as "beyond".
    structural_rounded = float("nan")
    for cand_raw in (structural_candidates or []):
        cand = round_to_tick(cand_raw, tick_size)
        if not math.isnan(cand) and sign * (cand - p2) > 0.0:
            structural_rounded = cand
            break
    runner_rounded = round_to_tick(runner_price, tick_size)

    structural_ok = not math.isnan(structural_rounded)
    p3 = structural_rounded if structural_ok else fallback

    runner_ok = not math.isnan(runner_rounded) and sign * (runner_rounded - p3) > 0.0
    p4 = runner_rounded if runner_ok else round_to_tick(p3 + sign * stop_points * rung2_r, tick_size)

    prices = [p1, p2, p3, p4]
    # Slot 3 here holds runner_ok, not "is rung 3 structural" -- it is only safe
    # because index 3 is only ever consumed when last == True, where the
    # "and not last" below zeroes it back out.
    structural = [False, False, structural_ok, runner_ok]

    # Allocate first, then drop: a rung that is dropped for spacing folds its
    # quantity forward so the position is always fully covered.
    k = min(4, contracts)
    base_qty = contracts // k
    extra = contracts - base_qty * k
    qty = [base_qty + (1 if i < extra else 0) for i in range(k)]

    min_gap = min_rung_ticks * tick_size
    prev = entry
    carried = 0

    for i in range(k):
        last = i == k - 1
        # The runner is never dropped: it is the rung the whole design is for.
        if not last and abs(prices[i] - prev) < min_gap:
            carried += qty[i]
            continue
        plan["rungs"].append(dict(
            index=len(plan["rungs"]) + 1,
            price=prices[i],
            quantity=qty[i] + carried,
            is_structural=structural[i] and not last,
            is_runner=last,
        ))
        carried = 0
        prev = prices[i]

    plan["total_quantity"] = sum(r["quantity"] for r in plan["rungs"])
    plan["valid"] = plan["total_quantity"] == contracts
    return plan


def _import_engine():
    """Deferred: only resolve_ladder() needs PropSim's engine, so the parity
    gate (build_ladder + round_to_tick above) has zero import-time dependency
    on it and cannot break from an unrelated change over there."""
    propsim_dir = os.path.normpath(os.path.join(_HERE, "..", "..", "PropSim"))
    if propsim_dir not in sys.path:
        sys.path.insert(0, propsim_dir)
    import engine
    return engine


def resolve_ladder(tape, entry_idx, direc, entry, stop_points, rung1_r, rung2_r,
                    rung3_fallback_r, structural_candidates, runner_price, contracts,
                    tick_size, min_rung_ticks, costs, **resolve_kwargs):
    """Build the ladder, then call engine.resolve() once per surviving rung on
    the SAME entry_idx, and return the list of resulting Trades -- callers sum
    what they need (see summarize() below, and the module docstring for what
    that sum is exact about and what it is not).

    Each rung fills at the SAME price: `limit_px` defaults to `entry` for every
    leg, modelling design.md S6.1's K independent limit orders resting at one
    price (confirmed in the Phase 0a probe -- all three legs filled at
    29840.25). Pass `limit_px=` in resolve_kwargs to override.

    UNTESTED AGAINST A GOLDEN FIXTURE: golden_ladder.csv holds rung SCHEDULES
    (build_ladder's output), not resolved trades -- no fixture exercises this
    function's use of resolve() against real tick data. It is written directly
    against resolve()'s real, current signature (PropSim/engine.py:1085), and
    correct by inspection, but that is not the same claim as "parity-checked".
    """
    engine = _import_engine()
    plan = build_ladder(direc, entry, stop_points, rung1_r, rung2_r, rung3_fallback_r,
                         structural_candidates, runner_price, contracts, tick_size, min_rung_ticks)
    if not plan["valid"]:
        return []

    resolve_kwargs.setdefault("limit_px", [entry])
    trades = []
    for rung in plan["rungs"]:
        leg = engine.resolve(tape, [entry_idx], [direc], [plan["stop_price"]],
                              [rung["price"]], costs, contracts=rung["quantity"],
                              **resolve_kwargs)
        trades.extend(leg)
    return trades


def summarize(trades):
    """Sum a resolve_ladder() result into one MeanTick trade's headline figures.

    EXACT: pnl, win (pnl > 0). WRONG, KNOWINGLY AND BOUNDEDLY: mae, mfe and
    intra_mdd -- see the module docstring. Never read mae/intra_mdd from this
    dict as a safety claim.
    """
    if not trades:
        return dict(pnl=0.0, win=False, mae=0.0, mfe=0.0, intra_mdd=0.0, legs=[])
    pnl = sum(t.pnl for t in trades)
    return dict(
        pnl=pnl,
        win=pnl > 0.0,
        mae=sum(t.mae for t in trades),
        mfe=sum(t.mfe for t in trades),
        intra_mdd=sum((t.intra_mdd or 0.0) for t in trades),
        legs=trades,
    )


def _demo():
    """ponytail: the one runnable check non-trivial logic leaves behind.

    Reproduces golden_ladder.csv's "rung1_only" fixture by hand (no dependency
    on the CSV file, so this runs even before the C# side has written one) and
    asserts the collapse-to-single-runner behaviour build_ladder exists for.
    research/compare_mirror.py is the real, CSV-driven gate; this is a smoke
    test that the module imports and runs standalone.
    """
    plan = build_ladder(1, 18000, 10.0, 1.0, 3.0, 4.0, [18055], 18120, 1, 0.25, 15)
    assert plan["valid"]
    assert len(plan["rungs"]) == 1
    r = plan["rungs"][0]
    assert r["index"] == 1 and r["quantity"] == 1
    assert r["is_runner"] and not r["is_structural"]
    assert abs(r["price"] - 18010.0) < 1e-9
    assert abs(plan["stop_price"] - 17990.0) < 1e-9
    assert round_to_tick(18000.13, 0.25) == 18000.25

    # Round-before-compare: 18030.1 lies 0.1pt beyond the RAW p2 (18030.0) but
    # ROUNDS onto it. Comparing raw-vs-rounded would pass the "beyond p2" check
    # and then round down onto a duplicate of rung 2 -- ninjascript/MeanTickExits.cs
    # fixed exactly this. min_rung_ticks=0 so the spacing fold can't mask it.
    p2 = build_ladder(1, 18000, 10.0, 1.0, 3.0, 4.0, [18030.1], 18120, 4, 0.25, 0)
    assert abs(p2["rungs"][2]["price"] - 18040.0) < 1e-9, "rung 3 falls back to 4R, not a p2 duplicate"
    assert p2["rungs"][2]["price"] != p2["rungs"][1]["price"]
    assert not p2["rungs"][2]["is_structural"]

    # Falls through past a rejected candidate to the next-best one: 18015 is nearer to
    # entry but short of p2 (18030), so it must be skipped in favor of 18055 -- a caller
    # that could only hand over ONE candidate could never express this.
    p3 = build_ladder(1, 18000, 10.0, 1.0, 3.0, 4.0, [18015, 18055], 18120, 4, 0.25, 15)
    assert abs(p3["rungs"][2]["price"] - 18055.0) < 1e-9, "falls through to the second candidate"
    assert p3["rungs"][2]["is_structural"]

    print("mean_tick.py: build_ladder self-check OK")


def _demo_resolve_ladder():
    """The second half of the runnable check: resolve_ladder() against a tiny,
    hand-built, monotonically-rising synthetic tape where every rung's target
    is hit in turn and nothing touches the stop -- so every number below is
    computable by hand (target - entry, in points, times $2/point, times 1
    contract per rung) and this fails loudly if the K-calls-summed wiring, not
    just build_ladder's arithmetic, ever breaks. Needs PropSim's engine
    (numpy) -- see _import_engine()'s docstring for why that is NOT required
    for the module's other self-check or for the parity gate.
    """
    import numpy as np
    engine = _import_engine()
    import tape as tp

    base = (np.int64(20000) * 86400 + tp.NET_EPOCH_S) * tp.TPS  # arbitrary day
    prices = [18000, 18005, 18010, 18015, 18030, 18035, 18040, 18060, 18090, 18120, 18125]
    tape_ = dict(ts=base + np.arange(len(prices), dtype=np.int64) * 10 * tp.TPS,
                 px=np.array(prices, dtype=float))
    costs = engine.Costs(commission=0.0, slippage_ticks=0.0, tick_size=0.25, point_value=2.0)

    trades = resolve_ladder(tape_, 0, 1, 18000.0, 10.0, 1.0, 3.0, 4.0,
                             None, 18120.0, 4, 0.25, 15, costs)
    assert len(trades) == 4, "one Trade per surviving rung"
    assert all(t.reason == "target" for t in trades), "the synthetic path never touches the stop"
    expected_pnl = [20.0, 60.0, 80.0, 240.0]     # (target - 18000) * 2.0 $/pt * 1 contract
    for t, exp in zip(trades, expected_pnl):
        assert abs(t.pnl - exp) < 1e-9, (t.pnl, exp)

    s = summarize(trades)
    assert abs(s["pnl"] - sum(expected_pnl)) < 1e-9
    assert s["win"] is True
    assert s["mae"] == 0.0 and s["intra_mdd"] == 0.0, "monotonic rise: no adverse excursion at all"
    print("mean_tick.py: resolve_ladder self-check OK")


if __name__ == "__main__":
    _demo()
    _demo_resolve_ladder()
