# MeanTick — validation

Phase 0a (the plumbing probe) is documented in `design.md` S6.1, in-place, because it produced
amendments to that section's own architecture. This file covers **Phase 0b** and **Phase 0c** —
the two diagnostics that run before any strategy code exists (`design.md` S9) and that can retire
or reshape the ladder before Task 7 writes it.

Both scripts consume `PropSim`'s tape loader (`engine.prepare`, `tape.py`) directly — they do not
invent a parallel one. Reproduce with:

```bash
python3 research/mfe_diagnostic.py
python3 research/cluster_histogram.py
```

Both ran against `PropSim`'s `ALL` contract — the stitched, continuous NQ tape (`continuous.py`).
**Note on session count:** `design.md` cites 275 sessions for this tape, which is
`continuous.load_meta()["sessions"]` — a count of overnight-session boundaries in the raw stitch.
RTH-sliced (`rth_only=True`, what both scripts below actually trade against), the same tape yields
**238** calendar trading days, 2025-08-03 → 2026-08-04. The two numbers measure different things
(overnight sessions vs. RTH calendar days) and neither is wrong; 238 is the one both diagnostics
below actually ran on, and it is what "total sessions" means in the sample arithmetic.

---

## Phase 0b — MFE modality

**Protocol.** For every 1-minute bar in 09:30–11:00 ET, both directions, a hypothetical entry at
the bar's close, walked forward tick-by-tick until price moves `STOP_POINTS = 10` against it or the
session ends — whichever comes first. Maximum favourable excursion up to that point, in R
(`= points / 10`). No commission, no slippage: this characterises the **tape**, not a strategy's
costs, and adding either would only shift every R value by a constant fraction, not change the
shape. A tape-level data hole (`ts` gap > `engine.MAX_GAP_S`, 5 in the whole tape) truncates the
walk at the last real tick before it, the same rule `engine.resolve()` uses — a hole is not a price
move.

**Deliberately not `engine.resolve()`.** That function holds one position at a time (a `free_at`
cooldown gate built for a real strategy). These 39,562 hypothetical entries overlap on purpose —
every minute gets its own independent counterfactual — so reusing `resolve()` would have silently
skipped nearly all of them while the previous one's walk was still in flight. `research/mfe_diagnostic.py`
walks each one on its own tape slice instead, using the same day-boundary and gap-guard conventions.

**Result**, full tape (238 RTH sessions, 220 of which have at least one bar inside the window --
the other 18 are likely truncated/holiday-shortened sessions, not chased down further since they
don't change the shape below; 81.3M ticks, 39,562 walks, 5.9% never hit the stop before their
session closed and are right-censored at whatever MFE they'd reached):

```
median R = 0.975   mean R = 3.475   max R = 161.6

    0-0.5R |  13267 (33.5%) ##################################################
    0.5-1R |   6519 (16.5%) #########################
    1-1.5R |   3920 ( 9.9%) ###############
    1.5-2R |   2713 ( 6.9%) ##########
      2-3R |   3421 ( 8.6%) #############
      3-4R |   1950 ( 4.9%) #######
      4-5R |   1333 ( 3.4%) #####
      5-6R |    903 ( 2.3%) ###
      6-8R |   1252 ( 3.2%) #####
     8-10R |    863 ( 2.2%) ###
      >10R |   3421 ( 8.6%)  #############   <- an open bin; NOT comparable to the fixed-width ones above
```

**Modality test.** SciPy 1.16.2 is installed but does not ship Hartigan's dip test natively, and
the only PyPI package for it (`diptest`) is not installed — this WSL has no `pip`, only
`uv pip install --target ~/.local/...`, which shadows apt-managed system packages
(`.claude/memory/wsl-python-uv-target-installs.md` records a real breakage from exactly this
pattern). Adding a third-party dependency for one diagnostic script was not worth that risk, and
the task brief itself pre-authorises the fallback used here: the ratio of density in the 3R–8R band
to density in the 1R–2R band, against a 0.15 threshold.

```
lo density (1-2R)  = 16.8%/R
hi density (3-8R)  =  2.7%/R
ratio              =  0.164   (threshold 0.15 -> clears it, narrowly)
```

**The honest reading, not just the pass/fail.** Two things the ratio alone hides:

1. **This is not textbook bimodality.** Printing density per R-unit (count divided by bin width,
   the fixed-width bins only) shows a distribution that falls *monotonically* from 0 outward —
   67.1, 33.0, 19.8, 13.7, 8.6, 4.9, 3.4, 2.3, 1.6, 1.1 %/R — with no interior trough and no second
   hump. A real Hartigan dip test would very likely **not** reject unimodality on this shape; "many
   small pokes plus a fat tail" (design.md's own phrase) describes a heavy-tailed distribution with
   its single mode at zero, which is what this is, not two separate populations. The 0.15 ratio
   threshold is a coarse proxy for "is there enough tail mass to matter for a runner", and it is
   what the design pre-registered as the tie-breaker — that is why this validation follows it rather
   than substituting a stricter definition after seeing the shape — but "bimodal" should be read as
   *that* operational claim, not as two humps.
2. **The clearance margin is thin (9% above threshold)**, and 39,562 walks are not 39,562
   independent draws — every session contributes ~180 walks that share one underlying price path
   (autocorrelated). A session-level check, largely free of that correlation, corroborates the
   pooled number rather than undermining it: of 220 sessions with a window bar, **100% reached at
   least 3R** on some entry that morning, 97.3% reached 10R, and 73.2% reached 20R. The fat tail is
   not a handful of outlier trend days carrying the pooled statistic (the July-02 2026 session, alone,
   ran a short from 30274 down over 900 points with no 10-point pullback — checked by hand, it is a
   real session, not a data hole — but sessions like it are not why the ratio clears; the tail
   recurs on nearly every session, just usually at a smaller scale).

**Verdict: NOT unimodal. Stop condition does not fire.** The pre-registered ratio test passes, and
the session-level corroboration is stronger evidence than the pooled ratio's thin margin alone would
suggest. Task 7 (the ladder) is not retired by this diagnostic.

**One number Task 7 should carry forward regardless of the verdict above:** rung 1 sits at `1.0R`
(design.md S5.2), and **50.0%** of hypothetical entries (0–0.5R + 0.5–1R) never reach 1R before the
stop. That is not a modality question — it is a direct statement that half of MeanTick's real
entries, if they resemble this tape's unconditional distribution at all, would not fill rung 1
either, before the whole ladder question is even asked. Task 7 should treat this as a live risk to
size against, not assume the entry signal's own selectivity rescues it without checking.

---

## Phase 0c — level clustering (amendment A1)

**The approximation, stated once.** MeanTick's real Gate 1 (design.md S4.1) admits three 4H array
types — fair value gap, order block (itself anchored to an FVG), and rejection block. Only the FVG
detector exists in Python today (Task 9 ports the rest). `research/cluster_histogram.py` uses
`PropSim`'s own `fvg` strategy detection formula (`engine.py`, class `FVG`, gap arrays currently
around lines 558–560 — re-verify before trusting the line numbers) as the array proxy, reimplemented
rather than called directly because `FVG.entries()` also does retrace-fill tick-scanning this
diagnostic does not want mixed into the density measurement. **This understates array density**:
order blocks and rejection blocks are *more* candidates, not fewer, so a real multi-type population
would be *tighter* (more crowded, smaller median gap) than what is reported below. Every number here
is an **optimistic bound** on clustering — i.e. a pessimistic bound on how safe it is to assume A1
is unnecessary.

Detection ran on 4H bars built from the **full, non-RTH** tape (real 4H candles run 24h; an
RTH-only tape would bucket them incorrectly at the 09:30/16:00 edges). `level` is each gap's 50%
consequent encroachment (design.md S4.1's own definition), not `FVG.entries()`'s near-edge retrace
price. "Same-direction neighbor" is searched within `FreshBars4H = 5` bars (~20h) of a candidate's
own formation bar — design.md's own freshness window, so an array outside it would no longer be a
live Gate-1 candidate anyway.

**Result, full tape:**

```
4H FVG arrays detected: 343 (202 bullish, 141 bearish)

nearest same-direction array, ALL 343 detected arrays (76 with no same-direction
neighbor within 5 bars at all):
  median gap = 103.12 pts   mean = 134.75 pts   n <= 10pt (stop) = 12 (4.5%)
      0-5pt |    6        20-30pt |    8
     5-10pt |    4        30-50pt |   25
    10-15pt |    2       50-100pt |   78
    15-20pt |    6         >100pt |  138

nearest same-direction array, JUST the 65 in-window (09:30-11:00 ET) candidates:
  median gap = 87.50 pts   n <= 10pt (stop) = 1 of 65 (1.5%)   no same-dir neighbor at all = 20
```

**Headline number: median cluster gap = 87.5 points** among the candidates MeanTick would actually
see in its own entry window — **8.75x the 10-point stop**. Only one in-window candidate in the
entire 238-session tape had a same-direction array within the stop distance.

**Outcome split** (design.md S5.1's control arm — resting limit at the array's level, 10pt stop,
`4.0R` single target, real commission + 2-tick slippage each way, via `engine.resolve()`):

```
clustered (nearest same-dir array <= stop): n=1,  win rate=0.0%
isolated  (nearest same-dir array >  stop): n=64, win rate=23.4%
```

**This split is not usable evidence.** One clustered candidate cannot support either the hazard
reading or the confluence reading — it is not a sample, it is an anecdote, and reporting a "0% vs
23.4%" comparison without saying that plainly would misrepresent what one trade can show. The FVG-only
proxy simply does not generate enough clustered candidates to test this amendment; that would need
either the full three-array-type detector (Task 9) or a materially wider stop/proximity definition,
neither of which is this task's job to invent.

**Verdict on A1: ship OFF, on the density evidence alone — not on the outcome split.** The
clustering hazard the amendment exists to guard against (two same-direction arrays close enough that
"which one gets tagged" is a coin flip against a 10-point stop) essentially does not occur in this
proxy: 95.5% of all detected arrays, and 98.5% of actual in-window candidates, have their nearest
same-direction neighbor well outside the stop. Given the approximation understates density (missing
array types would only tighten this, not loosen it), an order of magnitude of additional crowding
would be needed to threaten the 10-point stop — a real possibility once order blocks and rejection
blocks are added (Task 9), but not something this measurement can rule out or confirm further. A1
should default OFF as designed and be re-measured once the full detector exists, not flipped ON
speculatively from a single anecdote.

---

## Sample arithmetic — the second stop condition

```
total sessions in tape (RTH):                    238
sessions with >=1 in-window FVG candidate:         56  (23.5%)
total in-window candidates:                        65
after a 60/40 chronological train/test split: ~26 out-of-sample candidates
house gate (strategy-profitability-gates.md):     >=100 out-of-sample trades
```

**26 is below 100. This is the FVG-only proxy's count, not a certified count of MeanTick's real
entry rate** — the true detector (Task 9) admits two more array types and would plausibly raise
this, since order blocks and rejection blocks are additional entry paths on days the FVG proxy finds
nothing (some overlap is also expected, where more than one array type coincides on the same
session and would not have added a second candidate). But even a generous 3x multiplier for the two
missing types — 26 -> 78 — still lands short of 100. **This measurement does not clear the gate, and
there is no plausible reading of the approximation that gets it there on this tape alone.**

**Stop condition fires. Per the task brief: the answer is collecting forward Replay sessions, a
calendar commitment, not a code change.** `.claude/memory/nt8-market-replay-nrd.md` records this
machine's measured retention floor: NT8's Market Replay server serves roughly the trailing **90
calendar days** from whenever a session is requested — verified by the server's own refusal message
for older dates, not assumed. Concretely: every week that passes without recording or downloading a
session is a week of tape this project can never get back. Whatever the real Task-9 detector's
candidate rate turns out to be, closing this gap from the existing tape alone is not possible; it
needs new sessions, and the sooner collection starts the more of the current ~90-day window is still
reachable.

---

## What Task 7/8/9 should carry forward

- **Task 7 (the ladder) is unblocked by Phase 0b**, but should treat the 50% rung-1-miss rate as a
  real design input, not an incidental footnote.
- **A1 (`UseClusterSkip`) ships OFF**, per design.md's own default, with this measurement as the
  supporting (not confirming) evidence. Re-run `cluster_histogram.py` once Task 9's order-block and
  rejection-block detectors exist in Python — this file's numbers are already labelled as the
  optimistic bound they are, on purpose, so a tighter re-measurement is expected, not a
  contradiction.
- **The ≥100 out-of-sample gate is not yet reachable from data already on disk.** This is a
  scheduling fact for Javier to act on (start recording/downloading forward Replay sessions now),
  not a design decision for this repo to make.

---

## Mirror fidelity (Task 9)

**Scope.** This mirror covers design.md S7, the ladder EXIT only:
`propsim/mean_tick.py`'s `build_ladder()` ports `MtLadder.BuildLadder`
(`ninjascript/MeanTickExits.cs`), and `resolve_ladder()` calls PropSim's
`resolve()` (`PropSim/engine.py:1085`) K times on the same `entry_idx` -- one
call per surviving rung, each carrying that rung's own `contracts` slice and
its own target -- then sums the resulting Trades. It does **not** port S4's
detection (4H PD arrays, the 15-minute rejection block) to Python; that stays
C#-only, and no task in this project's plan ports it. A line in this file's
Phase 0c section anticipates re-running `cluster_histogram.py` "once Task 9's
order-block and rejection-block detectors exist in Python" -- that expectation
was set before this task's actual scope was read from the brief; Task 9 built
the exit mirror, not a detection mirror, and that re-run has no code to run
against yet.

**What is EXACT.** P&L, win rate, per-rung fill statistics (entry/exit price,
exit reason), because `resolve()` is linear in `contracts` and each rung is
resolved as its own complete, correctly-sized position from the same entry to
its own exit -- summing K exact per-leg P&Ls gives the exact combined P&L.

**What is WRONG, KNOWINGLY AND BOUNDEDLY: `intra_mdd`, and `mae` with it.**
Each of the K `resolve()` calls computes its own worst excursion over its own
window (entry to that rung's own exit) at its own rung-sized `contracts`:
`intra_mdd` as the fall from a running peak set inside that call, `mae` as the
worst print against entry seen anywhere in that call's window
(`PropSim/engine.py:1310` and `:1346-1353`). Summing the K results adds
together excursions that were each maximised **independently**, over windows
of different lengths -- the runner's window outlives rung 1's, whose own
window ends the moment rung 1's target fills. That is a sum of per-leg
maxima, not the single simultaneous worst instant the real ladder (one
position, stepping down in size as each rung closes) actually lived through,
and a sum of independently-maximised quantities can only be **>=** the true
combined figure, never <. Both figures are therefore overstated, in the same
direction, by the same mechanism -- not just `intra_mdd` as design.md S7
names, but `mae` too, for the identical reason and by the same argument (asked
of this task explicitly; the conclusion is that it applies to both, not just
the one named in the design doc).

The prop breach test is `max(hwm - balance - mae, intra_mdd)`
(`PropSim/engine.py:1067`). Since **both** of that formula's data-dependent
inputs are inflated or exact here, and neither is ever deflated, this mirror
is **conservative on breach** end to end. **It must never be used to argue
that a MeanTick variant is safe** -- only that a variant it calls unsafe is
worth checking more carefully, and that a variant it calls safe still needs a
check that does not share this reduction (a real Market Replay run, or a
future engine.py extension that tracks the position's true declining size).

This is a declared reduction, which is the house standard -- see PatternZone,
which shipped with no mirror at all and said so. An undeclared reduction is
how a false safety claim gets made; this one is written down in the module's
own docstring and here, next to any number derived from it, on purpose.

**What is UNTESTED, stated as a limitation rather than implied by omission.**
`golden_ladder.csv` holds rung SCHEDULES -- `build_ladder()`'s output -- not
resolved trades; no fixture exercises `resolve_ladder()`'s use of `resolve()`
against real tick data, because none was in scope to build (three named
fixtures were specified for the schedule, not for a resolved-trade sequence).
`resolve_ladder()` is written directly against `resolve()`'s real, current
signature and defaults `limit_px` to the rung's shared entry price, modelling
design.md S6.1's K independent limit orders resting at one price (the
behaviour the Phase 0a probe observed directly -- all three legs filled at
29840.25). That default is an assumption this validation names but has not
independently confirmed for `resolve_ladder()` itself: it is correct by
inspection against the probe's own observation and against `resolve()`'s
documented contract, not by a golden-fixture parity check. `propsim/mean_tick.py`
carries its own runnable self-check (`python3 propsim/mean_tick.py`) covering
both halves -- `build_ladder()` against a hand-computed fixture, and
`resolve_ladder()` against a synthetic, monotonically-rising tape where every
number is checkable by hand -- but a self-check that the author wrote is not
the same claim as a fixture generated independently by the C# side, which is
what the schedule parity gate below actually is.

**Parity result.**

```
dotnet run --project tests          # OK  121 checks, writes tests/golden_ladder.csv
python3 research/compare_mirror.py  # PARITY OK -- 3 fixtures, 8 rungs, all prices
                                     # within 1e-9, all quantities and flags exact
```

Both commands were run for this task, in that order, against a freshly
regenerated `golden_ladder.csv` (not a stale copy) -- exit code 0 on both.
`compare_mirror.py` hardcodes the three fixtures' INPUT parameters
(`rung1_r`, `rung2_r`, `structural_price`, `runner_price`, `contracts`,
`tick_size`, `min_rung_ticks`) verbatim from the three `MtLadder.BuildLadder(...)`
calls in `tests/ExitTests.cs`'s golden-CSV section, because the CSV itself
records only what `BuildLadder` returned, not what it was given -- there is no
way to recover the inputs from the outputs alone. **If `ExitTests.cs`'s three
calls ever change, `FIXTURES` in `compare_mirror.py` must change with them, or
the gate will silently compare against the wrong recipe.** No mismatch
occurred in this run, so no engine needed correcting; had one occurred, the
fix would have gone to whichever engine's arithmetic order differs from
`MeanTickExits.cs`'s comment ("Arithmetic order is fixed and is not to be
reassociated"), never to the 1e-9 tolerance.
