# MeanTick — design spec

A NinjaTrader 8 strategy for MNQ. It rests a limit order at the **mean threshold** — the 50% — of a
PD array at the New York open, protects it with a fixed 10-point stop, and (the part that is new to
this workspace) exits through a **ladder of partial take-profits plus a runner**.

Status: design approved, no code written. Version 1 scope only.

---

## 1. What and why

The model comes from a publicly taught ICT-derived method ("top & bottom ticking", RICH KO on
YouTube, 6 videos Nov-2025 → Jul-2026). The full extraction and a critical read of the evidence live
outside this repo, in the private workspace at `docs/research/richko-turtle-soup-study.md`, with the
six timestamped transcripts under `docs/research/richko-transcripts/`. Citations below are of the
form `V4 [7:04]` — video number and transcript timestamp.

The source teaches three different ways to *choose the level*. All three share one execution core,
unchanged across eight months of videos: a resting limit at the 50% of a PD array, a fixed 10-point
stop, an R-multiple target, and no chasing. **MeanTick mechanizes exactly one of the three** — the
newest and most specified, the 4H PD array → 15-minute rejection block (V1, Jul-2026). The other two
are out of scope for v1 (§11).

### The hypothesis, written down before anything is measured

PropSim already contains a strategy called `fvg` (`PropSim/engine.py:524-584`): a resting limit on an
FVG retrace with a 40-tick stop. It was one of seven configs killed in the pre-registered NQ search of
2026-08, for *"fixed 40t stops inside 1-min noise + cost load"*. Subtracting it, MeanTick contributes
exactly three deltas:

1. the limit rests at the **50%** of the array, not at its near edge;
2. the array is a **15-minute rejection block** (entry at the midpoint of its wick), qualified by a
   4H PD array — not a bare FVG;
3. the exit is a **ladder**, not a single target.

> **H (pre-registered):** the 50% of the rejection wick of a 15-minute candle that rejects a fresh 4H
> PD array is a level with edge on MNQ, with a 10-point stop, in the 09:30–11:00 ET window.

Deltas 1 and 2 are the hypothesis. **Delta 3 is not.** The ladder is an amendment whose only question
is whether it beats a single target at the same contract count. It is never credited with edge, and it
always runs against the control arm (§5.1). This distinction exists because a positive backtest of the
whole bundle would prove nothing about any of its parts, and because `LatigoBreak` already
demonstrated that truncating winners while leaving losers full-size can invert a +$28,333 result
into −$2,815.

---

## 2. Prior evidence this stands on — and what it does not

**Stands on.** The execution core is internally consistent across all six source videos: resting
limit at the 50% of a level, fixed 10-point stop, R-multiple target, primary order at full risk plus a
backup at half risk, no chasing. There is no contradiction anywhere in the corpus on any of those.
The single-sided-wick filter is the one rule the source quantifies — *"it might play out maybe two out
of 10 times, the other eight out of 10 it is going to fail"* (V1 [3:16]) — which makes it directly
falsifiable.

**Does not stand on.** Every example in the corpus is a retrospective replay on charts marked up after
the outcome was known. In V4 the trader admits he was not at the screen for two of the three setups he
presents, and counts them as wins for the model. The only loss shown in a tutorial (V3) is at half
risk and is offset by a 7R win in the same video. There is no trade log, no timestamped fill, no
equity curve, no sample size. The "90% win rate" of one video title is the win rate of the examples
its author chose to publish. **No claimed statistic from the source is carried into this design.** The
source supplies rules; this repo supplies the evidence.

**Two source claims are load-bearing and both are soft.** The stop is *"a 10 to 15 point stop"*
(V5 [11:18]), not 10. And the target is 1:3 in V5, "a fixed 1 to 4" in V1, and "a 4R *this time*" in
V6 — the last phrasing proving the target is chosen per trade. Since every ladder rung is a multiple
of the stop, stop size and rung table are coupled: any later sweep must be a 2-D grid, never two
independent 1-D sweeps.

---

## 3. Architecture

```
ninjascript/MeanTickTypes.cs      pure   bar struct, Wilder ATR, pivots, tick rounding, scalar helpers
ninjascript/MeanTickCore.cs       pure   4H PD arrays, 15m rejection block, entry price, day filters
ninjascript/MeanTickExits.cs      pure   rung schedule, quantities, break-even, runner
ninjascript/MeanTickStrategy.cs   shell  data series, ET clock, order plumbing, telemetry
tests/                                    assert harness + golden CSV fixtures
propsim/mean_tick.py                      the Python mirror
research/                                 parity harness + the Phase-0 diagnostics
```

The three pure files carry every decision. They contain **zero `using NinjaTrader.*`**, live in
namespace `MeanTickCore`, and are **C# 7.3 only**. That is not stylistic: NT8 compiles everything under
`bin/Custom` into one assembly and ships its own types under common names, so a NinjaTrader `using`
here is how a CS0101 duplicate-type clash starts. Purity is also what lets the same files compile in
NT8 *and* in `tests/MeanTick.Tests.csproj`, which has no NinjaTrader assemblies on its reference path.
Behaviour must be identical in both.

Expect `nt8c` to report **CS0246 on the shell's `using MeanTickCore;` line. It is a false positive**
(the same one is annotated at `VeeSnapStrategy.cs:58`). Do not "fix" it by collapsing namespaces.

### What is ported, not written

| Piece | Source | Treatment |
|---|---|---|
| `MtBar`, `MtSwing`, Wilder ATR, pivot detector, `RoundToTick`, scalar helpers | `VeeSnap/ninjascript/VeeSnapTypes.cs` (whole file, 161 lines) | port verbatim, `Vs`→`Mt`, namespace `MeanTickCore`. Zero new logic on day one. |
| 3-bar fair value gap detector | `VeeSnap/ninjascript/VeeSnapCore.cs:455-467` | port verbatim; it already has a Python mirror and tests |
| "rest a limit at 50% of a zone" | `VeeSnap/ninjascript/VeeSnapCore.cs:1210-1232`, `RetestFillPct = 0.50` | port; it *is* the mean threshold |
| Fixed 40-tick (10-point) stop | `VeeSnap/ninjascript/VeeSnapExits.cs:43` | port the constant and its MNQ framing |
| Multi-series fold with absolute-index anti-lookahead | `VeeSnap/ninjascript/VeeSnapStrategy.cs:314-315`, `:698-715` | port the pattern |
| Wall-clock TTL cancel of a resting order | `VeeSnap/ninjascript/VeeSnapStrategy.cs:755-791` | port the pattern, retimed to the session window |
| Assert harness, `CheckBits`, golden-CSV parity | `VeeSnap/tests/Program.cs:8-53`, `:26-33`, `ExitTests.cs:786-798` | port whole |
| K independent entry signals, each with its own bracket | `TraderClaudeV2` (2-leg version) | adapt to K legs — §6 |

`MtMath.RoundToTick` is mandatory for every price computed in the core.
`Instrument.MasterInstrument.RoundToTickSize` must never touch a core price: the Python mirror cannot
call it, and a disagreeing rounding drifts the two engines one tick at a time.

### What does not exist anywhere and is genuinely new

Order blocks, rejection blocks, OTE by that name, and **the ladder — in C# and in Python alike**. All
six comparable house strategies exit with one stop and one target at full position size. Beware one
false friend when grepping: `VeeSnapCore.cs:590-635 UpdateRejections` is about a *zone* being
penetrated and reclaimed, not an ICT rejection block.

---

## 4. Detection (`MeanTickCore`)

Three gates, evaluated on closed bars only. All prices rounded with `MtMath.RoundToTick` before any
comparison, never after.

### 4.1 Gate 1 — a live 4H PD array

Admitted array types on the 240-minute series:

- **4H fair value gap** — the ported 3-bar detector. Level = consequent encroachment, 50% of the gap.
- **4H order block** — **defined through the FVG, not through a displacement threshold.** A bullish
  order block is the last down-close candle immediately preceding the 3-candle sequence that produced
  a bullish fair value gap; bearish is the mirror. Level = mean threshold, 50% of that candle's
  high-low range.
  This definition is deliberate. The conventional wording — "the last opposite candle before a
  displacement leg" — smuggles in a free parameter, because "displacement" has no agreed threshold and
  three different ad-hoc versions of it already exist in this tree. Anchoring the order block to the
  gap the move left behind reuses the detector that is already ported and tested, and removes the dial
  entirely. The same definition is used for the 15-minute order block in §5.2.
- **4H rejection block** — a 4H candle with a one-sided wick, measured as in §4.3. Level = 50% of the
  wick.

Two qualifiers, both from V1 [0:56–1:24]:

- **Freshness:** the array must have formed within the last `FreshBars4H = 5` 4H candles. The source
  says *"the most recent four or five 4H candles"* and warns against *"a PD array from 3 weeks ago"*.
- **Proximity:** `|price − level| ≤ ProximityAtrMult × ATR(14) on the 4H series`, default `1.5`. The
  source's phrase is *"within a reasonable distance"* and it is never defined. This constant is our
  invention. It is **pre-registered and not swept in the first pass** — sweeping an undefined
  constant is how the 2026-08 funnel produced fourteen corpses.

If no qualifying array exists at 09:30, wait for the 10:00 ET 4H candle open, which frequently creates
one, and re-evaluate. If still none, the day is a no-trade.

### 4.2 Gate 2 — a 15-minute rejection block off that array

A 15-minute candle qualifies when all three hold:

1. its range touches the 4H level,
2. it closes **outside** the level, in the rejection direction,
3. it leaves a rejection wick on the correct side — upper wick when bearish, lower when bullish.

### 4.3 Gate 3 — the wick must be one-sided

```
bearish:  reject = High − max(Open,Close)      opposite = min(Open,Close) − Low
bullish:  reject = min(Open,Close) − Low       opposite = High − max(Open,Close)

qualifies  ⟺  reject ≥ MinWickTicks · tick   AND   opposite / reject ≤ WickRatioMax
```

Defaults: `MinWickTicks = 8` (2 points — a wick smaller than that is noise, and its 50% is inside the
spread), `WickRatioMax = 0.33`.

A candle with wicks on both sides is a doji, or both liquidity pools have already been taken, and the
source vetoes it outright: *"it might play out maybe two out of 10 times, the other eight out of 10
times it is going to fail"* (V1 [3:16]). This is the only rule in the corpus with a stated failure
rate, so it gets its own test asserting both the accept and the reject branch.

### 4.4 Entry price — the midpoint of the wick, not of the candle

```
bearish:  entry = max(Open,Close) + (High − max(Open,Close)) × 0.5
bullish:  entry = min(Open,Close) − (min(Open,Close) − Low) × 0.5
```

This is the whole model in one line, and it is the reason the project is called MeanTick. Note that it
is the wick's midpoint — the source measures it with a Fibonacci tool on the wick alone (V1 [5:09],
[7:32]), not on the candle body or range.

### 4.5 Session, arming, and expiry

- Window: **09:30–11:00 ET**, one trade per day, hard latch.
- The window covers both the open and the 10:00 4H candle, which the source treats as an explicit
  second attempt (V1 [1:52], V6 [2:49]). V6 shows the full discipline: one attempt, check 10:00, leave.
- An unfilled entry limit is cancelled at the window close (`EntryTtlMinutes` from placement, default
  90, whichever comes first). The only source statement is a preference — *"I don't like to sit here
  too long waiting for it to reach"* (V5 [7:32]) — so the TTL is ours.
- The clock is **explicit Eastern Time**, not `barStart.TimeOfDay`. VeeSnap gets away with the naked
  version only because this machine's NT8 displays ET. MeanTick is a model of one clock instant; a
  chart in another timezone would arm it at the wrong minute with no symptom other than no trades or
  bad ones. The Python mirror uses explicit ET constants, so without this the two engines diverge by
  an hour twice a year and the parity gate would blame the engine.

### 4.6 Amendment A1 — clustering skip (parameter, default OFF)

If the next PD array of the same direction lies within `ClusterSkipPoints` (default = the stop, 10) of
the chosen level, skip the trade: the stop is narrower than the spacing between the two candidates, so
the trade is a coin flip on which one gets tagged first.

This is the failure the source diagnosed on his own money — *"was I freaking out like, oh man, I picked
the wrong order block?"* (V6 [2:20]) — after price wicked through his level and delivered from the one
below. It is also the reverse of a claim he makes elsewhere: a coincident level is *"a double level…
a lot more confluence"* (V3 [4:22]). Both readings are testable and they are opposites, which is
exactly why the parameter ships OFF and is measured (§9, Phase 0c) rather than assumed.

---

## 5. Exits (`MeanTickExits`)

`R` = the stop distance = `StopPoints` (default 10) = $20 per MNQ contract.

### 5.1 Baseline mode `SingleTarget` — the control arm

One target at `4.0R`, full position, one stop. This is what the tutorials actually teach (V1 [3:16],
[5:38]) and it is the arm every ladder result is reported against. Without it, no ladder number means
anything.

### 5.2 Mode `Ladder` — K = 4

| Rung | Qty | Price | Kind |
|---|---|---|---|
| 1 | 25% | entry ± 1.0R | fixed — pays for the trade |
| 2 | 25% | entry ± 3.0R | fixed — the V5 "bread and butter" target |
| 3 | 25% | first opposing PD array on the 15m series | structural |
| 4 | 25% | runner — see §5.3 | structural + trail |

The mix is deliberate. The two fixed rungs reproduce the two targets the source actually teaches and
need zero new detection. The two structural rungs implement the reason he gives for taking partials at
all: *"I took partials throughout here just in case we didn't make it to that gap"* (V4 [7:33]) — the
rungs insure a far magnet that may not be reached.

**Rung 3 fallback.** If no valid opposing 15m array exists, rung 3 falls back to a fixed `4.0R`. Without
this rule a day with no clean level leaves the rung orphaned and the ladder silently becomes K=3.

**The rung level universe is closed.** Across the recaps the source targets thirteen different level
types. Rung 3 admits exactly two: a 15-minute fair value gap (entry at its consequent encroachment) or
a 15-minute order block (entry at its mean threshold), whichever is nearer in the trade's direction. An
open universe would always find a rung a few points away and the ladder would fit the tape rather than
test it.

**Minimum rung spacing.** A rung within `MinRungTicks = 15` of the previous one is dropped and its
quantity folded into the next rung. Below that spacing a rung does not clear the house's ≥$5/contract
bar and is pure commission.

### 5.3 The runner

The runner has no source policy at all. One sentence in six videos — *"sometimes I'll leave a runner,
aim for higher"* (V5 [11:48]) — contradicted by V3's terminal 7R and by V4 leaving one of six contracts
unaccounted for. Everything here is ours and is labelled as such:

- **Target:** the opposing London extreme. London is **02:00–05:00 ET**, the source's definition
  (V5 [5:09]). This deliberately diverges from both house implementations — VeeSnap uses 03:00–09:30
  (`VeeSnapStrategy.cs:79`, `:635`), Apertura4HMSS uses 03:00–05:00 (`Apertura4HMSS.cs:463`). The
  divergence is intentional and recorded here so a future reader does not "harmonize" it.
- **Trail:** by 1-minute swing, not by ATR. A trade that resolves in two minutes never moves an ATR,
  so an ATR trail would be inert exactly when it is needed.
- If the runner is still open at `RunnerCutoffEt` (default 12:00 ET), it is flattened at market.

### 5.4 Break-even (parameter, default OFF)

When rung 1 fills, the surviving legs move their stop to entry.

This is our invention and the corpus contradicts itself on it: V4 moves to break-even once the entry
candle closes with space (V4 [6:36]), while V6 rides +1R all the way back to a full stop under an
explicit no-management doctrine — *"you just set your levels and let price do what price does"*
(V6 [2:20]). V6's own −$980 would have been a scratch under the V4 rule. That makes it worth a
parameter and worth measuring; it does not make it a rule of the model. It ships OFF.

---

## 6. NT8 order plumbing (`MeanTickStrategy`)

### 6.1 The ladder is built from K entry signals, not K exit orders

`EntriesPerDirection = K`. Each rung is its own entry signal — `MT_R1 … MT_R4` — submitted as a
separate limit order at the *same* entry price with its own quantity, and given its own bracket via
`SetStopLoss(signalName, …)` / `SetProfitTarget(signalName, …)`.

The reason is a documented hazard. NT8 treats a live exit order as protecting *the position* and
resizes it to whatever remains: once any partial close happens, later exit orders take the entire
remaining position (`nt8-educational/reference/historical_order_backfill_logic.md:136-141`, and
`nt8-strategy/reference/managed_approach.md:138` describes the same semantics in realtime). Under an
N-exit-orders design, rung 2 would not sell its allocated contract — it would sell the remainder, and
the runner would never exist. The docs' only worked example of partial exits is sequential
`ExitLong(1)` calls, never N simultaneous resting limits.

With K independent entry signals there are no partial exits: each leg is a complete position with a
complete bracket, so the resize cannot occur, and per-leg break-even is free. `TraderClaudeV2` already
runs this pattern in this workspace at K=2.

A sequential design — submit rung k+1 only when rung k fills — was considered and rejected. It keeps
only two live orders and would reuse the existing bracket machinery unchanged, but this model's
signature is delivering 8R in under two minutes (V1 [8:01], V5 [12:17]); a move like that clears three
rungs before the second could be submitted, so the design would systematically miss rungs on exactly
the trades that pay.

**This is measured before anything is built** — see §9, Phase 0a. If the probe shows the legs collapse
even under this design, the entire exit architecture changes and this section is rewritten.

#### Phase 0a result (2026-08-16)

`probe/LadderProbe.cs` run live in NT8 Market Replay, NQ, entry 29840.25:

```
6:44:00 PM EXEC PR1           qty=1 px=29840.25 | position=Long 1
6:44:00 PM EXEC PR2           qty=1 px=29840.25 | position=Long 2
6:44:00 PM EXEC PR3           qty=1 px=29840.25 | position=Long 3
6:50:00 PM EXEC Profit target qty=1 px=29845.25 | position=Long 2
6:52:00 PM EXEC Profit target qty=1 px=29850.25 | position=Long 1
7:08:00 PM EXEC Profit target qty=1 px=29860.25 | position=Flat 0
```

All three legs filled at one price, building the position to Long 3. Each target price
matches only its own leg's rung — 29845.25 (+5 pts = 20 ticks, PR1), 29850.25 (+10 pts = 40
ticks, PR2), 29860.25 (+20 pts = 80 ticks, PR3) — and each target execution stepped the
position down by exactly qty=1: 3 → 2 → 1 → 0.

**Verdict: PARTIAL.** Target-side independence is CONFIRMED: had NT8 resized surviving exit
orders to the remaining position, the first target to fill would have closed all 3 (or the
second would have closed the remaining 2), and the position would have jumped straight to
Flat. It did not. PR3 stayed working for 16 minutes after PR2 closed and filled at its own
80-tick target — the runner surviving independently of its siblings, which is the behaviour
the whole ladder design exists for, now observed rather than assumed. Stop-side independence
is **UNOBSERVED** — see Phase 0a round 2 below, which turned out to measure a manual-order
artefact rather than the strategy. **§6.1's K-independent-entry-signals architecture remains
unvalidated on the stop side** until a run where the strategy itself holds a stop is captured.

Two limits of this evidence, stated so they aren't overclaimed:
- **No stop was hit in *this* run, or in any run so far.** Target-side independence is
  confirmed; the strategy's own stops have never once been observed to fill or survive a
  sibling's close — see Phase 0a round 2 below.
- **This was a Replay/realtime run.** NT8's documented exit-quantity resize
  (`nt8-educational/reference/historical_order_backfill_logic.md:136-141`) is scoped to
  *historical backfill*, which per `historical_order_backfill_logic.md:7` covers TWO
  scenarios — the Strategy Analyzer, and a live running strategy's historical catch-up at
  startup — not the Analyzer alone. Neither is exercised by a pure Replay/realtime run;
  both remain open and must be checked before any Analyzer or live-startup result on the
  real strategy is trusted.

**Task 7 is UNBLOCKED; Task 8 remains BLOCKED until the stop-side probe (round 3, below)
passes.** `MeanTickExits.cs` is a pure rung-schedule builder — a target-side claim, and
target-side independence is confirmed by run 1. The stop architecture lives in Task 8's
order plumbing, which is exactly what round 2 failed to measure.

#### Phase 0a round 2 (2026-08-16, MNQ 09-26)

A second Replay run, MNQ 09-26, exposed what read on the chart as a "3-lot stop loss"
vanishing after a target filled. **Javier confirms the strategy never opened a trade in this
run — every order on that chart was placed by him, by hand.** The run therefore measures
**nothing** about the strategy's own bracket lifetime. What it does document: a hand-placed
3-contract stop under Chart Trader's OCO grouping was cancelled when his own manually-placed
target filled — ordinary manual-order OCO behaviour, not a platform or strategy defect.

**Operating rule, because this cost a session: never hand-place or drag orders against a
running strategy's position.** The strategy does not see manual orders, and its position
accounting fights with them. Mixing manual orders into a managed strategy's live position is
not a supported way to observe that strategy's own behaviour.

The chart alone could not settle *whose* stop vanished — at one identical stop price, three
1-lot stops and one 3-lot stop (manual or strategy-placed) render identically. This is the
same-price rendering confound that round 3's instrumentation (distinct 40/41/42-tick stop
distances, `OnOrderUpdate` logging `Order.Oco`, and market-order entries so the probe
actually trades instead of waiting on a limit that never fills) exists to remove.

#### Proposed alternative (pending the stop-side probe)

Not yet decided. §6.1's body above describes the design **as originally proposed**, and
Phase 0a round 2 reopens whether it survives a partial close. A candidate replacement, to be
settled by round 3's `PooledStopTest` mode (`probe/LadderProbe.cs`): one pooled,
`""`-scoped `ExitLongStopMarket` for the whole position, plus K per-leg `ExitLongLimit`
targets — all `Exit*` methods, no `Set*` calls. It is attractive because §5 already gives
every rung the *same* stop price and §5.4 moves all legs to break-even together, so K
per-leg stops would be K orders all carrying one number; a single pooled stop says the same
thing once. This replaces §6.1's design only if the probe confirms the pooled stop is not
itself OCO-killed by a per-leg target fill — otherwise it fails for the same reason the
original per-signal design remains unverified: NT8 never documents OCO's grouping key, so
any stop's survival under a partial close is unconfirmed until measured directly.

Round 1 (and the strategy-side portion of round 2, which never ran) used `StopTargetHandling`
set in `SetDefaults` to `PerEntryExecution`. That setting was likely a no-op:
`stoptargethandling.md:11` documents `PerEntryExecution` as NT8's own default, so pinning it
in code changed nothing relative to an unset default. Worse, `managed_approach.md:131` states
the *effective* value is read from the Strategies-window "Stop & target submission" property,
which overrides whatever `SetDefaults` sets — so round 1's actual runtime value was never
confirmed by the code at all. Round 3 adds a `State.DataLoaded` print of the live
`StopTargetHandling` value for exactly this reason: it is the only way to know what actually
ran.

`probe/` is deliberately **not deleted**: the stop side has never been observed — round 1
never hit a stop and round 2 measured a manual-order artefact instead of the strategy — and
round 3 instruments the probe (market entries, distinct stop distances, `Order.Oco` logging,
the pooled-stop alternative) to make that observation for the first time. It stays in the
tree until both the stop-side and Analyzer questions are closed.

### 6.2 Data series

Primary 1-minute, plus `AddDataSeries` 15-minute and 240-minute. Order of the calls is the index —
port the guard (`if (BarsInProgress != 0 || CurrentBar < 0) return`) and the absolute-index
anti-lookahead fold from `VeeSnapStrategy.cs:698-715` verbatim. All orders go to
`barsInProgressIndex 0`.

### 6.3 Known traps carried in from the workspace

- Set in-flight flags **before** any `Enter*`/`Exit*` call, never after — the order-event race is
  documented in `.claude/memory/nt8-order-event-race.md`.
- Never submit orders from the `OnMarketData` thread.
- A cancel-replace of one leg kills its OCO partner. Whether per-signal brackets actually contain
  that to a single leg is **unverified** — it has never been tested; Phase 0a round 2 (§6.1)
  attempted to but measured a manual-order artefact instead of the strategy. Round 3 is the test.

---

## 7. The Python mirror and the parity gate

`PropSim`'s `resolve()` (`PropSim/engine.py:1085-1088`) takes a scalar stop and a scalar target and
produces exactly one `Trade` per entry. It cannot express partial exits, and extending it would touch
the ledger and fingerprint machinery of an already-validated project.

Instead, `propsim/mean_tick.py` calls `resolve()` **K times on the same `entry_idx`**, each call
carrying that rung's `contracts` slice and its own target, then sums the resulting trades.

- **Exact:** P&L, win rate, per-rung fill statistics, exit reasons.
- **Wrong, knowingly and boundedly:** `intra_mdd`. Each call computes its own fall-from-peak against a
  full-size position, so the summed figure overstates the runner's intra-trade excursion. The prop
  breach test is `max(hwm − balance − mae, intra_mdd)`, so MeanTick's mirror is **conservative on
  breach and must not be used to argue a variant is safe.** This is a declared reduction, which is the
  house standard, not a bug to be discovered later.

Parity is pinned the house way: a `golden_ladder.csv` written by the C# test run and read back by the
mirror at 1e-9, with three fixtures — rung 1 only, all rungs, and a stop-out between rungs. Bit
equality (`CheckBits`) is used for every arithmetic result, because a test that tolerates a last-bit
difference tolerates the drift it exists to catch.

---

## 8. Account risk

MNQ is **$2.00 per point**, $0.50 per tick. The 10-point stop is **$20 per contract** — the source's
NQ risk was exactly ten times that, so none of his dollar figures transfer.

| Size | Risk/trade | Straight stops inside a Lucid 50K DLL ($1,200) |
|---|---|---|
| 4 MNQ (1/rung) | $80 | 15 |
| 8 MNQ (2/rung) | $160 | 7 |

v1 runs **4–8 MNQ on a 50K evaluation**. Two consequences the strategy must respect:

1. **The ladder changes the payoff distribution, not the risk.** Maximum loss is unchanged: all four
   legs share one entry price and one stop distance. Rungs cannot reduce drawdown; they can only
   trade tail upside for hit rate. Any argument that the ladder is "safer" is wrong.
2. **Four of five prop firms breach on unrealized equity.** A runner held far beyond the first targets
   raises the high-water mark on trailing-drawdown variants and can breach on the giveback. The runner
   logic must know which account it is on; see `.claude/memory/prop-firm-drawdown-two-axes.md`. Lucid's
   non-Daily `breach_basis` is still unverified, and `PropSim` stores `micro_ratio` but never applies
   it — so every mini↔micro cap conversion is done by hand until that is fixed.

---

## 9. Validation plan

Two experiments run **before** any strategy code, because both can invalidate the design rather than
merely tune it.

**Phase 0a — the plumbing probe.** A throwaway 3-rung strategy in Market Replay, watching the Orders
tab as rung 1 fills. Question: do the surviving legs keep their own quantity, or does NT8 resize them
to the remaining position? Roughly half an hour of work, and §5 and §6 both hang on the answer. If the
legs collapse, Strategy Analyzer is dead for this project and Market Replay forward becomes the only
gate — a schedule decision, not a code decision.

**Phase 0b — the MFE diagnostic.** On the existing tape, the distribution of maximum favourable
excursion for 09:30–11:00 entries with a 10-point stop. **If it is unimodal, rungs destroy expectancy
by construction and the ladder is not built.** If it is bimodal — many small pokes plus a fat tail —
the rungs pay. No strategy code required; the cheapest experiment in the project and the only one that
can retire the ladder before it is written.

**Phase 0c — the clustering histogram.** For every 09:30 candidate, the signed distance from the chosen
limit to the next-nearest array of the same direction, histogrammed against the 10-point stop. If the
median gap is under ~10 points, *which* array is chosen sits inside the noise of the stop, and no
backtest of "the 50% of the chosen array" can distinguish the model from tape-fitting. Decides whether
A1 (§4.6) ships ON, OFF, or inverted as a confluence filter.

**Sample arithmetic, to be done now and not at the end.** MeanTick fires at most 1–2 times per session.
The house gate needs ≥100 out-of-sample trades. PropSim holds 275 sessions of real ticks; after
removing no-setup days and an honest train/test split, the count may not reach 100. If it does not,
the answer is collecting forward Replay sessions — a calendar commitment, decided before building, not
discovered after. `.claude/memory/nt8-market-replay-nrd.md` records the retention floor on this machine
and must be consulted as part of this arithmetic.

**Gates.** `.claude/memory/strategy-profitability-gates.md` is the bar, out-of-sample only, and the
2026-08 ledger stands at 36 rows — any new claim on this tape needs |t| > 3.2.

---

## 10. Parameters (closed list)

Anything not on this list is a constant in code, not a dial.

| Parameter | Default | Swept in pass 1? |
|---|---|---|
| `StopPoints` | 10 | no — but coupled to the rung table; any later sweep is a 2-D grid |
| `SessionStartEt` / `SessionEndEt` | 09:30 / 11:00 | no |
| `MaxTradesPerDay` | 1 | no |
| `EntryTtlMinutes` | 90 | no |
| `FreshBars4H` | 5 | no |
| `ProximityAtrMult` | 1.5 | no — pre-registered, our invention |
| `MinWickTicks` | 8 | no |
| `WickRatioMax` | 0.33 | yes — it is the source's own falsifiable filter |
| `ExitMode` | `SingleTarget` | yes — this *is* the ladder experiment |
| `SingleTargetR` | 4.0 | no |
| `RungR1` / `RungR2` | 1.0 / 3.0 | yes, as a table |
| `Rung3FallbackR` | 4.0 | no |
| `MinRungTicks` | 15 | no |
| `Contracts` | 4 | no (8 as a second arm) |
| `RunnerCutoffEt` | 12:00 | no |
| `UseBreakEven` | OFF | yes |
| `UseClusterSkip` (A1) | OFF | decided by Phase 0c, not swept blind |
| `LondonStartEt` / `LondonEndEt` | 02:00 / 05:00 | no |

---

## 11. Out of scope for v1

- **Framework A** (session-profile classification + London range + discount/premium PD array, V5) and
  **Framework C** (OTE golden zone off a 15m displacement, V2/V6). Both are real parts of the source
  model and both are candidates for v2 as alternative level selectors behind the same execution core.
  Neither is built until Framework B has a verdict.
- The half-risk backup limit at a second level (V5 [12:47–13:42]). It interacts with the ladder's
  quantity accounting and would confound the K=4 result.
- Daily/weekly PD arrays, breaker blocks, new day opening gap, relative equal highs. They appear in the
  source's live recaps but never in a stated rule, and every one of them widens the rung universe.
- Any time-based exit. The "you'll know in under a minute" language is descriptive, is falsified by V6
  (filled → +1R → full stop) and by V4's eleven-minute trade, and has no rule behind it.
- A shared ICT detection library across repos. Each project owns its namespace to avoid CS0101; FVG is
  already implemented four times in this tree and that duplication is structural, not accidental.

---

## Amendments

_(none yet — A1 in §4.6 is designed but ships OFF and is not an amendment until Phase 0c decides)_
