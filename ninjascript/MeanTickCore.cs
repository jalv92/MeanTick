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
    }
}
