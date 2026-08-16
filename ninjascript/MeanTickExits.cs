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
    }
}
