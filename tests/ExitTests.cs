// tests/ExitTests.cs
using System;
using System.Collections.Generic;
using MeanTickCore;

public static class ExitTests
{
    public static void Run()
    {
        T.Section("single target (control arm)");

        // Long from 18000, 10-point stop, 4R target, 4 contracts.
        {
            var p = MtLadder.BuildSingleTarget(MtDir.Long, 18000, 10.0, 4.0, 4, 0.25);
            T.Check(p.Valid, "plan is valid");
            T.CheckBits(p.StopPrice, 17990.0, "stop is entry minus StopPoints");
            T.Check(p.Rungs.Count == 1, "the control arm has exactly one rung");
            T.Check(p.Rungs[0].Index == 1, "the single rung is 1-based index 1");
            T.CheckBits(p.Rungs[0].Price, 18040.0, "4R above entry");
            T.Check(p.Rungs[0].Quantity == 4, "the single rung carries the whole position");
            T.Check(!p.Rungs[0].IsRunner, "the control arm has no runner");
            T.Check(!p.Rungs[0].IsStructural, "the control arm's rung is not structural");
            T.Check(p.TotalQuantity == 4, "total quantity equals contracts");
        }

        // Short is the exact mirror. Written out rather than derived, because a sign
        // error here is silent: the plan still 'works', it just loses money.
        {
            var p = MtLadder.BuildSingleTarget(MtDir.Short, 18000, 10.0, 4.0, 4, 0.25);
            T.CheckBits(p.StopPrice, 18010.0, "short stop is entry plus StopPoints");
            T.CheckBits(p.Rungs[0].Price, 17960.0, "short target is 4R below entry");
        }

        // Degenerate inputs produce an INVALID plan, never a plan with a nonsense price.
        {
            var p = MtLadder.BuildSingleTarget(MtDir.None, 18000, 10.0, 4.0, 4, 0.25);
            T.Check(!p.Valid, "MtDir.None yields an invalid plan");
            T.Check(p.Rungs != null && p.Rungs.Count == 0, "an invalid plan still has an empty, non-null Rungs list");
            var q = MtLadder.BuildSingleTarget(MtDir.Long, 18000, 0.0, 4.0, 4, 0.25);
            T.Check(!q.Valid, "a zero stop yields an invalid plan");
            var r = MtLadder.BuildSingleTarget(MtDir.Long, 18000, 10.0, 4.0, 0, 0.25);
            T.Check(!r.Valid, "zero contracts yields an invalid plan");
            var s = MtLadder.BuildSingleTarget(MtDir.Long, 18000, 10.0, 0.0, 4, 0.25);
            T.Check(!s.Valid, "a zero targetR yields an invalid plan");
        }

        // Every emitted price is tick-rounded. 3.3R on a 10-point stop = 33 points,
        // so the raw target is 18033.13, which is not on a 0.25 grid -> 18033.25.
        // The expected value is a literal, not a second call to RoundToTick: asserting
        // against the function under test would pass even if the function were wrong.
        {
            var p = MtLadder.BuildSingleTarget(MtDir.Long, 18000.13, 10.0, 3.3, 4, 0.25);
            T.CheckBits(p.Rungs[0].Price, 18033.25, "target is rounded to tick");
            T.CheckBits(p.StopPrice, 17990.25, "stop is rounded to tick too");
        }
    }
}
