// MeanTickStrategy -- the NT8 shell for MeanTick. Holds NO decision: every rule lives in
// MeanTickCore.cs / MeanTickExits.cs (pure, unit-tested, mirrored in Python). This file is
// plumbing only -- series, an explicit Eastern-Time clock, order submission, and telemetry.
// Spec: docs/design.md, sections 4-6 and 10 (the closed parameter list).
//
// CHART REQUIREMENTS:
//   * Primary series = 1 Minute (every gate in the core counts 1m bars and 15m/240m folds
//     off it). DataLoaded warns, non-blocking, if it looks different.
//   * An ETH template is RECOMMENDED, not required: the runner leg's target (the opposing
//     02:00-05:00 ET London extreme, spec 5.3) needs pre-market bars. On an RTH-only
//     template the London range stays NaN and MtLadder.BuildLadder's own NaN-guard falls
//     back gracefully (spec 5.2's Rung3FallbackR-style logic) -- nothing crashes, the ladder
//     just runs one leg shorter on its far end.
//
// THE ET CLOCK is the single most important part of this file (design.md 4.5). MeanTick is a
// model of one clock instant (09:30 ET) plus a 10:00 ET second attempt; a chart in another
// display timezone would arm it at the wrong minute with NO symptom besides no trades or bad
// ones, and the Python mirror uses explicit ET constants -- a naked TimeOfDay here would let
// the two engines diverge by an hour twice a year while the parity gate blamed the engine.
// Session/London boundaries are NOT exposed as properties here on purpose: they live as
// `const`s in MtSession (MeanTickCore.cs) so the two engines cannot drift apart dial by dial.
//
// ORDERS: K independent entry signals (MT_R1..MT_RK, one per rung), each with its OWN bracket
// via SetStopLoss/SetProfitTarget -- never K exit orders on one position. NT8 resizes a live
// exit order to the whole remaining position after a partial close (nt8-educational/reference/
// historical_order_backfill_logic.md:136-141), which would make rung 2 sell the runner's
// contracts too. TraderClaudeV2 runs this pattern at K=2; Phase 0a (design.md 6.1) measured
// target-side independence at K=3 live before this file was written.
//
// THE ORDER-EVENT RACE RULES THIS FILE (.claude/memory/nt8-order-event-race.md): NT8 can
// deliver OnOrderUpdate/OnExecutionUpdate synchronously, in-stack, BEFORE the Enter*/Exit*
// call that caused them returns. Every in-flight tracker's KEY is written before the submit
// that makes it true (a placeholder if the value itself is not known yet), and every clear
// is checked against what an in-stack echo may have already done, never assumed absent.
//
// nt8c reports CS0246 on `using MeanTickCore;` below. FALSE POSITIVE, annotated at
// VeeSnapStrategy.cs:58 -- do not "fix" it by collapsing namespaces.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using MeanTickCore;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class MeanTick : Strategy
    {
        #region Constants and identity

        // Series indices. The AddDataSeries call order in State.Configure IS the index.
        private const int SeriesPrimary = 0;   // 1-minute, where every order is submitted
        private const int Series15M     = 1;
        private const int Series4H      = 2;

        private const string RungSignalPrefix = "MT_R";
        private const string SigCutoff         = "MT_Cutoff";

        // ATR(14) on the 4H series is spec 4.1's own constant, not a dial -- it is not on the
        // §10 closed parameter list.
        private const int Atr4HPeriod = 14;

        // Implementation-only scan bounds. NOT spec dials: design.md's §10 closed list has no
        // parameter for either window, so these stay hardcoded rather than becoming an
        // ungoverned property. ponytail: generous headroom over what detection actually needs
        // (FreshBars4H default 5, so ~8 bars) -- raise if a live FreshBars4H sweep exceeds it.
        private const int Bars4HWindow  = 40;
        private const int Bars15MWindow = 300;

        #endregion

        #region Fields

        private WilderAtr _atr4H;
        private readonly List<MtBar> _bars4H  = new List<MtBar>();   // absolute-index fold, newest last
        private readonly List<MtBar> _bars15M = new List<MtBar>();
        private int _fold4H = -1, _fold15 = -1;                      // last-folded index into BarsArray[...]

        private double _londonHigh = double.NaN, _londonLow = double.NaN;

        private int    _tradesToday;
        private bool   _cutoffFlattening;   // set BEFORE the runner-cutoff Exit* call -- suppresses
                                             // the bracket-death guard for the sibling-rung cancels
                                             // that flatten cascades into (see CheckRunnerCutoff)
        private double _entryPrice;
        private int    _placedEtSec;
        private bool   _planCounted;   // has THIS plan already incremented _tradesToday? set
                                        // false on arm, true on the plan's first entry fill --
                                        // without it, a K-rung Ladder fill increments once per
                                        // RUNG, so one Ladder setup could consume up to K of the
                                        // day's budget while a SingleTarget setup consumes 1

        // "Armed" is derived, not tracked: _restingOrders.Count > 0 means unfilled entry
        // limits are still working. A separate bool invites exactly the kind of stale-flag
        // bug nt8-order-event-race.md documents; deriving it removes the whole class of bug.
        private readonly Dictionary<string, Order> _restingOrders = new Dictionary<string, Order>();
        // Rungs whose bracket is still live -- i.e. armed-but-unfilled OR filled-and-open.
        // Removed the moment a rung's own exit (stop/target) fills or its unfilled entry is
        // cancelled/rejected. Used to scope the bracket-death guard PER RUNG (see
        // OnOrderUpdate) and to find the "surviving legs" for break-even.
        private readonly HashSet<string> _liveRungSignals = new HashSet<string>();

        #endregion

        #region Lifecycle

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "MeanTick";
                Description = "Limit at the 50% of a 15m rejection block off a fresh 4H PD array "
                    + "(RichKO Framework B). Requires a 1-Minute primary series; an ETH template is "
                    + "recommended so the London range (runner target) is available. docs/design.md.";
                Calculate = Calculate.OnBarClose;

                // entryhandling.md: EntryHandling.UniqueEntries makes EntriesPerDirection apply
                // PER uniquely-named entry, not across all of them. Four unique signal names
                // (MT_R1..MT_R4) x EntriesPerDirection=1 = 4 contracts, matching Contracts=4
                // below. EntryHandling.AllEntries with EntriesPerDirection=4 would instead
                // permit 4 entries PER NAME = up to 16 contracts -- a real sizing bug, not a
                // style choice.
                EntriesPerDirection = 1;
                EntryHandling       = EntryHandling.UniqueEntries;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                BarsRequiredToTrade          = 20;
                IncludeCommission            = true;
                RealtimeErrorHandling        = RealtimeErrorHandling.IgnoreAllErrors;

                // ---- §10 closed parameter list. Session/London boundaries are NOT here: they
                // stay const in MtSession so the NT8 and Python engines cannot drift apart.
                StopPoints       = 10;
                MaxTradesPerDay  = 1;
                EntryTtlMinutes  = 90;
                UseClusterSkip   = false;   // A1: median same-dir gap measured at 8.75x the stop
                VerboseGateDiagnostics = true;   // Sim/Replay-only right now; diagnosing IS the job

                FreshBars4H      = 5;
                ProximityAtrMult = 1.5;
                MinWickTicks     = 8;
                WickRatioMax     = 0.33;

                ExitMode         = MtExitMode.SingleTarget;   // the control arm -- spec 5.1
                SingleTargetR    = 4.0;
                RungR1           = 1.0;
                RungR2           = 3.0;
                Rung3FallbackR   = 4.0;
                MinRungTicks     = 15;
                UseBreakEven     = false;
                RunnerCutoffEt   = 1200;   // HHMM, ET

                Contracts        = 4;
            }
            else if (State == State.Configure)
            {
                // Order IS the index -- swapping these two lines silently repoints every
                // BarsArray[Series15M]/[Series4H] read in this file at the wrong series.
                AddDataSeries(BarsPeriodType.Minute, 15);    // -> Series15M (BarsInProgress 1)
                AddDataSeries(BarsPeriodType.Minute, 240);   // -> Series4H  (BarsInProgress 2)
            }
            else if (State == State.DataLoaded)
            {
                _atr4H = new WilderAtr(Atr4HPeriod);
                if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
                    Print(Name + ": WARNING -- primary series is not 1-Minute. Every gate here "
                        + "counts 1m bars and folds 15m/4H off it; this is a different experiment.");
            }
        }

        #endregion

        #region OnBarUpdate

        private bool _printedClockDiag;
        private bool _printedAtrWarmupWarning;

        protected override void OnBarUpdate()
        {
            // The BarsInProgress guard comes FIRST, before anything reads Time[0] -- NT8's
            // singular indexers are context-relative to whichever series is calling
            // (multi_time_frame_instruments.md:290,320), so Time[0] on a 15m/4H bar-close call
            // is THAT series' bar time, not the primary's. VeeSnapStrategy.cs:478 guards first
            // and only then captures Time[0] (:481) to pass into its fold (:509) -- ported the
            // same shape here: guard, capture `now` once, hand it to the fold explicitly.
            if (BarsInProgress != SeriesPrimary || CurrentBar < BarsRequiredToTrade)
                return;

            DateTime now = Time[0];
            bool new15 = FoldClosedBars(now);   // also feeds _atr4H from newly closed 4H bars

            if (!_printedClockDiag)
            {
                _printedClockDiag = true;
                int diagSec = EtSecondsOfDay(now);
                Print(Name + ": clock check -- raw Time[0]=" + now.ToString("yyyy-MM-dd HH:mm:ss")
                    + " (Kind=" + now.Kind + ") -> computed ET " + TimeSpan.FromSeconds(diagSec).ToString(@"hh\:mm\:ss")
                    + ". This assumes an un-Kinded bar DateTime is TimeZoneInfo.Local, not NT8's own "
                    + "chart-display timezone -- verify this matches the chart's actual ET wall-clock time.");

                // Same DataLoaded warning as below, repeated here on purpose: DataLoaded's own
                // print scrolls away before the session starts, so this is the one Javier can
                // actually still see once bars are flowing -- right beside the clock check,
                // landing in the Output window together.
                if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
                    Print(Name + ": WARNING -- primary series is not 1-Minute. Every gate here "
                        + "counts 1m bars and folds 15m/4H off it; this is a different experiment.");
            }

            if (Bars.IsFirstBarOfSession)
                ResetDailyState();

            int etSec = EtSecondsOfDay(now);
            UpdateLondonRange(etSec);
            CheckRunnerCutoff(etSec);

            MtWindowState win = MtSession.Window(etSec);
            if (win != MtWindowState.Open && win != MtWindowState.SecondChance)
            {
                CancelAllResting("window-closed");
                return;
            }

            if (_restingOrders.Count > 0 && MtSession.IsExpired(etSec, _placedEtSec, EntryTtlMinutes))
            {
                CancelAllResting("ttl");
                return;
            }

            if (_tradesToday >= MaxTradesPerDay || _restingOrders.Count > 0)
                return;

            // Gate 2 (the 15m rejection block) is defined on a 15m candle close -- nothing new
            // to evaluate on a bar where no fresh 15m bar just folded.
            if (!new15)
                return;

            if (!_atr4H.IsWarm)
            {
                // Window is open and a fresh 15m bar just folded -- the ONLY reason nothing
                // runs is that ATR(Atr4HPeriod) on the 4H series hasn't seen enough closed 4H
                // candles yet (roughly 2-3 days of loaded history). That is a chart/history
                // problem, not "no setup today" -- say so once so it isn't mistaken for the
                // ordinary silent case.
                if (!_printedAtrWarmupWarning)
                {
                    _printedAtrWarmupWarning = true;
                    Print(Name + ": WARNING -- ATR(" + Atr4HPeriod + ") on the 4H series is still "
                        + "warming up (" + _atr4H.BarsFed + "/" + Atr4HPeriod + " 4H bars fed). No "
                        + "detection runs until it is warm -- load more history if this persists.");
                }
                return;
            }

            MtDir dir;
            double entryPrice;
            if (!TryDetect(out dir, out entryPrice))
                return;

            MtExitPlan plan = BuildPlan(dir, entryPrice);
            if (!plan.Valid)
                return;

            SubmitPlan(dir, entryPrice, plan);
        }

        private void ResetDailyState()
        {
            _tradesToday = 0;
            CancelAllResting("session-roll");   // safety net -- IsExpired already forces this
                                                 // by SessionEndSec every prior day
            _londonHigh = double.NaN;
            _londonLow  = double.NaN;
        }

        private void UpdateLondonRange(int etSec)
        {
            if (!MtSession.InLondon(etSec)) return;
            _londonHigh = double.IsNaN(_londonHigh) ? High[0] : Math.Max(_londonHigh, High[0]);
            _londonLow  = double.IsNaN(_londonLow)  ? Low[0]  : Math.Min(_londonLow, Low[0]);
        }

        // Spec 5.3: flatten anything still open at market past RunnerCutoffEt. Runs every bar
        // regardless of the entry window's own state, since the runner can still be open well
        // past 11:00. ponytail: no flatten-pending debounce -- ExitLong/ExitShort are market
        // orders that settle same-bar in backtest/Playback; add a pending-gate if live testing
        // ever shows a repeat fire before the position clears.
        private void CheckRunnerCutoff(int etSec)
        {
            // I2: design.md:270 scopes the cutoff to the RUNNER, and design.md:544 puts any
            // time-based exit out of v1 scope. SingleTarget is the control arm every ladder
            // number is reported against -- giving it a time exit the ladder's own
            // justification does not require would bias that comparison.
            if (ExitMode != MtExitMode.Ladder) return;
            if (Position.MarketPosition == MarketPosition.Flat) return;
            if (etSec < HhmmToEtSec(RunnerCutoffEt)) return;

            Print(Name + ": runner cutoff (" + RunnerCutoffEt.ToString("0000") + " ET) reached -- flattening at market.");
            // In Ladder mode with several rungs still live, this unscoped flatten makes NT8
            // auto-cancel every OTHER rung's Set-based bracket as an OCO side effect. Set
            // BEFORE the Exit* call so the bracket-death guard (OnOrderUpdate) does not mistake
            // that cascade for a leg dying unprotected.
            _cutoffFlattening = true;
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong(SeriesPrimary, Position.Quantity, SigCutoff, "");
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort(SeriesPrimary, Position.Quantity, SigCutoff, "");
        }

        // Bars.Count spans the WHOLE loaded series, future bars included, so the `> now` guard
        // is the ONLY thing standing between this loop and lookahead. Ported verbatim from
        // VeeSnapStrategy.cs:698-715 (Vs -> Mt). Absolute index, driven from the primary
        // branch: a per-series process pointer running a bar late on a shared timestamp was a
        // Critical in PullbackZone. `now` is a caller-supplied parameter, never Time[0] read
        // in here, because this method (like VeeSnap's FoldContextBars) must also be safe to
        // reason about independent of which BarsInProgress happens to be live when it runs;
        // the caller is the one place that both knows it is on the primary and captured Time[0]
        // there (VeeSnapStrategy.cs:478,481,509).
        private bool FoldClosedBars(DateTime now)
        {
            bool new15 = false;

            Bars b15 = BarsArray[Series15M];
            for (int j = _fold15 + 1; j < b15.Count; j++)
            {
                if (b15.GetTime(j) > now) break;
                _bars15M.Add(ToBar(b15, j));
                if (_bars15M.Count > Bars15MWindow) _bars15M.RemoveAt(0);
                _fold15 = j;
                new15 = true;
            }

            Bars b4H = BarsArray[Series4H];
            for (int j = _fold4H + 1; j < b4H.Count; j++)
            {
                if (b4H.GetTime(j) > now) break;
                MtBar bar = ToBar(b4H, j);
                _atr4H.Update(bar);
                _bars4H.Add(bar);
                if (_bars4H.Count > Bars4HWindow) _bars4H.RemoveAt(0);
                _fold4H = j;
            }

            return new15;
        }

        private static MtBar ToBar(Bars b, int j)
        {
            return new MtBar
            {
                Time = b.GetTime(j),
                Open = b.GetOpen(j),
                High = b.GetHigh(j),
                Low = b.GetLow(j),
                Close = b.GetClose(j),
                Volume = b.GetVolume(j)
            };
        }

        #endregion

        #region Detection (the shell only gathers bars/ATR and calls MtDetect -- every rule,
        // including WHICH 4H array is chosen, lives in the pure layer so it can be tested)

        private bool TryDetect(out MtDir dir, out double entryPrice)
        {
            dir = MtDir.None;
            entryPrice = 0.0;
            if (_bars15M.Count == 0)
                return false;   // Only reachable before the very first 15m bar folds; new15
                                 // gates every real call here, so there is nothing to diagnose yet.

            double price = Close[0];
            // The array SELECTION (priority + tie-break) is pure and tested --
            // MtDetect.FindQualifiedHtfCandidates's own comment explains the ordering.
            MtGateDiag diag;
            List<MtArray> candidates = MtDetect.FindQualifiedHtfCandidates(
                _bars4H, price, TickSize, FreshBars4H, _atr4H.Value, ProximityAtrMult, MinWickTicks, WickRatioMax, out diag);
            if (candidates.Count == 0)
            {
                PrintGateDiag(diag);
                return false;
            }

            MtArray htf = candidates[0];   // the chosen array -- most recent qualifying, per its own priority

            if (UseClusterSkip)
            {
                // Spec 4.6 (A1): "the next PD array of the SAME direction" -- the nearest one
                // in scan order (i.e. time) sharing htf.Dir, skipping opposite-direction ones
                // in between. ClusterSkipPoints is not an independent §10 dial -- the design
                // text ties it to the stop ("default = the stop, 10"), so it is StopPoints here.
                for (int k = 1; k < candidates.Count; k++)
                {
                    if (candidates[k].Dir != htf.Dir) continue;
                    if (Math.Abs(candidates[k].Level - htf.Level) <= StopPoints)
                    {
                        Print(Name + ": cluster skip -- a same-direction array sits within "
                            + StopPoints + " points of the chosen level.");
                        return false;
                    }
                    break;
                }
            }

            MtBar bar15 = _bars15M[_bars15M.Count - 1];
            int idx15 = _bars15M.Count - 1;

            // Gate 2 (design.md 4.2), all three conditions -- touch, close-outside-in-the-
            // rejection-direction, and wick geometry -- live in one pure, tested function.
            MtArray block15;
            if (!MtDetect.TryRejectionOffLevel(bar15, idx15, htf, TickSize, MinWickTicks, WickRatioMax, out block15, out diag))
            {
                PrintGateDiag(diag);
                return false;
            }

            // block15.Level IS the entry price: MtDetect.TryRejectionBlock already computes the
            // 50%-of-the-wick midpoint (design.md 4.4) as its Level field.
            dir = block15.Dir;
            entryPrice = block15.Level;
            return true;
        }

        // One line per evaluation naming the reason and the number behind it (design.md's own
        // request: a day with no trade should be a data point, not silence). Which threshold to
        // print beside diag.Actual is a plain lookup -- every one of these is already a shell
        // property or a value it just computed, never a re-derivation of what MeanTickCore.cs
        // decided. Default ON: this strategy is Sim/Replay-only right now and diagnosing it is
        // the current job; at most 6 lines/day since this only runs on a fresh-15m evaluation.
        private void PrintGateDiag(MtGateDiag diag)
        {
            if (!VerboseGateDiagnostics) return;

            string why;
            switch (diag.Reason)
            {
                case MtRejectReason.No4HArray:
                    why = "no 4H PD array (rejection block/FVG/order block) found anywhere in the loaded 4H history.";
                    break;
                case MtRejectReason.NoFreshArray:
                    why = "4H array(s) found but none fresh -- nearest is " + diag.Actual.ToString("0")
                        + " bars old, max FreshBars4H=" + FreshBars4H + ".";
                    break;
                case MtRejectReason.NoProximateArray:
                    why = "fresh 4H array(s) found but none within proximity -- nearest is "
                        + diag.Actual.ToString("0.00") + " points from price, max "
                        + (ProximityAtrMult * _atr4H.Value).ToString("0.00") + " ("
                        + ProximityAtrMult.ToString("0.00") + "x ATR=" + _atr4H.Value.ToString("0.00") + ").";
                    break;
                case MtRejectReason.No15mTouch:
                    why = "15m candle never touched the chosen 4H level -- missed by "
                        + diag.Actual.ToString("0.00") + " points.";
                    break;
                case MtRejectReason.CloseWrongSide:
                    why = "15m candle touched the level but closed back through it -- short by "
                        + diag.Actual.ToString("0.00") + " points.";
                    break;
                case MtRejectReason.NoRejectionShape:
                    why = "15m candle closed outside the level but its own body is the wrong color for a rejection.";
                    break;
                case MtRejectReason.WickTooShort:
                    why = "15m rejection wick too short -- " + diag.Actual.ToString("0.0")
                        + " ticks, min MinWickTicks=" + MinWickTicks + ".";
                    break;
                case MtRejectReason.WickTwoSided:
                    why = "15m wick two-sided -- ratio " + diag.Actual.ToString("0.00")
                        + " > WickRatioMax=" + WickRatioMax.ToString("0.00") + " max.";
                    break;
                default:
                    why = "unrecognized reason " + diag.Reason + " (Actual=" + diag.Actual.ToString("0.00") + ").";
                    break;
            }
            Print(Name + ": no setup -- " + why);
        }

        #endregion

        #region Exit planning

        private MtExitPlan BuildPlan(MtDir dir, double entryPrice)
        {
            MtExitPlan plan;
            if (ExitMode == MtExitMode.Ladder)
            {
                // Spec 5.2's closed rung-3 universe: a 15m FVG (consequent encroachment) or its
                // order block (mean threshold). The candidate LIST (nearest-first) is pure and
                // tested (MtDetect.FindStructuralCandidates); BuildLadder itself picks the first
                // one that also clears rung 2 and falls back to Rung3FallbackR if none does.
                List<double> structuralCandidates = MtDetect.FindStructuralCandidates(_bars15M, dir, entryPrice, TickSize);
                double runnerPrice = dir == MtDir.Long ? _londonHigh : _londonLow;
                plan = MtLadder.BuildLadder(dir, entryPrice, StopPoints, RungR1, RungR2, Rung3FallbackR,
                                             structuralCandidates, runnerPrice, Contracts, TickSize, MinRungTicks);
            }
            else
            {
                plan = MtLadder.BuildSingleTarget(dir, entryPrice, StopPoints, SingleTargetR, Contracts, TickSize);
            }

            // A qualifying setup that still produces an invalid plan is otherwise silent --
            // OnBarUpdate's `if (!plan.Valid) return;` has no output. RungR1/RungR2 sit inside
            // their own independent [0.1, 20] Range so NT8 cannot express "RungR2 > RungR1" as a
            // property constraint; an inverted pair (or any other guard in BuildLadder/
            // BuildSingleTarget) would otherwise leave the strategy running a whole session doing
            // nothing with no line explaining why.
            if (!plan.Valid)
                Print(Name + ": WARNING -- a qualifying setup (" + dir + " @ " + entryPrice.ToString("0.00")
                    + ") produced an INVALID exit plan and was skipped. Check RungR1 < RungR2 and the "
                    + "other BuildLadder/BuildSingleTarget guards.");

            return plan;
        }

        #endregion

        #region Submission

        // Spec 6.1. K independent entry signals, one bracket each, all at the SAME limit price.
        // The bracket is set BEFORE its matching Enter*Limit (setstoploss.md /
        // setprofittarget.md: "the Set method should be called prior to submitting the
        // associated entry order"). Absolute prices straight from the pure engine -- never a
        // second, divergent calculation here (MtMath.RoundToTick already ran inside it).
        private void SubmitPlan(MtDir dir, double entryPrice, MtExitPlan plan)
        {
            _entryPrice = entryPrice;
            _placedEtSec = EtSecondsOfDay(Time[0]);
            _planCounted = false;   // I1: this plan has not yet counted toward _tradesToday

            _liveRungSignals.Clear();
            for (int i = 0; i < plan.Rungs.Count; i++)
                _liveRungSignals.Add(RungSignalPrefix + plan.Rungs[i].Index);

            Print(Name + ": arming " + plan.Rungs.Count + "-rung " + dir + " @ "
                + entryPrice.ToString("0.00") + ", stop " + plan.StopPrice.ToString("0.00") + ".");

            for (int i = 0; i < plan.Rungs.Count; i++)
            {
                MtRung r = plan.Rungs[i];
                string sig = RungSignalPrefix + r.Index;

                SetStopLoss(sig, CalculationMode.Price, plan.StopPrice, false);
                SetProfitTarget(sig, CalculationMode.Price, r.Price);

                // Placeholder KEY before the submit (nt8-order-event-race.md): a fill that
                // lands synchronously in-stack during Enter*Limit runs OnExecutionUpdate's
                // `_restingOrders.Remove(n)` immediately, which needs the key already present
                // to have anything to remove. Without this, that Remove is a silent no-op and
                // the post-call assignment below would then re-insert the already-filled order
                // as if it were still resting -- permanently stale, since nothing fires again
                // to clear it (and a later TTL cancel would CancelOrder() a terminal order).
                _restingOrders[sig] = null;
                Order o = dir == MtDir.Long
                    ? EnterLongLimit(SeriesPrimary, true, r.Quantity, entryPrice, sig)
                    : EnterShortLimit(SeriesPrimary, true, r.Quantity, entryPrice, sig);
                // Only write the real reference if the key survived the call -- if an in-stack
                // event already removed it (filled, or rejected/cancelled-unfilled), this must
                // not revive it.
                if (_restingOrders.ContainsKey(sig))
                    _restingOrders[sig] = o;
            }
        }

        // Cancels every still-resting rung entry. `_restingOrders` is cleared and each
        // signal dropped from `_liveRungSignals` BEFORE any CancelOrder() call: an in-stack
        // Cancelled echo (nt8-order-event-race.md) must not find a live entry to remove a
        // second time, and mutating a Dictionary mid-foreach would throw anyway.
        private void CancelAllResting(string why)
        {
            if (_restingOrders.Count == 0) return;

            var toCancel = new List<Order>(_restingOrders.Values);
            var sigs = new List<string>(_restingOrders.Keys);
            Print(Name + ": cancelling " + toCancel.Count + " resting entr"
                + (toCancel.Count == 1 ? "y" : "ies") + " (" + why + ").");

            _restingOrders.Clear();
            for (int i = 0; i < sigs.Count; i++)
                _liveRungSignals.Remove(sigs[i]);

            for (int i = 0; i < toCancel.Count; i++)
                if (toCancel[i] != null) CancelOrder(toCancel[i]);
        }

        #endregion

        #region Order and execution events

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
            int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            Order o = execution.Order;
            if (o == null) return;

            bool holding = o.OrderState == OrderState.Filled || o.OrderState == OrderState.PartFilled
                        || (o.OrderState == OrderState.Cancelled && o.Filled > 0);
            if (holding)
            {
                string n = o.Name;

                if (_liveRungSignals.Contains(n))   // an entry fill for one of the K rungs
                {
                    if (o.OrderState != OrderState.PartFilled)
                        _restingOrders.Remove(n);

                    // I1: count once per PLAN, not once per rung -- a K-rung Ladder fill would
                    // otherwise consume up to K of the day's budget from a single setup, while
                    // a SingleTarget setup consumes 1. ExitMode is the swept parameter, so both
                    // arms need the same effective daily trade count to be comparable.
                    if (!_planCounted && _tradesToday < MaxTradesPerDay)
                    {
                        _planCounted = true;
                        _tradesToday++;
                        Print(Name + ": setup " + _tradesToday + "/" + MaxTradesPerDay + " today (first fill "
                            + n + ") @ " + price.ToString("0.00") + ".");
                    }
                }
                else if (n == "Stop loss" || n == "Profit target")
                {
                    string rungSig = o.FromEntrySignal;
                    _liveRungSignals.Remove(rungSig);

                    // Spec 5.4, default OFF. Per-signal brackets keep this contained to the
                    // surviving legs -- a sibling's stop/target is untouched.
                    if (UseBreakEven && n == "Profit target" && rungSig == RungSignalPrefix + "1"
                        && _liveRungSignals.Count > 0)
                    {
                        foreach (string sig in _liveRungSignals)
                            SetStopLoss(sig, CalculationMode.Price, _entryPrice, false);
                        Print(Name + ": rung 1 target filled -- moved " + _liveRungSignals.Count
                            + " surviving leg(s) to break-even.");
                    }
                }
            }

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                _liveRungSignals.Clear();
                _cutoffFlattening = false;
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
            int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error,
            string comment)
        {
            if (order == null) return;
            string n = order.Name;

            // A rejected/cancelled-unfilled entry leg: drop it so `_restingOrders.Count > 0`
            // does not stay stuck on a dead order. Cleared BEFORE CancelOrder() inside
            // CancelAllResting, so a self-caused cancel echo finds nothing here to remove.
            if (_restingOrders.ContainsKey(n)
                && (orderState == OrderState.Rejected || (orderState == OrderState.Cancelled && filled == 0)))
            {
                _restingOrders.Remove(n);
                _liveRungSignals.Remove(n);
                return;
            }

            // Bracket-death guard (design.md 6.3; pattern from RlpLongStrategy.cs:339-372,
            // adapted). Phase 0a confirmed target-side independence but the stop side has
            // never once been observed filling or surviving a sibling's close -- this is what
            // converts "Javier notices on the chart, eventually" into a log line at the
            // instant it happens. Scoped PER RUNG via `_liveRungSignals`, not
            // Position.MarketPosition: with K independent brackets, a sibling rung's own
            // normal OCO close (target fills -> that rung's stop auto-cancels) would otherwise
            // false-positive on every single rung fill, since the OTHER rungs keep the
            // aggregate position non-flat. A rung already removed from `_liveRungSignals`
            // (its own exit already filled, or its entry never filled) is a benign echo, not a
            // leak -- this can still race a same-tick OCO cancel arriving before its sibling's
            // execution is processed (nt8-order-event-race.md); Round 3 (design.md 6.1) is
            // the actual empirical test of that ordering.
            if (!_cutoffFlattening
                && (n == "Stop loss" || n == "Profit target")
                && (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                && _liveRungSignals.Contains(order.FromEntrySignal))
            {
                Print(Name + ": WARNING -- " + n + " (" + order.FromEntrySignal + ") " + orderState
                    + " while that rung's position is still open -- no resurrection here, it is "
                    + "unprotected on that side.");
            }
        }

        #endregion

        #region The Eastern-Time clock

        // The single most important part of this file -- see the header. The timezone id
        // differs by platform (Linux/WSL vs Windows); try both spellings so the same code
        // works in the test runner and in NT8.
        private static readonly TimeZoneInfo EtZone = ResolveEtZone();

        private static TimeZoneInfo ResolveEtZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        }

        private static int EtSecondsOfDay(DateTime barTime)
        {
            DateTime utc = barTime.Kind == DateTimeKind.Utc
                ? barTime
                : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(barTime, DateTimeKind.Unspecified),
                                                TimeZoneInfo.Local);
            DateTime et = TimeZoneInfo.ConvertTimeFromUtc(utc, EtZone);
            return et.Hour * 3600 + et.Minute * 60 + et.Second;
        }

        private static int HhmmToEtSec(int hhmm)
        {
            return (hhmm / 100) * 3600 + (hhmm % 100) * 60;
        }

        #endregion

        #region Properties

        [NinjaScriptProperty, Range(0.25, 1000)]
        [Display(Name = "Stop (points)", Description = "Fixed stop distance in points for every rung -- spec 5, coupled to the rung R-table.", GroupName = "01. Entry", Order = 0)]
        public double StopPoints { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Max trades/day", Description = "Hard daily latch -- spec 4.5.", GroupName = "01. Entry", Order = 1)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty, Range(1, 600)]
        [Display(Name = "Entry TTL (min)", Description = "Cancel an unfilled resting entry this many minutes after placement, or at session end, whichever comes first.", GroupName = "01. Entry", Order = 2)]
        public int EntryTtlMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cluster skip (A1)", Description = "Skip the trade if the next same-direction PD array sits within StopPoints of the chosen level. Default OFF -- spec 4.6/validation.md (median gap measured at 8.75x the stop).", GroupName = "01. Entry", Order = 3)]
        public bool UseClusterSkip { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Verbose gate diagnostics", Description = "Print one line per evaluation naming which gate rejected a setup and the number behind it. Default ON -- at most 6 lines/day (one fresh 15m bar can fold at most 6 times inside the 09:30-11:00 ET window), and this strategy is Sim/Replay-only.", GroupName = "01. Entry", Order = 4)]
        public bool VerboseGateDiagnostics { get; set; }

        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Fresh bars (4H)", Description = "A 4H PD array must have formed within this many closed 4H candles -- spec 4.1.", GroupName = "02. Detection", Order = 0)]
        public int FreshBars4H { get; set; }

        [NinjaScriptProperty, Range(0.1, 10)]
        [Display(Name = "Proximity (x ATR)", Description = "The 4H array must sit within this many ATR(14)-on-4H of price -- spec 4.1, pre-registered, not swept.", GroupName = "02. Detection", Order = 1)]
        public double ProximityAtrMult { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Min wick (ticks)", Description = "Minimum rejection-wick size, in ticks, on both the 4H and 15m rejection blocks -- spec 4.3.", GroupName = "02. Detection", Order = 2)]
        public int MinWickTicks { get; set; }

        [NinjaScriptProperty, Range(0.01, 1.0)]
        [Display(Name = "Wick ratio max", Description = "Opposite-side wick / rejection wick must not exceed this -- spec 4.3, the source's own falsifiable filter.", GroupName = "02. Detection", Order = 3)]
        public double WickRatioMax { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Exit mode", Description = "SingleTarget is the control arm (spec 5.1) and the default. Ladder is the amendment under test (spec 5.2) and must never be the default.", GroupName = "03. Exits", Order = 0)]
        public MtExitMode ExitMode { get; set; }

        [NinjaScriptProperty, Range(0.1, 20)]
        [Display(Name = "Single target (R)", Description = "SingleTarget mode's only target, in R -- spec 5.1.", GroupName = "03. Exits", Order = 1)]
        public double SingleTargetR { get; set; }

        [NinjaScriptProperty, Range(0.1, 20)]
        [Display(Name = "Rung 1 (R)", Description = "Ladder rung 1 -- spec 5.2.", GroupName = "03. Exits", Order = 2)]
        public double RungR1 { get; set; }

        [NinjaScriptProperty, Range(0.1, 20)]
        [Display(Name = "Rung 2 (R)", Description = "Ladder rung 2 -- spec 5.2.", GroupName = "03. Exits", Order = 3)]
        public double RungR2 { get; set; }

        [NinjaScriptProperty, Range(0.1, 20)]
        [Display(Name = "Rung 3 fallback (R)", Description = "Used when no valid opposing 15m array exists -- spec 5.2.", GroupName = "03. Exits", Order = 4)]
        public double Rung3FallbackR { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Min rung spacing (ticks)", Description = "A rung within this many ticks of the previous one is dropped and its quantity folded into the next -- spec 5.2.", GroupName = "03. Exits", Order = 5)]
        public int MinRungTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Break-even", Description = "Move surviving legs' stop to entry when rung 1's target fills. Default OFF -- spec 5.4.", GroupName = "03. Exits", Order = 6)]
        public bool UseBreakEven { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Runner cutoff (ET, HHMM)", Description = "Flatten anything still open at market at this ET time -- spec 5.3.", GroupName = "03. Exits", Order = 7)]
        public int RunnerCutoffEt { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Contracts", Description = "Total position size, split across rungs in Ladder mode -- spec 8.", GroupName = "04. Sizing", Order = 0)]
        public int Contracts { get; set; }

        #endregion
    }
}
