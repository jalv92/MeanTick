// MeanTickExits.cs — Exit planning for MeanTick. Same purity contract as
// MeanTickCore.cs: zero `using NinjaTrader.*`, namespace MeanTickCore, C# 7.3 only.
//
// Arithmetic order is fixed and is not to be reassociated for elegance: the Python
// mirror reproduces these numbers bit-for-bit against golden_ladder.csv, and a
// reordered sum drifts the last bit, which is exactly what CheckBits exists to catch.
using System;
using System.Collections.Generic;

namespace MeanTickCore
{
    public enum MtExitMode { SingleTarget, Ladder }

    public struct MtRung
    {
        public int    Index;
        public double Price;
        public int    Quantity;
        public bool   IsStructural;
        public bool   IsRunner;
    }

    public struct MtExitPlan
    {
        public bool         Valid;
        public double       StopPrice;
        public List<MtRung> Rungs;
        public int          TotalQuantity;
    }

    public static class MtLadder
    {
        // Spec 5.1. The control arm: one target at SingleTargetR, whole position.
        // This is what the tutorials actually teach, and it is the arm every ladder
        // number is reported against -- without it, no ladder result means anything.
        public static MtExitPlan BuildSingleTarget(MtDir dir, double entry, double stopPoints,
                                                   double targetR, int contracts, double tickSize)
        {
            var plan = new MtExitPlan { Rungs = new List<MtRung>() };
            if (dir == MtDir.None || stopPoints <= 0.0 || contracts <= 0 || targetR <= 0.0)
                return plan;

            int sign = dir == MtDir.Long ? 1 : -1;

            plan.StopPrice = MtMath.RoundToTick(entry - sign * stopPoints, tickSize);
            plan.Rungs.Add(new MtRung
            {
                Index        = 1,
                Price        = MtMath.RoundToTick(entry + sign * stopPoints * targetR, tickSize),
                Quantity     = contracts,
                IsStructural = false,
                IsRunner     = false
            });
            plan.TotalQuantity = contracts;
            plan.Valid         = true;
            return plan;
        }

        // Spec 5.2. Two fixed rungs reproduce the two targets the source actually teaches
        // and need zero detection; two structural rungs implement the reason he gives for
        // taking partials at all -- "just in case we didn't make it to that gap" (V4 [7:33]).
        // The rungs insure a far magnet that may not be reached.
        //
        // The ladder does NOT reduce risk. All rungs share one entry price and one stop, so
        // the maximum loss is identical to the control arm. Rungs trade tail upside for hit
        // rate; any claim that they are 'safer' is wrong (spec 8).
        public static MtExitPlan BuildLadder(MtDir dir, double entry, double stopPoints,
                                             double rung1R, double rung2R, double rung3FallbackR,
                                             IList<double> structuralCandidates, double runnerPrice,
                                             int contracts, double tickSize, int minRungTicks)
        {
            var plan = new MtExitPlan { Rungs = new List<MtRung>() };
            if (dir == MtDir.None || stopPoints <= 0.0 || contracts <= 0) return plan;
            // rung2R must be strictly beyond rung1R -- design.md 5.2's table is swept as a
            // pair, and an inverted pair puts rung 2 nearer than rung 1, which also makes the
            // MinRungTicks spacing fold below measure the wrong distance.
            if (rung2R <= rung1R) return plan;

            int sign = dir == MtDir.Long ? 1 : -1;
            plan.StopPrice = MtMath.RoundToTick(entry - sign * stopPoints, tickSize);

            double p1 = MtMath.RoundToTick(entry + sign * stopPoints * rung1R, tickSize);
            double p2 = MtMath.RoundToTick(entry + sign * stopPoints * rung2R, tickSize);
            double fallback = MtMath.RoundToTick(entry + sign * stopPoints * rung3FallbackR, tickSize);

            // A caller that hands over only ONE already-filtered price could only ever accept
            // or reject it -- never fall through to the next-best level once the nearest
            // candidate turned out to be short of rung 2. structuralCandidates is expected
            // nearest-to-entry first (MtDetect.FindStructuralCandidates sorts it that way);
            // walking it in order and taking the first that clears p2 also gives the nearest
            // candidate to p2 itself, since every candidate beyond p2 is further from entry
            // than p2 is, so distance-from-entry order and distance-from-p2 order agree there.
            // Round before comparing, never after -- a raw candidate that lies fractionally
            // beyond p2 but ROUNDS onto p2 (or short of it) must not be treated as "beyond".
            double structuralRounded = double.NaN;
            if (structuralCandidates != null)
            {
                for (int ci = 0; ci < structuralCandidates.Count; ci++)
                {
                    double cand = MtMath.RoundToTick(structuralCandidates[ci], tickSize);
                    if (!double.IsNaN(cand) && sign * (cand - p2) > 0.0)
                    {
                        structuralRounded = cand;
                        break;
                    }
                }
            }
            double runnerRounded = MtMath.RoundToTick(runnerPrice, tickSize);

            bool structuralOk = !double.IsNaN(structuralRounded);
            double p3 = structuralOk ? structuralRounded : fallback;

            bool runnerOk = !double.IsNaN(runnerRounded) && sign * (runnerRounded - p3) > 0.0;
            double p4 = runnerOk
                ? runnerRounded
                : MtMath.RoundToTick(p3 + sign * stopPoints * rung2R, tickSize);

            var prices     = new double[] { p1, p2, p3, p4 };
            // Slot 3 here holds runnerOk, not "is rung 3 structural" -- it is only safe
            // because index 3 is only ever consumed when last == true, where the
            // "&& !last" below zeroes it back out.
            var structural = new bool[]   { false, false, structuralOk, runnerOk };

            // Allocate first, then drop: a rung that is dropped for spacing folds its
            // quantity forward so the position is always fully covered.
            // K-shrink keeps the FRONT of the price array (the two fixed rungs), so a
            // 1- or 2-contract trade never reaches the structural or runner target --
            // spec-compliant, and Javier's configured size (4-8 MNQ) never hits this path.
            int k = Math.Min(4, contracts);
            var qty = new int[4];
            int baseQty = contracts / k;
            int extra   = contracts - baseQty * k;
            for (int i = 0; i < k; i++) qty[i] = baseQty + (i < extra ? 1 : 0);

            double minGap = minRungTicks * tickSize;
            double prev   = entry;
            int carried   = 0;

            for (int i = 0; i < k; i++)
            {
                bool last = i == k - 1;
                // The runner is never dropped: it is the rung the whole design is for.
                if (!last && Math.Abs(prices[i] - prev) < minGap)
                {
                    carried += qty[i];
                    continue;
                }

                plan.Rungs.Add(new MtRung
                {
                    Index        = plan.Rungs.Count + 1,
                    Price        = prices[i],
                    Quantity     = qty[i] + carried,
                    IsStructural = structural[i] && !last,
                    IsRunner     = last
                });
                carried = 0;
                prev    = prices[i];
            }

            for (int i = 0; i < plan.Rungs.Count; i++) plan.TotalQuantity += plan.Rungs[i].Quantity;
            plan.Valid = plan.TotalQuantity == contracts;
            return plan;
        }
    }
}
