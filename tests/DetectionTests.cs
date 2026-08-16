// tests/DetectionTests.cs
using System;
using System.Collections.Generic;
using MeanTickCore;

public static class DetectionTests
{
    private static MtBar B(double o, double h, double l, double c, double v = 100.0)
    {
        return new MtBar { Time = new DateTime(2026, 1, 5, 9, 30, 0),
                           Open = o, High = h, Low = l, Close = c, Volume = v };
    }

    public static void Run()
    {
        T.Section("primitives");

        // RoundToTick is the house spelling, hand-rolled, and the mirror copies it
        // character for character. If this drifts, every price in both engines drifts.
        T.CheckBits(MtMath.RoundToTick(18000.13, 0.25), 18000.25, "roundToTick rounds up at .13");
        T.CheckBits(MtMath.RoundToTick(18000.12, 0.25), 18000.00, "roundToTick rounds down at .12");
        T.CheckBits(MtMath.RoundToTick(18000.125, 0.25), 18000.25, "roundToTick .125 goes up (floor(x+0.5))");

        // Unit collapses to 0 on a degenerate range instead of dividing by zero.
        // NaN would make every comparison false and every guard fail OPEN.
        T.CheckBits(MtMath.Unit(5.0, 3.0, 3.0), 0.0, "unit collapses on lo==hi");
        T.CheckBits(MtMath.Unit(4.0, 2.0, 6.0), 0.5, "unit maps midpoint to 0.5");

        // The ATR crosses sessions by design. First sample seeds with its own TR.
        var atr = new WilderAtr(14);
        atr.Update(B(100, 110, 90, 105));
        T.CheckBits(atr.Value, 20.0, "atr seeds with tr[0] itself");
        T.Check(!atr.IsWarm, "atr is not warm after one sample");

        T.Section("rejection block");

        // A textbook bearish rejection block: long upper wick, tiny lower wick, closes down.
        // Body top = max(O,C) = 18010. Upper wick = 18030 - 18010 = 20. Entry = 18010 + 10.
        // Lower wick = 18000 - 17998 = 2, ratio 0.1.
        {
            MtArray a;
            bool ok = MtDetect.TryRejectionBlock(B(18010, 18030, 17998, 18000), 42, MtDir.Short,
                                                 0.25, 8, 0.33, out a);
            T.Check(ok, "bearish rejection block with a clean one-sided wick qualifies");
            T.CheckBits(a.Level, 18020.0, "entry is the midpoint of the WICK, not of the candle");
            T.Check(a.Dir == MtDir.Short, "direction is short");
            T.Check(a.Kind == MtArrayKind.RejectionBlock, "kind is rejection block");
        }

        // Mirror case, bullish: body bottom = min(O,C) = 18000, lower wick = 18000 - 17980 = 20.
        {
            MtArray a;
            bool ok = MtDetect.TryRejectionBlock(B(18000, 18025, 17980, 18020), 42, MtDir.Long,
                                                 0.25, 8, 0.33, out a);
            T.Check(ok, "bullish rejection block qualifies");
            T.CheckBits(a.Level, 17990.0, "bullish entry is the midpoint of the lower wick");
        }

        // THE filter with a stated failure rate: wicks on both sides is a doji, and the
        // source vetoes it outright (V1 [3:16], "8 out of 10 times it is going to fail").
        // Upper wick 20, lower wick 20 -> ratio 1.0 > 0.33.
        {
            MtArray a;
            bool ok = MtDetect.TryRejectionBlock(B(18010, 18030, 17990, 18000), 42, MtDir.Short,
                                                 0.25, 8, 0.33, out a);
            T.Check(!ok, "two-sided wick is rejected");
        }

        // Exactly at the ratio boundary: opposite/reject == max must PASS (<=, not <).
        // The boundary is tested at 0.25 rather than at the 0.33 default on purpose: 0.33
        // is not representable in binary, so an "exactly at the boundary" assert written
        // against it would be testing a rounding artefact instead of the comparison.
        // reject = 18042-18010 = 32, opposite = 18009-18001 = 8, ratio = 0.25 exactly.
        {
            MtArray a;
            bool ok = MtDetect.TryRejectionBlock(B(18010, 18042, 18001, 18009), 42, MtDir.Short,
                                                 0.25, 8, 0.25, out a);
            T.Check(ok, "ratio exactly at the max passes (boundary is inclusive)");
        }

        // A wick smaller than MinWickTicks is noise and its 50% sits inside the spread.
        // reject = 18011 - 18010 = 1.0 point = 4 ticks < 8.
        {
            MtArray a;
            bool ok = MtDetect.TryRejectionBlock(B(18010, 18011, 18008, 18009), 42, MtDir.Short,
                                                 0.25, 8, 0.33, out a);
            T.Check(!ok, "wick shorter than MinWickTicks is rejected");
        }

        // The close must be on the rejection side. A bearish-shaped wick that closes UP
        // is not a bearish rejection block.
        {
            MtArray a;
            bool ok = MtDetect.TryRejectionBlock(B(18000, 18030, 17999, 18010), 42, MtDir.Short,
                                                 0.25, 8, 0.33, out a);
            T.Check(!ok, "bearish rejection requires a bearish close");
        }

        // Every level the core emits is tick-rounded before it leaves the core.
        // bodyTop = 18007.5, reject = 18027.56 - 18007.5 = 20.06, raw level = 18017.53,
        // which is not on a 0.25 grid -> 18017.50.
        {
            MtArray a;
            MtDetect.TryRejectionBlock(B(18007.5, 18027.56, 18005, 18007), 42, MtDir.Short,
                                       0.25, 8, 0.33, out a);
            T.CheckBits(a.Level, 18017.50, "level is rounded to tick before it leaves the core");
        }
    }
}
