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
    }
}
