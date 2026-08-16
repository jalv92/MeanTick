<h1 align="center">MeanTick</h1>

<p align="center">
  <b>A NinjaTrader 8 strategy that rests a limit order at the midpoint of a rejection wick, and a test harness built to prove it wrong.</b><br>
  Most published trading models are demonstrated on replays of days that worked. This one ships with the measurement that says its sample is too small to conclude anything — and the code to redo it when the sample grows.
</p>

<p align="center">
  <a href="#what-it-does">What it does</a> ·
  <a href="#quick-start">Quick start</a> ·
  <a href="#how-it-works">How it works</a> ·
  <a href="#what-is-and-is-not-validated">What is validated</a> ·
  <a href="#configuration">Configuration</a> ·
  <a href="#limits">Limits</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-research-orange?style=flat-square" alt="status: research">
  <img src="https://img.shields.io/badge/instrument-MNQ-blue?style=flat-square" alt="instrument: MNQ">
  <img src="https://img.shields.io/badge/platform-NinjaTrader%208-lightgrey?style=flat-square" alt="platform: NinjaTrader 8">
  <img src="https://img.shields.io/badge/tests-160%20passing-brightgreen?style=flat-square" alt="tests: 160 passing">
  <img src="https://img.shields.io/badge/parity-C%23%20%E2%86%94%20Python-blueviolet?style=flat-square" alt="parity: C# to Python">
  <img src="https://img.shields.io/badge/license-MIT-green?style=flat-square" alt="license: MIT">
</p>

<p align="center">
  <img src="docs/assets/hero.png" width="100%" alt="Three independently-bracketed legs filling at one price in NinjaTrader Market Replay, each stepping the position down at its own target">
</p>

---

## What it does

At the New York open, MeanTick looks for one thing: a 15-minute candle that pokes into a fresh 4-hour PD array, gets refused, and closes back outside it, leaving a wick on one side only. It then rests a limit order at the **midpoint of that wick** — the mean threshold, which is what the project is named after — with a fixed 10-point stop.

The exit is where the design work went. It runs in one of two modes:

- **`SingleTarget`** — one 4R target on the whole position. This is the control arm, and it is the default.
- **`Ladder`** — four rungs: 1R, 3R, the nearest 15-minute PD array that clears rung 2, and a runner aimed at the opposing London extreme.

The ladder is not the hypothesis. It is an amendment that has to beat the control arm at the same contract count, and it is never credited with edge on its own. That distinction is deliberate: a positive backtest of both together would prove nothing about either.

<p align="center">
  <img src="docs/assets/setup.png" width="100%" alt="A 15-minute rejection candle off a 4-hour fair value gap, with the entry limit drawn at the midpoint of the rejection wick">
</p>

## Quick start

```bash
git clone https://github.com/jalv92/MeanTick.git
cd MeanTick
bash scripts/gate.sh
```

That runs everything: 160 assertions over the pure engines, the C#↔Python ladder parity check, and a real `nt8c build` of all four NinjaScript files staged as NinjaTrader would compile them.

To run it in NinjaTrader, copy the four files in `ninjascript/` into `Documents/NinjaTrader 8/bin/Custom/Strategies/` and compile. The folder is decided by the namespace, not the role — the three `MeanTickTypes/Core/Exits` files are a pure library that happens to live beside the strategy.

**Run it in Sim or Market Replay.** See [Limits](#limits) before considering anything else.

## How it works

```
                                4H series ──┐
                                            ├── a PD array within 1.5×ATR, ≤5 candles old
                                15m series ─┘
                                            │
                    09:30 ET ───────────────┤
                                            ▼
                          a 15m candle that touches the level,
                          closes back outside it, and leaves a
                          wick on ONE side only
                                            │
                                            ▼
                    ENTRY = midpoint of that wick      STOP = 10 points
                                            │
                              ┌─────────────┴─────────────┐
                              ▼                           ▼
                      SingleTarget                     Ladder
                      one 4R target            1R · 3R · structural · runner
```

Four files, and the split is the point:

| File | Role |
|---|---|
| `MeanTickTypes.cs` | bar struct, Wilder ATR, tick rounding — ported verbatim from a sibling project, mirrored in Python |
| `MeanTickCore.cs` | every detection rule: PD arrays, the rejection block, the session window |
| `MeanTickExits.cs` | the rung schedule, quantities, fallbacks |
| `MeanTickStrategy.cs` | the NinjaTrader shell — data series, an explicit Eastern-Time clock, order plumbing. **No decisions.** |

The first three contain zero `using NinjaTrader.*` and compile both inside NinjaTrader's single assembly *and* in a standalone .NET test runner. That is what makes 160 assertions possible against a platform strategy, and it is why `propsim/mean_tick.py` can mirror the ladder engine bit-for-bit — `research/compare_mirror.py` holds the two to 1e-9 on every price, and it runs inside the gate.

## What is and is not validated

**There is no validated performance result. The sample is too small to produce one.**

Everything below was measured on PropSim's continuous NQ tick tape — 238 RTH sessions, 2025-08-03 to 2026-08-04. Sample sizes are given because a number without one is decoration.

| Measured | Result | Sample |
|---|---|---|
| Out-of-sample trade count | **~26 against a house gate of ≥100 — fails** | 65 in-window candidates, 60/40 split |
| Maximum favourable excursion, fat-tail ratio | 0.164 against a pre-registered 0.15 threshold — **clears it narrowly**, and the distribution is *not* textbook bimodal | 39,562 walks |
| Entries never reaching rung 1 (1R) | 50.0% | 39,562 walks |
| Median gap to the next same-direction array | 87.5 points, 8.75× the stop — so the clustering amendment ships **off** | 65 in-window candidates |
| Independent per-leg targets in NinjaTrader | **Confirmed** — three legs filled at one price and stepped the position 3→2→1→0, each at its own target | 1 Replay session |
| Independent per-leg **stops** | **Never observed.** No stop has been hit in any run to date | — |

<p align="center">
  <img src="docs/assets/mfe.png" width="100%" alt="Maximum favourable excursion distribution: density falling monotonically from zero with a long right tail">
</p>

The full record, including the reasoning that undercuts some of these verdicts, is in [`docs/validation.md`](docs/validation.md). The design and its amendments are in [`docs/design.md`](docs/design.md).

## Configuration

Seventeen parameters, all on NinjaTrader's property grid. The ones that matter:

| Parameter | Default | Note |
|---|---|---|
| `ExitMode` | `SingleTarget` | the control arm; `Ladder` is the experiment |
| `StopPoints` | 10 | $20 per MNQ contract |
| `Contracts` | 4 | one per rung; the ladder needs at least four |
| `WickRatioMax` | 0.33 | the source model's only rule with a stated failure rate |
| `UseBreakEven` | off | our invention, not the source's — swept, not assumed |
| `UseClusterSkip` | off | measured unnecessary; see the table above |

Session boundaries and the London range are **constants, not dials** — they are mirrored in Python and must not drift. `docs/design.md` §10 lists every parameter and says which are swept.

## Limits

- **Sim and Market Replay only.** The out-of-sample gate is unmet and the stop side has never been observed. Both facts are recorded rather than worked around.
- Most prop firms breach on *unrealised* drawdown, which penalises the runner specifically. The runner's own policy is our invention — the source model states one sentence about it and contradicts itself elsewhere.
- The Python mirror deliberately overstates `intra_mdd` and `mae`, making it **conservative on breach**. It must never be used to argue a variant is safe.
- The strategy mechanises one of three level-selection frameworks the source teaches. The other two are named and deferred in `docs/design.md` §11.
- `probe/LadderProbe.cs` is a throwaway measuring instrument, kept deliberately: half its question is still open.

## License

MIT. See [LICENSE](LICENSE).
