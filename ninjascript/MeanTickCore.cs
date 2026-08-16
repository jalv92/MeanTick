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

    public static class MtDetect
    {
        // The model in one function. `expected` is the direction the trade would take,
        // so a Short setup wants an UPPER rejection wick (price was pushed up and
        // refused), and Long wants a lower one.
        public static bool TryRejectionBlock(MtBar bar, int barIndex, MtDir expected,
                                             double tickSize, int minWickTicks,
                                             double wickRatioMax, out MtArray array)
        {
            array = new MtArray();

            double bodyTop = Math.Max(bar.Open, bar.Close);
            double bodyBot = Math.Min(bar.Open, bar.Close);

            double reject, opposite, level;

            if (expected == MtDir.Short)
            {
                if (bar.Close >= bar.Open) return false;   // must close bearish
                reject   = bar.High - bodyTop;
                opposite = bodyBot - bar.Low;
                level    = bodyTop + reject * 0.5;
            }
            else if (expected == MtDir.Long)
            {
                if (bar.Close <= bar.Open) return false;   // must close bullish
                reject   = bodyBot - bar.Low;
                opposite = bar.High - bodyTop;
                level    = bodyBot - reject * 0.5;
            }
            else
            {
                return false;
            }

            if (reject < minWickTicks * tickSize) return false;
            // Guard the degenerate case explicitly rather than letting 0/0 produce NaN:
            // a NaN ratio makes the comparison below false and the gate would fail OPEN.
            if (reject <= 0.0) return false;
            if (opposite / reject > wickRatioMax) return false;

            array.Kind     = MtArrayKind.RejectionBlock;
            array.Dir      = expected;
            array.Top      = bar.High;
            array.Bottom   = bar.Low;
            array.Level    = MtMath.RoundToTick(level, tickSize);
            array.BarIndex = barIndex;
            array.Time     = bar.Time;
            array.Valid    = true;
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
    }
}
