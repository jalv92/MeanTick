// MeanTickCore.cs — Detection for MeanTick. The whole model in one function:
// a rejection block is a candle that pokes into a level and gets refused on
// one side only, and the entry is the 50% of THAT WICK, not of the candle.
//
// ZERO `using NinjaTrader.*`, own namespace MeanTickCore, C# 7.3 only: NT8
// compiles bin/Custom into ONE assembly and ships its own types under common
// names, so a NinjaTrader using here is how a CS0101 clash starts. Purity is
// also what lets this file compile in NT8 and in the net8 test runner, which
// has no NinjaTrader assemblies on its reference path.
using System;
using System.Collections.Generic;

namespace MeanTickCore
{
    public enum MtDir { None = 0, Long = 1, Short = -1 }

    public enum MtArrayKind { Fvg, OrderBlock, RejectionBlock }

    public struct MtArray
    {
        public MtArrayKind Kind;
        public MtDir       Dir;
        public double      Top;
        public double      Bottom;
        public double      Level;
        public int         BarIndex;
        public DateTime    Time;
        public bool        Valid;
    }

    // Every way a day's evaluation can come out, computed HERE (the pure layer) so the
    // shell has only to format one Print line, not re-derive WHY. Silence used to be the
    // common case (measured: 76.5% of 238 sessions have zero qualifying candidates) and
    // looked identical whether the strategy was working correctly or misconfigured -- this
    // enum is what makes those two cases distinguishable in the Output window.
    public enum MtRejectReason
    {
        Accepted,           // a full setup: qualified 4H array + Gate 2 15m rejection.
        No4HArray,          // nothing shaped like a 4H PD array anywhere in the scanned window.
        NoFreshArray,       // 4H array(s) found, but every one is older than FreshBars4H.
        NoProximateArray,   // a fresh 4H array exists, but none sits within ProximityAtrMult*ATR.
        No15mTouch,         // the 15m candle's range never reached the chosen 4H level.
        CloseWrongSide,     // it touched, but closed back through the level instead of past it.
        NoRejectionShape,   // it closed on the right side but the candle body itself is the
                            // wrong color for a rejection (e.g. closed above the level yet
                            // still closed below its own open) -- TryRejectionBlock's own
                            // "must close bearish/bullish" guard, reachable independently of
                            // condition 2 above (see TryRejectionOffLevel's Long counter-example).
        WickTooShort,       // the rejection wick itself is shorter than MinWickTicks.
        WickTwoSided,       // opposite-side wick / rejection wick exceeds WickRatioMax.
    }

    // One reason plus the one number that explains it -- distance from a threshold, bars of
    // staleness, a ratio, whatever is relevant to `Reason`. Which threshold to print beside
    // `Actual` is a lookup the shell already owns (MinWickTicks, WickRatioMax, FreshBars4H,
    // ProximityAtrMult are its own properties), so this struct does not duplicate them.
    public struct MtGateDiag
    {
        public MtRejectReason Reason;
        public double         Actual;
    }

    public static class MtDetect
    {
        // The model in one function. `expected` is the direction the trade would take,
        // so a Short setup wants an UPPER rejection wick (price was pushed up and
        // refused), and Long wants a lower one.
        public static bool TryRejectionBlock(MtBar bar, int barIndex, MtDir expected,
                                             double tickSize, int minWickTicks,
                                             double wickRatioMax, out MtArray array)
        {
            MtGateDiag diag;
            return TryRejectionBlock(bar, barIndex, expected, tickSize, minWickTicks, wickRatioMax, out array, out diag);
        }

        // Same rule, plus WHY when it fails -- diag.Reason names the gate, diag.Actual is the
        // measured number behind it (ticks for a short wick, the ratio for a two-sided one).
        public static bool TryRejectionBlock(MtBar bar, int barIndex, MtDir expected,
                                             double tickSize, int minWickTicks,
                                             double wickRatioMax, out MtArray array, out MtGateDiag diag)
        {
            array = new MtArray();
            diag  = new MtGateDiag { Reason = MtRejectReason.NoRejectionShape };

            double bodyTop = Math.Max(bar.Open, bar.Close);
            double bodyBot = Math.Min(bar.Open, bar.Close);

            double reject, opposite, level;

            if (expected == MtDir.Short)
            {
                if (bar.Close >= bar.Open) return false;   // must close bearish -- diag stays NoRejectionShape
                reject   = bar.High - bodyTop;
                opposite = bodyBot - bar.Low;
                level    = bodyTop + reject * 0.5;
            }
            else if (expected == MtDir.Long)
            {
                if (bar.Close <= bar.Open) return false;   // must close bullish -- diag stays NoRejectionShape
                reject   = bodyBot - bar.Low;
                opposite = bar.High - bodyTop;
                level    = bodyBot - reject * 0.5;
            }
            else
            {
                return false;   // MtDir.None -- not a real gate outcome, diag stays NoRejectionShape
            }

            // Guard the degenerate case explicitly rather than letting 0/0 produce NaN: a NaN
            // ratio makes the comparison below false and the gate would fail OPEN. Both this
            // and the MinWickTicks check below report as WickTooShort -- reject<=0 only ever
            // triggers when reject is also under minWickTicks*tickSize, so it is the same gate.
            if (reject < minWickTicks * tickSize || reject <= 0.0)
            {
                diag = new MtGateDiag { Reason = MtRejectReason.WickTooShort, Actual = reject / tickSize };
                return false;
            }

            double ratio = opposite / reject;
            if (ratio > wickRatioMax)
            {
                diag = new MtGateDiag { Reason = MtRejectReason.WickTwoSided, Actual = ratio };
                return false;
            }

            array.Kind     = MtArrayKind.RejectionBlock;
            array.Dir      = expected;
            array.Top      = bar.High;
            array.Bottom   = bar.Low;
            array.Level    = MtMath.RoundToTick(level, tickSize);
            array.BarIndex = barIndex;
            array.Time     = bar.Time;
            array.Valid    = true;
            diag = new MtGateDiag { Reason = MtRejectReason.Accepted, Actual = ratio };
            return true;
        }

        // 3-bar imbalance, ported byte-for-byte from VeeSnapCore.cs:455-467 (the gap
        // arithmetic only -- `minHeight` here is the caller's already-computed threshold,
        // not an ATR multiplier, so this function does no ATR scaling of its own). The
        // bearish case is `c.High < a.Low`, NOT a loose overlap test: the strict spelling
        // is what the source and its Python mirror pin. Bar b, the FVG's middle candle, is
        // never read -- the classic 3-candle definition compares only the outer two bars.
        public static bool TryFvg(MtBar a, MtBar b, MtBar c, int cBarIndex,
                                  double minHeight, double tickSize, out MtArray array)
        {
            array = new MtArray();

            if (c.Low > a.High && c.Low - a.High >= minHeight)
            {
                array.Kind     = MtArrayKind.Fvg;
                array.Dir      = MtDir.Long;
                array.Top      = c.Low;
                array.Bottom   = a.High;
                array.Level    = MtMath.RoundToTick((array.Top + array.Bottom) * 0.5, tickSize);
                array.BarIndex = cBarIndex;
                array.Time     = c.Time;
                array.Valid    = true;
                return true;
            }
            if (c.High < a.Low && a.Low - c.High >= minHeight)
            {
                array.Kind     = MtArrayKind.Fvg;
                array.Dir      = MtDir.Short;
                array.Top      = a.Low;
                array.Bottom   = c.High;
                array.Level    = MtMath.RoundToTick((array.Top + array.Bottom) * 0.5, tickSize);
                array.BarIndex = cBarIndex;
                array.Time     = c.Time;
                array.Valid    = true;
                return true;
            }
            return false;
        }

        // The order block is defined THROUGH the FVG rather than through a "displacement"
        // threshold. The conventional wording -- "the last opposite candle before a
        // displacement leg" -- smuggles in a free parameter, because displacement has no
        // agreed definition and three different ad-hoc versions already exist in this tree.
        // Anchoring to the gap the move left behind reuses a detector that is already ported,
        // mirrored and tested, and removes the dial entirely. Spec 4.1.
        public static bool TryOrderBlockFromFvg(IList<MtBar> bars, int fvgFirstIndex,
                                                MtDir dir, int baseIndex, double tickSize,
                                                out MtArray array)
        {
            array = new MtArray();
            if (bars == null || fvgFirstIndex < 0 || fvgFirstIndex >= bars.Count) return false;
            if (dir == MtDir.None) return false;

            for (int i = fvgFirstIndex; i >= 0; i--)
            {
                MtBar b = bars[i];
                bool opposite = dir == MtDir.Long ? b.Close < b.Open : b.Close > b.Open;
                if (!opposite) continue;

                array.Kind     = MtArrayKind.OrderBlock;
                array.Dir      = dir;
                array.Top      = b.High;
                array.Bottom   = b.Low;
                array.Level    = MtMath.RoundToTick((b.High + b.Low) * 0.5, tickSize);
                array.BarIndex = baseIndex + i;
                array.Time     = b.Time;
                array.Valid    = true;
                return true;
            }
            return false;
        }

        // Spec 4.1. Freshness is the source's "most recent four or five 4H candles";
        // proximity is its undefined "within a reasonable distance", pre-registered here as
        // an ATR multiple and NOT swept in the first pass -- sweeping an undefined constant
        // is how the 2026-08 funnel produced fourteen corpses.
        public static bool IsQualifiedHtf(MtArray array, int currentBarIndex, double price,
                                          int freshBars, double atr, double proximityAtrMult)
        {
            if (!array.Valid) return false;
            if (currentBarIndex - array.BarIndex > freshBars) return false;
            if (atr <= 0.0) return false;
            return Math.Abs(price - array.Level) <= proximityAtrMult * atr;
        }

        // Spec 4.2, Gate 2 -- the WHOLE rule, not just the wick geometry TryRejectionBlock
        // alone checks. A 15m candle qualifies only when all three hold: (1) its range
        // touches the 4H level, (2) it CLOSES OUTSIDE the level in the rejection direction,
        // (3) it leaves a one-sided rejection wick on the correct side. Condition 2 was
        // missing entirely from the shell's original Gate 2 check -- a candle can touch a
        // level, leave a clean wick, and still close back through it. Counter-example that
        // shipped past review: 4H level 18000 (Long), 15m O=17990 H=18001 L=17960 C=17999 --
        // touch passes, wick ratio passes (0.067), but the close (17999) never got back
        // ABOVE the level it supposedly rejected upward from. Folded here, not the shell, so
        // both the touch test and the close-direction test are testable.
        public static bool TryRejectionOffLevel(MtBar bar15, int barIndex15, MtArray htf,
                                                 double tickSize, int minWickTicks,
                                                 double wickRatioMax, out MtArray block)
        {
            MtGateDiag diag;
            return TryRejectionOffLevel(bar15, barIndex15, htf, tickSize, minWickTicks, wickRatioMax, out block, out diag);
        }

        // Same three conditions, plus WHY when it fails. diag.Actual is a distance in points
        // for conditions 1-2 (how far the level sat outside the candle, how far short the
        // close fell) and comes straight from TryRejectionBlock's own diag for condition 3.
        public static bool TryRejectionOffLevel(MtBar bar15, int barIndex15, MtArray htf,
                                                 double tickSize, int minWickTicks,
                                                 double wickRatioMax, out MtArray block, out MtGateDiag diag)
        {
            block = new MtArray();
            // Unreachable from the shell today -- TryDetect only ever calls this with
            // candidates[0] out of FindQualifiedHtfCandidates, which is always Valid with a
            // real Dir. No dedicated reason for it: adding a member for a branch no caller can
            // hit would be exactly the kind of diagnostic nobody could ever see fire.
            diag = new MtGateDiag { Reason = MtRejectReason.No4HArray };
            if (!htf.Valid || htf.Dir == MtDir.None) return false;

            // Condition 1: the candle's range touches the 4H level.
            if (bar15.High < htf.Level || bar15.Low > htf.Level)
            {
                double miss = bar15.High < htf.Level ? htf.Level - bar15.High : bar15.Low - htf.Level;
                diag = new MtGateDiag { Reason = MtRejectReason.No15mTouch, Actual = miss };
                return false;
            }

            // Condition 2: closes OUTSIDE the level, in the rejection direction. Long means
            // "rejected upward" so the close must end up ABOVE the level; Short the mirror.
            int sign = htf.Dir == MtDir.Long ? 1 : -1;
            if (sign * (bar15.Close - htf.Level) <= 0)
            {
                double shortfall = sign > 0 ? htf.Level - bar15.Close : bar15.Close - htf.Level;
                diag = new MtGateDiag { Reason = MtRejectReason.CloseWrongSide, Actual = shortfall };
                return false;
            }

            // Condition 3: a one-sided rejection wick on the correct side.
            return TryRejectionBlock(bar15, barIndex15, htf.Dir, tickSize, minWickTicks, wickRatioMax, out block, out diag);
        }

        // Spec 4.1's array SELECTION, not just qualification -- this decides WHETHER and
        // WHICH array a day trades off, so it belongs here, pure and tested, not in the shell
        // that cannot compile into tests/MeanTick.Tests.csproj. Scans EVERY candle in `bars`
        // (the shell caps that list at Bars4HWindow=40, so this is at most ~38 iterations, a
        // few times a day -- nowhere near hot-path), newest first, for every candidate that
        // passes IsQualifiedHtf. Per-bar type priority (rejection block, then FVG, then the
        // order block anchored to that FVG) and the "most-recent-qualifying-bar wins"
        // tie-break are decisions, not mechanics -- spec 4.1 lists the three admitted types
        // but never ranks them, so this function IS that ranking. candidates[0] is what a
        // caller uses as "the" qualified array; the rest exist so a caller can also implement
        // A1's cluster-skip (spec 4.6) without re-scanning.
        //
        // The startIdx floor of 2 means a candidate anchored at bar index 0 or 1 can never be
        // found by this scan, even though TryRejectionBlock itself does not need the two bars
        // before it -- a non-issue per the prior comment here (ATR(14)'s own warm-up already
        // guarantees more history than that in every real caller).
        //
        // The scan used to stop at `currentIdx - freshBars` instead of scanning the whole
        // list -- a real optimization for the ACCEPT decision, since IsQualifiedHtf's own
        // freshness check would reject anything older anyway. But it silently discarded the
        // one piece of information a stale-vs-absent diagnostic needs: a candidate that DOES
        // exist, just too old. Widened here so `diag` can tell "arrays found but none fresh"
        // (NoFreshArray) apart from "nothing shaped like an array at all" (No4HArray) --
        // `found`'s contents are byte-for-byte the same either way, since IsQualifiedHtf still
        // gates every addition to it exactly as before.
        public static List<MtArray> FindQualifiedHtfCandidates(IList<MtBar> bars, double price,
                                                                double tickSize, int freshBars,
                                                                double atr, double proximityAtrMult,
                                                                int minWickTicks, double wickRatioMax)
        {
            MtGateDiag diag;
            return FindQualifiedHtfCandidates(bars, price, tickSize, freshBars, atr, proximityAtrMult,
                                              minWickTicks, wickRatioMax, out diag);
        }

        // Same scan, plus WHY nothing qualified: No4HArray (no candidate shape found at all),
        // NoFreshArray (found some, all older than freshBars -- diag.Actual is the freshest
        // one's age in bars), or NoProximateArray (a fresh one exists, none close enough --
        // diag.Actual is the nearest one's distance in points). Accepted when found.Count > 0;
        // the caller (TryDetect) only reads `diag` when the list comes back empty.
        public static List<MtArray> FindQualifiedHtfCandidates(IList<MtBar> bars, double price,
                                                                double tickSize, int freshBars,
                                                                double atr, double proximityAtrMult,
                                                                int minWickTicks, double wickRatioMax,
                                                                out MtGateDiag diag)
        {
            var found = new List<MtArray>();
            int n = bars == null ? 0 : bars.Count;
            if (n == 0)
            {
                diag = new MtGateDiag { Reason = MtRejectReason.No4HArray };
                return found;
            }

            int currentIdx = n - 1;
            // Floor of 2 only -- no freshBars-based cap. See the widened-scan comment above.
            int startIdx = 2;

            bool anyRaw = false, anyFresh = false;
            int bestBarsOld = int.MaxValue;
            double bestDistance = double.MaxValue;

            // Local, not a separate static helper: every one of its captured locals (found,
            // anyRaw, anyFresh, bestBarsOld, bestDistance, currentIdx, price, freshBars, atr,
            // proximityAtrMult) would otherwise need threading through as a ref/out parameter
            // at all four call sites below.
            void Track(MtArray candidate)
            {
                anyRaw = true;
                int barsOld = currentIdx - candidate.BarIndex;
                if (barsOld < bestBarsOld) bestBarsOld = barsOld;
                if (barsOld > freshBars) return;

                anyFresh = true;
                double distance = Math.Abs(price - candidate.Level);
                if (distance < bestDistance) bestDistance = distance;

                if (IsQualifiedHtf(candidate, currentIdx, price, freshBars, atr, proximityAtrMult))
                    found.Add(candidate);
            }

            for (int i = currentIdx; i >= startIdx; i--)
            {
                MtBar bar = bars[i];

                MtArray rb;
                if (TryRejectionBlock(bar, i, MtDir.Long, tickSize, minWickTicks, wickRatioMax, out rb))
                    Track(rb);
                if (TryRejectionBlock(bar, i, MtDir.Short, tickSize, minWickTicks, wickRatioMax, out rb))
                    Track(rb);

                if (i < 2) continue;

                MtArray fvg;
                if (!TryFvg(bars[i - 2], bars[i - 1], bar, i, 0.0, tickSize, out fvg))
                    continue;
                Track(fvg);

                MtArray ob;
                if (TryOrderBlockFromFvg(bars, i - 2, fvg.Dir, 0, tickSize, out ob))
                    Track(ob);
            }

            if (found.Count > 0)
                diag = new MtGateDiag { Reason = MtRejectReason.Accepted };
            else if (!anyRaw)
                diag = new MtGateDiag { Reason = MtRejectReason.No4HArray };
            else if (!anyFresh)
                diag = new MtGateDiag { Reason = MtRejectReason.NoFreshArray, Actual = bestBarsOld };
            else
                diag = new MtGateDiag { Reason = MtRejectReason.NoProximateArray, Actual = bestDistance };

            return found;
        }

        // Spec 5.2's rung-3 candidate gathering, moved out of the shell for the identical
        // reason FindQualifiedHtfCandidates was: a caller that hands over only ONE
        // already-filtered price can accept or reject it, never fall through to the
        // next-best level once the first candidate turns out to be short of rung 2. Returns
        // every 15m FVG (consequent encroachment) or order-block (mean threshold) level that
        // lies ahead of `entry` in the trade's direction, NEAREST FIRST -- MtLadder.BuildLadder
        // walks this list and takes the first one that also clears rung 2. Filtering on price
        // POSITION, not the array's own Dir polarity, is deliberate (design.md 5.2): a bullish
        // FVG above price still acts as resistance for a long regardless of its own label.
        public static List<double> FindStructuralCandidates(IList<MtBar> bars, MtDir dir, double entry, double tickSize)
        {
            var found = new List<double>();
            if (bars == null) return found;
            int sign = dir == MtDir.Long ? 1 : -1;

            for (int i = bars.Count - 1; i >= 2; i--)
            {
                MtArray fvg;
                if (!TryFvg(bars[i - 2], bars[i - 1], bars[i], i, 0.0, tickSize, out fvg))
                    continue;

                if (sign * (fvg.Level - entry) > 0.0) found.Add(fvg.Level);

                MtArray ob;
                if (TryOrderBlockFromFvg(bars, i - 2, fvg.Dir, 0, tickSize, out ob)
                    && sign * (ob.Level - entry) > 0.0)
                    found.Add(ob.Level);
            }

            found.Sort((a, b) => Math.Abs(a - entry).CompareTo(Math.Abs(b - entry)));
            return found;
        }
    }

    public enum MtWindowState { Closed, Open, SecondChance }

    // Every boundary here is EASTERN TIME seconds-of-day, supplied by the shell.
    // The pure layer never reads a machine clock and never guesses a timezone: MeanTick
    // is a model of one clock instant, and a chart in another timezone would arm it at
    // the wrong minute with no symptom except no trades or bad ones. The Python mirror
    // uses explicit ET constants, so a naked TimeOfDay here would let the two engines
    // diverge by an hour twice a year and the parity gate would blame the engine.
    public static class MtSession
    {
        public const int SessionStartSec = 9 * 3600 + 30 * 60;
        public const int SessionEndSec   = 11 * 3600;
        public const int SecondChanceSec = 10 * 3600;
        public const int LondonStartSec  = 2 * 3600;
        public const int LondonEndSec    = 5 * 3600;

        public static MtWindowState Window(int etSecondsOfDay)
        {
            if (etSecondsOfDay < SessionStartSec) return MtWindowState.Closed;
            if (etSecondsOfDay >= SessionEndSec)  return MtWindowState.Closed;
            return etSecondsOfDay >= SecondChanceSec ? MtWindowState.SecondChance : MtWindowState.Open;
        }

        public static bool InLondon(int etSecondsOfDay)
        {
            return etSecondsOfDay >= LondonStartSec && etSecondsOfDay < LondonEndSec;
        }

        public static bool IsExpired(int etSecondsOfDay, int placedEtSecondsOfDay, int ttlMinutes)
        {
            if (etSecondsOfDay >= SessionEndSec) return true;
            return etSecondsOfDay - placedEtSecondsOfDay >= ttlMinutes * 60;
        }
    }
}
