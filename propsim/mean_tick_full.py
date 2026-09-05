#!/usr/bin/env python3
"""MeanTick -- FULL PropSim plugin (detection + entry + exit).

Ports design.md's whole causal chain -- Gate 1 (a fresh, proximate 4H PD array),
Gate 2 (the 15m rejection block off it), the 50%-of-the-wick entry, and the
exit -- into one `Strategy.entries()` that PropSim can run on the real tick
tape. `mean_tick.py` in this same folder mirrors ONLY the ladder exit's
arithmetic (`build_ladder`, bit-for-bit against golden_ladder.csv) and is not a
plugin at all -- it imports os/sys and calls PropSim's `engine.resolve()`
directly. This file is the thing `plugins.py --check` can actually load.

Sandbox: nothing is imported except numpy (the loader hands a real plugin
`Strategy`, `Param` and `np`; the try/except stand-in below only fires when
this file runs standalone, e.g. under `if __name__ == "__main__"`, following
the pattern in ../../PullbackZone/propsim/pullback_zone.py).

Spec: docs/design.md. Detection is ported arithmetic-for-arithmetic from
ninjascript/MeanTickCore.cs (MtDetect.*, MtSession, WilderAtr); rounding is
MtMath.RoundToTick. Session/window/London constants are ET seconds-of-day,
matching this tape's own convention (tape.py:251 defines RTH as
9*3600+30*60 .. 16*3600 directly in sec_of_day units -- these ticks are
already stamped in ET wall-clock, no timezone conversion needed, unlike the
C# shell which must build its own ET clock from a machine timezone).

=================================== FIDELITY ===================================

1. **Ladder mode is reduced to rung 1 only.** PropSim's `resolve()` takes one
   scalar stop and one scalar target per trade and produces exactly one Trade;
   MeanTick's Ladder (K=4 partial exits, two fixed rungs, two structural rungs,
   a runner off the London range, per-rung independent brackets) cannot be
   expressed. `exit_mode=1` targets `RungR1` (default 1.0R) -- the FIRST exit
   level, the fast leg the source calls "it pays for the trade" -- and stops
   there. Rungs 2-4, the runner, the London-range target, MinRungTicks
   spacing/folding and UseBreakEven are NOT modeled. This is a reduction, not a
   safety claim: it can only ever UNDERSTATE Ladder's real payoff (a trade that
   clears rung 1 and keeps running never gets credited for that), never
   overstate it, so a positive Ladder-mode number here is conservative and a
   negative one is not evidence Ladder itself is bad -- only that its fast leg
   alone is not enough.
2. **UseClusterSkip (A1) is implemented** (the same-direction-within-StopPoints
   skip, straight off the already-computed candidate list) but ships OFF by
   default, matching design.md's own "not swept" decision.
3. **RunnerCutoffEt and the break-even move are not modeled.** Both are
   Ladder-only mechanics (design.md's own "I2": SingleTarget deliberately has
   no time-based exit) and are moot once Ladder is reduced to rung 1 above.
4. **4H bar alignment is assumed fixed-clock, ET-midnight-anchored** (00:00,
   04:00, 08:00, 12:00, 16:00, 20:00) -- NT8's own 240-minute series alignment
   on this specific chart template is not independently verified against this
   tape. Gate 1's freshness/proximity math itself (`is_qualified_htf`,
   `find_qualified_htf_candidates`) is a faithful port once bars exist.
5. **Session/window/TTL gating runs on this plugin's native 15-minute clock**,
   not NT8's 1-minute primary. A boundary candidate can differ by at most one
   15-minute bar from the real strategy's 1-minute-granular check -- negligible
   next to `EntryTtlMinutes=90`.
6. **`MaxTradesPerDay` > 1 is not faithfully modeled.** `resolve()` walks one
   position at a time; a value above the shipped default of 1 would need
   concurrent positions this port does not support. Not swept in design.md, so
   this never binds at the shipped default.
7. **London range, VerboseGateDiagnostics telemetry, and 4H-bar-count-40
   eviction are otherwise faithfully ported** (`BARS4H_WINDOW` below) where
   they still matter to what gets selected; the ones that only matter to the
   runner (out of scope by #1) are dropped along with it.

=================================================================================
"""
import numpy as np

try:
    Strategy
except NameError:
    class Strategy:                      # pragma: no cover - sandbox stand-in
        # Real engine.Strategy sets these class defaults too (TICK_SIZE/
        # POINT_VALUE), overwritten per-instance by backtest() with the
        # contract actually running; a strategy built directly, as the
        # selfchecks below do, keeps the class default.
        tick = 0.25
        point_value = 20.0

    class Param:                         # pragma: no cover - sandbox stand-in
        def __init__(self, default, lo, hi, desc, fixed=False):
            self.default, self.lo, self.hi = default, lo, hi
            self.desc, self.fixed = desc, fixed

TICK = 0.25
_TPS = 10_000_000                      # .NET ticks per second
_NET_EPOCH_S = 62135596800             # seconds from 0001-01-01 to 1970-01-01

# The bar the strategy's own arming decision runs on (design.md 4.2: "Gate 2 ...
# is defined on a 15-minute candle close"). entries() rebuilds this straight
# from `tape`, ignoring the engine's own `bars` -- same reason PullbackZone
# does: two series (here, 15m AND 4H) off one tape, and the engine builds
# exactly one at whatever tf_secs the caller asked for.
TF_SECS = 900
SESSION = "09:30-11:00 ET, RTH calendar day (full_session=True for the 4H fold)"

# MtSession's own constants (MeanTickCore.cs) -- ET seconds-of-day.
SESSION_START_SEC = 9 * 3600 + 30 * 60      # 34200
SESSION_END_SEC = 11 * 3600                 # 39600

# Bars4HWindow (MeanTickStrategy.cs): NT8 keeps only the most recent 40 4H bars
# in memory, so nothing older can ever be found -- capping the scan here is
# not a performance shortcut, it is the same eviction.
BARS4H_WINDOW = 40
ATR4H_PERIOD = 14


def round_to_tick(px, tick):
    """MtMath.RoundToTick, character for character (see mean_tick.py's copy;
    duplicated rather than imported -- this file has zero non-numpy deps)."""
    if tick <= 0 or px != px:          # NaN guard: NaN != NaN
        return px
    return np.floor(px / tick + 0.5) * tick


def wilder_atr(h, l, c, n):
    """House Wilder recursion (MeanTickTypes.cs WilderAtr / PullbackZoneCore's
    copy): seed = tr[0] itself, TR reaches across session boundaries, no reset."""
    prev = np.concatenate(([c[0]], c[:-1]))
    tr = np.maximum(h - l, np.maximum(np.abs(h - prev), np.abs(l - prev)))
    tr[0] = h[0] - l[0]
    out = np.empty(len(tr))
    run = 0.0
    for i in range(len(tr)):
        if i < n:
            run = (run * i + tr[i]) / (i + 1)
        else:
            run += (tr[i] - run) / n
        out[i] = run
    return out


def _day_index(ts):
    return (ts // _TPS - _NET_EPOCH_S) // 86400


def _sec_of_day(ts):
    return (ts // _TPS - _NET_EPOCH_S) % 86400


def _build_bars(tape, secs):
    """OHLC per (day, fixed-clock time-slot) -- local, numpy-only copy of
    tape.build_bars, ported the way PullbackZone ports it: the sandbox hands a
    strategy exactly one bar size and this needs two (15m and 4H) off the same
    ticks."""
    ts, px = tape["ts"], tape["px"]
    if not len(ts):
        z = np.array([])
        return dict(t=z, o=z, h=z, l=z, c=z, start=np.array([], np.int64),
                    end=np.array([], np.int64))
    slot = _day_index(ts) * (86400 // secs + 1) + _sec_of_day(ts) // secs
    starts = np.concatenate(([0], np.flatnonzero(np.diff(slot)) + 1))
    ends = np.concatenate((starts[1:], [len(ts)]))
    return dict(t=ts[starts], o=px[starts], c=px[ends - 1],
                h=np.maximum.reduceat(px, starts),
                l=np.minimum.reduceat(px, starts), start=starts, end=ends)


def _bucket_end_ts(t, secs):
    """The FIXED scheduled close of the bucket each timestamp in `t` (a bar's
    own first-tick time) belongs to -- computed from the clock grid itself,
    not from whether a later bar happens to exist. Bars are built only where
    ticks exist (`_build_bars` skips empty slots), so a real gap right after
    the bar in question must not read as "still forming"; this is what makes
    that safe."""
    sod = _sec_of_day(t)
    day = _day_index(t)
    end_sod = ((sod // secs) + 1) * secs
    return (day * 86400 + _NET_EPOCH_S + end_sod) * _TPS


# --------------------------------------------------------- MtDetect (ported)

def try_rejection_block(o, h, l, c, i, expect_dir, tick, min_wick_ticks, wick_ratio_max):
    """MtDetect.TryRejectionBlock. expect_dir: +1 long (bullish, wick below) /
    -1 short (bearish, wick above). Returns (level, top, bottom) or None."""
    body_top = max(o[i], c[i])
    body_bot = min(o[i], c[i])
    if expect_dir < 0:
        if c[i] >= o[i]:
            return None                          # must close bearish
        reject = h[i] - body_top
        opposite = body_bot - l[i]
        level = body_top + reject * 0.5
    else:
        if c[i] <= o[i]:
            return None                          # must close bullish
        reject = body_bot - l[i]
        opposite = h[i] - body_top
        level = body_bot - reject * 0.5
    if reject < min_wick_ticks * tick or reject <= 0.0:
        return None
    if opposite / reject > wick_ratio_max:
        return None
    return round_to_tick(level, tick), h[i], l[i]


def try_fvg(h, l, a_idx, c_idx, min_height, tick):
    """3-bar imbalance (MtDetect.TryFvg): only bars a and c are read, the
    middle bar never contributes. Returns (dir, level) or None."""
    if l[c_idx] > h[a_idx] and l[c_idx] - h[a_idx] >= min_height:
        return 1, round_to_tick((l[c_idx] + h[a_idx]) * 0.5, tick)
    if h[c_idx] < l[a_idx] and l[a_idx] - h[c_idx] >= min_height:
        return -1, round_to_tick((l[a_idx] + h[c_idx]) * 0.5, tick)
    return None


def try_order_block_from_fvg(o, h, l, c, fvg_first_idx, direc, tick, floor=0):
    """MtDetect.TryOrderBlockFromFvg: scan backward from (and including) the
    FVG's own first candle for the last opposite-close bar. `floor` is the
    BARS4H_WINDOW eviction boundary -- the list this mirrors a scan over
    cannot hold anything older. Returns (level, bar_idx) or None."""
    for i in range(fvg_first_idx, floor - 1, -1):
        opposite = (c[i] < o[i]) if direc > 0 else (c[i] > o[i])
        if not opposite:
            continue
        return round_to_tick((h[i] + l[i]) * 0.5, tick), i
    return None


def is_qualified_htf(level, bar_idx, current_idx, price, fresh_bars, atr, prox_mult):
    """MtDetect.IsQualifiedHtf: freshness then proximity."""
    if current_idx - bar_idx > fresh_bars:
        return False
    if atr <= 0.0:
        return False
    return abs(price - level) <= prox_mult * atr


def find_qualified_htf_candidates(o4, h4, l4, c4, current_idx, price, tick,
                                   fresh_bars, atr, prox_mult, min_wick_ticks,
                                   wick_ratio_max):
    """MtDetect.FindQualifiedHtfCandidates: scans newest-to-oldest from
    `current_idx` down to the BARS4H_WINDOW floor, per-bar priority rejection
    block (long, then short), then FVG, then the order block anchored to that
    FVG. Returns a list of (level, dir, bar_idx), newest-qualifying-bar first
    -- candidates[0] is "the" chosen array; the rest exist for cluster-skip."""
    found = []
    window_lo = max(2, current_idx - (BARS4H_WINDOW - 1))
    for i in range(current_idx, window_lo - 1, -1):
        rb = try_rejection_block(o4, h4, l4, c4, i, 1, tick, min_wick_ticks, wick_ratio_max)
        if rb is not None:
            level = rb[0]
            if is_qualified_htf(level, i, current_idx, price, fresh_bars, atr, prox_mult):
                found.append((level, 1, i))
        rb = try_rejection_block(o4, h4, l4, c4, i, -1, tick, min_wick_ticks, wick_ratio_max)
        if rb is not None:
            level = rb[0]
            if is_qualified_htf(level, i, current_idx, price, fresh_bars, atr, prox_mult):
                found.append((level, -1, i))
        if i < 2:
            continue
        fvg = try_fvg(h4, l4, i - 2, i, 0.0, tick)
        if fvg is not None:
            fdir, flevel = fvg
            if is_qualified_htf(flevel, i, current_idx, price, fresh_bars, atr, prox_mult):
                found.append((flevel, fdir, i))
            ob = try_order_block_from_fvg(o4, h4, l4, c4, i - 2, fdir, tick, floor=window_lo)
            if ob is not None:
                olevel, obar = ob
                if is_qualified_htf(olevel, obar, current_idx, price, fresh_bars, atr, prox_mult):
                    found.append((olevel, fdir, obar))
    return found


def try_rejection_off_level(o15, h15, l15, c15, i, htf_level, htf_dir, tick,
                             min_wick_ticks, wick_ratio_max):
    """MtDetect.TryRejectionOffLevel -- Gate 2, all three conditions: (1) the
    candle's range touches htf_level, (2) it closes OUTSIDE the level in the
    rejection direction, (3) a one-sided rejection wick on the correct side.
    Returns the entry price (50% of THIS candle's own wick) or None.

    design.md's own counter-example (the one that shipped past review) is
    reproduced verbatim in the selfcheck below: a candle can touch a level,
    leave a clean wick, and still close back through it -- condition 2 is not
    redundant with condition 3."""
    if h15[i] < htf_level or l15[i] > htf_level:
        return None                                      # condition 1: no touch
    sign = 1 if htf_dir > 0 else -1
    if sign * (c15[i] - htf_level) <= 0:
        return None                                      # condition 2: wrong side
    rb = try_rejection_block(o15, h15, l15, c15, i, htf_dir, tick, min_wick_ticks, wick_ratio_max)
    if rb is None:
        return None                                      # condition 3: no rejection wick
    return rb[0]


_EMPTY5 = (np.array([], np.int64), np.array([], np.int8),
           np.array([]), np.array([]), np.array([]))


class MeanTick(Strategy):
    """Limit at the 50% of a 15m rejection block off a fresh 4H PD array
    (RichKO Framework B). Spec: docs/design.md. Parameter names are the NT8
    property names in snake_case; the list is closed and mirrors
    MeanTickStrategy.cs's `[NinjaScriptProperty]` block one-to-one, all
    `fixed=True` -- this project's own gate (design.md S9) is measured on the
    shipped defaults, not a sweep, and every dial not on design.md S10's closed
    list is a constant here too (BARS4H_WINDOW, ATR4H_PERIOD), same as there.
    """
    name, label = "mean_tick", "MeanTick (4H PD array -> 15m rejection -> mean-threshold limit)"
    uses_ticks = True
    # The London range and true 4H continuity both need pre-market ticks; an
    # RTH-only tape would silently zero out Gate 1's own history depth over a
    # session boundary and read like "no edge" rather than like a scoping bug
    # (Strategy.full_session's own docstring).
    full_session = True

    params = {
        "stop_points": Param(10.0, 0.25, 1000.0,
                             "StopPoints -- fixed stop distance, points", fixed=True),
        "max_trades_per_day": Param(1, 1, 50,
                                    "MaxTradesPerDay -- see FIDELITY #6 above 1", fixed=True),
        "entry_ttl_min": Param(90, 1, 600, "EntryTtlMinutes", fixed=True),
        "use_cluster_skip": Param(0, 0, 1,
                                  "UseClusterSkip (A1) -- default OFF, not swept", fixed=True),
        "fresh_bars_4h": Param(5, 1, 50, "FreshBars4H", fixed=True),
        "proximity_atr_mult": Param(1.5, 0.1, 10.0,
                                    "ProximityAtrMult -- pre-registered, our invention", fixed=True),
        "min_wick_ticks": Param(8, 1, 200, "MinWickTicks", fixed=True),
        "wick_ratio_max": Param(0.33, 0.01, 1.0, "WickRatioMax", fixed=True),
        "exit_mode": Param(0, 0, 1,
                          "0=SingleTarget (the control arm), 1=Ladder reduced to "
                          "rung 1 only -- see FIDELITY #1", fixed=True),
        "single_target_r": Param(4.0, 0.1, 20.0, "SingleTargetR", fixed=True),
        "rung_r1": Param(1.0, 0.1, 20.0,
                        "RungR1 -- used AS the target when exit_mode=1", fixed=True),
        "contracts": Param(4, 1, 100, "Contracts", fixed=True),
        "flatten_hhmm": Param(1600, 0, 2359,
                              "session-close flatten, ET HHMM -- proxy for "
                              "IsExitOnSessionCloseStrategy (design.md has no "
                              "explicit RTH-close time)", fixed=True),
    }

    def risk_ticks(self, p) -> float:
        return float(p["stop_points"]) / self.tick

    def entries(self, bars, tape, p):
        # `bars` is deliberately unused -- this setup needs 15m AND 4H series
        # off the same ticks and the engine builds exactly one; see the module
        # docstring and PullbackZone's identical choice.
        ts, px = tape["ts"], tape["px"]
        if len(ts) < 10:
            return _EMPTY5

        tick = self.tick
        min_wick = int(p["min_wick_ticks"])
        wick_ratio = float(p["wick_ratio_max"])
        fresh_bars = int(p["fresh_bars_4h"])
        prox_mult = float(p["proximity_atr_mult"])
        stop_pts = float(p["stop_points"])
        ttl_s = int(p["entry_ttl_min"]) * 60
        max_trades = int(p["max_trades_per_day"])
        cluster_skip = bool(p["use_cluster_skip"])
        r_mult = (float(p["single_target_r"]) if int(p["exit_mode"]) == 0
                  else float(p["rung_r1"]))

        b15 = _build_bars(tape, TF_SECS)
        b4 = _build_bars(tape, 4 * 3600)
        n15, n4 = len(b15["c"]), len(b4["c"])
        if n15 < 1 or n4 < ATR4H_PERIOD:
            return _EMPTY5

        o15, h15, l15, c15, t15 = b15["o"], b15["h"], b15["l"], b15["c"], b15["t"]
        o4, h4, l4, c4, t4 = b4["o"], b4["h"], b4["l"], b4["c"], b4["t"]

        atr4 = wilder_atr(h4, l4, c4, ATR4H_PERIOD)

        # "now" for every gate below is THIS bar's own scheduled CLOSE, not its
        # open -- NT8 evaluates at Time[0] of the primary bar whose close makes
        # `new15` true, which sits within one minute of the 15m bar's real
        # close, not at its open 15 minutes earlier (FIDELITY #5).
        t_now = t15 + TF_SECS * _TPS
        day_now = _day_index(t_now)
        sod_now = _sec_of_day(t_now)
        in_window = (sod_now >= SESSION_START_SEC) & (sod_now < SESSION_END_SEC)

        # Last CLOSED 4H bar as of each 15m bar's own "now" -- a fixed-clock
        # bucket boundary, immune to a later 4H bucket having zero ticks
        # (see _bucket_end_ts's own docstring).
        bucket_end4 = _bucket_end_ts(t4, 4 * 3600)
        htf_idx_of = np.searchsorted(bucket_end4, t_now, side="right") - 1

        et, dr, st, tg, lim = [], [], [], [], []
        day_cur, busy_until, traded_today = None, -1, 0

        for j in range(n15):
            if day_now[j] != day_cur:
                day_cur, busy_until, traded_today = day_now[j], -1, 0
            if not in_window[j]:
                continue
            if traded_today >= max_trades or t_now[j] < busy_until:
                continue

            ci = int(htf_idx_of[j])
            if ci < ATR4H_PERIOD - 1:                # ATR(14) not warm yet
                continue
            atr_val = float(atr4[ci])
            if not (atr_val > 0.0):
                continue

            found = find_qualified_htf_candidates(
                o4, h4, l4, c4, ci, float(c15[j]), tick, fresh_bars, atr_val,
                prox_mult, min_wick, wick_ratio)
            if not found:
                continue
            level0, dir0 = found[0][0], found[0][1]

            if cluster_skip:
                skip = False
                for level_k, dir_k, _ in found[1:]:
                    if dir_k != dir0:
                        continue
                    skip = abs(level_k - level0) <= stop_pts
                    break
                if skip:
                    continue

            entry_price = try_rejection_off_level(
                o15, h15, l15, c15, j, level0, dir0, tick, min_wick, wick_ratio)
            if entry_price is None:
                continue

            sign = 1 if dir0 > 0 else -1
            stop_price = round_to_tick(entry_price - sign * stop_pts, tick)
            target_price = round_to_tick(entry_price + sign * stop_pts * r_mult, tick)

            t_place = int(t_now[j])
            day_close_ts = (int(day_now[j]) * 86400 + _NET_EPOCH_S) * _TPS
            win_close_ts = day_close_ts + SESSION_END_SEC * _TPS
            expire_ts = min(t_place + ttl_s * _TPS, win_close_ts)

            a = int(np.searchsorted(ts, t_place, "right"))
            b = int(np.searchsorted(ts, expire_ts, "right"))
            filled_i = -1
            if b > a:
                seg = px[a:b]
                hit = (np.flatnonzero(seg <= entry_price) if dir0 > 0
                       else np.flatnonzero(seg >= entry_price))
                if len(hit):
                    filled_i = a + int(hit[0])

            if filled_i < 0:
                busy_until = expire_ts       # TTL/window cancel -- free to re-arm
                continue

            et.append(filled_i); dr.append(dir0)
            st.append(stop_price); tg.append(target_price); lim.append(entry_price)
            traded_today += 1
            busy_until = 10 ** 18            # MaxTradesPerDay=1 in the shipped
                                              # default: the day is done (FIDELITY #6)

        if not et:
            return _EMPTY5
        order = np.argsort(np.asarray(et))
        return (np.asarray(et, np.int64)[order], np.asarray(dr, np.int8)[order],
                np.asarray(st)[order], np.asarray(tg)[order], np.asarray(lim)[order])


# ---------------------------------------------------------------- selfcheck

def _selfcheck_core():
    """Unit-level ports, checked directly against MeanTickCore.cs's own
    documented behaviour -- including the exact counter-example design.md
    records as having shipped past review once."""
    assert round_to_tick(18000.13, 0.25) == 18000.25
    assert round_to_tick(float("nan"), 0.25) != round_to_tick(float("nan"), 0.25)  # NaN

    # Bullish rejection: O=18010 L=18000 C=18015 H=18015 -- reject=10 (40 ticks),
    # opposite=0, level = 18010 - 5 = 18005.
    o = np.array([18010.0]); h = np.array([18015.0]); l = np.array([18000.0]); c = np.array([18015.0])
    rb = try_rejection_block(o, h, l, c, 0, 1, TICK, 8, 0.33)
    assert rb is not None and abs(rb[0] - 18005.0) < 1e-9, rb
    assert try_rejection_block(o, h, l, c, 0, -1, TICK, 8, 0.33) is None  # wrong dir, not bearish

    # design.md's own MeanTickCore.cs counter-example: touch passes, wick ratio
    # passes (0.067), close never got back above the level -- CloseWrongSide.
    o2 = np.array([17990.0]); h2 = np.array([18001.0]); l2 = np.array([17960.0]); c2 = np.array([17999.0])
    assert try_rejection_off_level(o2, h2, l2, c2, 0, 18000.0, 1, TICK, 8, 0.33) is None
    # Same candle, level dropped to 17995 so the close (17999) DOES clear it --
    # the isolation half of the counter-example: only condition 2 moved.
    assert try_rejection_off_level(o2, h2, l2, c2, 0, 17995.0, 1, TICK, 8, 0.33) is not None

    # FVG: bullish gap between bar a (h=100) and bar c (l=103) -> level 101.5.
    # Bar 1 overlaps bar 0 (no gap yet) -- (0,1) must NOT read as a gap.
    h3 = np.array([100.0, 101.0, 106.0]); l3 = np.array([98.0, 99.0, 103.0])
    fvg = try_fvg(h3, l3, 0, 2, 0.0, TICK)
    assert fvg == (1, 101.5), fvg
    assert try_fvg(h3, l3, 0, 1, 0.0, TICK) is None   # bars overlap, no gap yet

    # Order block from that FVG: scan backward from bar 0 for the last
    # opposite-close (bearish, since the gap is bullish) candle.
    ob_o = np.array([102.0, 99.0]); ob_c = np.array([99.0, 101.0])  # bar0 bearish
    ob = try_order_block_from_fvg(ob_o, h3[:2], l3[:2], ob_c, 0, 1, TICK)
    assert ob is not None and ob[1] == 0, ob

    # Freshness + proximity.
    assert is_qualified_htf(100.0, 10, 12, 100.5, 5, 2.0, 1.5)          # fresh, close
    assert not is_qualified_htf(100.0, 4, 12, 100.5, 5, 2.0, 1.5)       # too old (8 > 5)
    assert not is_qualified_htf(100.0, 10, 12, 105.0, 5, 2.0, 1.5)      # too far (5 > 3)

    print("mean_tick_full.py: core selfcheck OK")


def _fx_pad_ticks(n_days, day0):
    """n_days of a single 4H bucket (04:00-08:00 ET) each, constant true range
    10 -- WilderAtr converges to EXACTLY 10.0 on a constant series, so the
    fixture's admission window (ProximityAtrMult=1.5 -> 15 points) is known
    without depending on the recursion's own warmup arithmetic."""
    ts, px = [], []
    for d in range(n_days):
        base = (int(day0 + d) * 86400 + _NET_EPOCH_S + 4 * 3600) * _TPS
        ts += [base, base + 600 * _TPS]
        px += [17990.0, 18000.0]
    return ts, px


def _fx_tape(fill_offset_min=20, touch=True, extra_ticks=None):
    """One padded day-20 setup: 20 days of ATR warmup, then day 20's own
    04:00-08:00 4H bucket carries a clean bullish rejection block (level
    18005.0, see the module's worked comment), and the 09:30 15m bar carries
    a matching 15m rejection whose own 50%-of-wick is 18003.0. A print at
    09:30 + fill_offset_min minutes trades down through 18003.0 to fill the
    resting long limit -- `touch=False` shifts the 15m bar so Gate 2's own
    touch condition fails, for the negative case."""
    day0 = 20000
    ts, px = _fx_pad_ticks(20, day0)
    test_day = day0 + 20

    # 4H rejection block: O=18010 L=18000 C=18015 H=18015 -> level 18005.0.
    b4 = (int(test_day) * 86400 + _NET_EPOCH_S + 4 * 3600) * _TPS
    ts += [b4, b4 + 600 * _TPS, b4 + 1200 * _TPS]
    px += [18010.0, 18000.0, 18015.0]

    # 09:30 15m bar. touch=True: O=18006 L=18000 C=18010 H=18010 -> touches
    # 18005, closes above it, 50%-of-wick = 18003.0 (the entry). touch=False:
    # shifted up so the low (18010) never reaches 18005 at all.
    b15 = (int(test_day) * 86400 + _NET_EPOCH_S + 9 * 3600 + 30 * 60) * _TPS
    if touch:
        ts += [b15, b15 + 300 * _TPS, b15 + 600 * _TPS]
        px += [18006.0, 18000.0, 18010.0]
    else:
        ts += [b15, b15 + 300 * _TPS, b15 + 600 * _TPS]
        px += [18020.0, 18012.0, 18024.0]

    # Fill print: trades down through 18003.0.
    fill_t = b15 + 900 * _TPS + fill_offset_min * 60 * _TPS
    ts += [fill_t, fill_t + 60 * _TPS]
    px += [18002.5, 18004.0]

    if extra_ticks:
        for dt, p in extra_ticks:
            ts.append(fill_t + dt * _TPS); px.append(p)

    n = len(ts)
    return dict(ts=np.array(ts, np.int64), px=np.array(px, np.float64),
                vol=np.ones(n, np.int64), side=np.zeros(n, np.int8))


def _selfcheck_positive():
    t = _fx_tape()
    s = MeanTick()
    p = {k: v.default for k, v in s.params.items()}
    et, dr, st, tg, lim = s.entries(None, t, p)
    assert len(et) == 1, (len(et), et)
    assert dr[0] == 1, dr
    assert abs(lim[0] - 18003.0) < 1e-9, lim
    assert abs(st[0] - 17993.0) < 1e-9, st            # entry - StopPoints(10)
    assert abs(tg[0] - 18043.0) < 1e-9, tg             # entry + 10 * SingleTargetR(4.0)
    assert t["px"][et[0]] <= lim[0] + 1e-9             # the fill tick actually reached the limit

    # Ladder mode: same setup, target reduced to rung 1 (1.0R) -- FIDELITY #1.
    p_ladder = dict(p, exit_mode=1)
    et2, dr2, st2, tg2, lim2 = s.entries(None, t, p_ladder)
    assert len(et2) == 1 and abs(tg2[0] - 18013.0) < 1e-9, tg2   # entry + 10 * 1.0
    print("mean_tick_full.py: positive selfcheck OK")


def _selfcheck_negative():
    """Same tape, the 15m bar shifted so it never touches the 4H level --
    Gate 2 condition 1 fails and no trade is produced."""
    t = _fx_tape(touch=False)
    s = MeanTick()
    p = {k: v.default for k, v in s.params.items()}
    et, dr, st, tg, lim = s.entries(None, t, p)
    assert len(et) == 0, len(et)
    print("mean_tick_full.py: negative selfcheck OK")


def _selfcheck_truncation():
    """No-lookahead invariant: truncate the tape a couple of ticks past the
    fill and the SAME trade (same entry/stop/target) must still come out --
    nothing downstream of the fill tick may have contributed to what was
    already decided."""
    t = _fx_tape()
    s = MeanTick()
    p = {k: v.default for k, v in s.params.items()}
    et, dr, st, tg, lim = s.entries(None, t, p)
    assert len(et) == 1
    cut = int(et[0]) + 1
    t2 = {k: (v[:cut] if k in ("ts", "px", "vol", "side") else v) for k, v in t.items()}
    et2, dr2, st2, tg2, lim2 = s.entries(None, t2, p)
    assert len(et2) == 1
    assert et2[0] == et[0] and dr2[0] == dr[0]
    assert abs(st2[0] - st[0]) < 1e-9 and abs(tg2[0] - tg[0]) < 1e-9 and abs(lim2[0] - lim[0]) < 1e-9
    print("mean_tick_full.py: truncation (no-lookahead) selfcheck OK")


if __name__ == "__main__":
    _selfcheck_core()
    _selfcheck_positive()
    _selfcheck_negative()
    _selfcheck_truncation()
