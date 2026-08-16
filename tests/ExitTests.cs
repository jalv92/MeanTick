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
            var r1 = T.Rung(p, 0);
            T.Check(r1.Index == 1, "the single rung is 1-based index 1");
            T.CheckBits(r1.Price, 18040.0, "4R above entry");
            T.Check(r1.Quantity == 4, "the single rung carries the whole position");
            T.Check(!r1.IsRunner, "the control arm has no runner");
            T.Check(!r1.IsStructural, "the control arm's rung is not structural");
            T.Check(p.TotalQuantity == 4, "total quantity equals contracts");
        }

        // Short is the exact mirror. Written out rather than derived, because a sign
        // error here is silent: the plan still 'works', it just loses money.
        {
            var p = MtLadder.BuildSingleTarget(MtDir.Short, 18000, 10.0, 4.0, 4, 0.25);
            T.CheckBits(p.StopPrice, 18010.0, "short stop is entry plus StopPoints");
            T.CheckBits(T.Rung(p, 0).Price, 17960.0, "short target is 4R below entry");
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
            T.CheckBits(T.Rung(p, 0).Price, 18033.25, "target is rounded to tick");
            T.CheckBits(p.StopPrice, 17990.25, "stop is rounded to tick too");
        }

        T.Section("ladder K=4");

        // The textbook plan. Long 18000, stop 10, 4 contracts, structural rung at 18055,
        // runner at 18120. Rungs: 1R=18010, 3R=18030, 18055, 18120.
        {
            var p = MtLadder.BuildLadder(MtDir.Long, 18000, 10.0, 1.0, 3.0, 4.0,
                                         new[] { 18055.0 }, 18120, 4, 0.25, 15);
            T.Check(p.Valid, "plan is valid");
            T.Check(p.Rungs.Count == 4, "K=4");
            var r1 = T.Rung(p, 0); var r2 = T.Rung(p, 1); var r3 = T.Rung(p, 2); var r4 = T.Rung(p, 3);
            T.CheckBits(r1.Price, 18010.0, "rung 1 at 1R");
            T.CheckBits(r2.Price, 18030.0, "rung 2 at 3R");
            T.CheckBits(r3.Price, 18055.0, "rung 3 is the structural level");
            T.CheckBits(r4.Price, 18120.0, "rung 4 is the runner");
            T.Check(r3.IsStructural, "rung 3 is flagged structural");
            T.Check(r4.IsRunner, "rung 4 is flagged runner");
            T.Check(r1.Quantity == 1, "each rung carries 1 of 4");
            T.Check(r2.Quantity == 1, "each rung carries 1 of 4");
            T.Check(r3.Quantity == 1, "each rung carries 1 of 4");
            T.Check(r4.Quantity == 1, "each rung carries 1 of 4");
            T.Check(p.TotalQuantity == 4, "quantities sum to contracts");
            foreach (var r in p.Rungs) T.Check(r.Quantity >= 1, "no rung carries zero quantity");
        }

        // Spec 5.2 fallback: no structural level -> rung 3 becomes a fixed 4R. Without this,
        // a day with no clean level silently leaves the ladder at K=3 with an orphan contract.
        {
            var p = MtLadder.BuildLadder(MtDir.Long, 18000, 10.0, 1.0, 3.0, 4.0,
                                         null, 18120, 4, 0.25, 15);
            T.Check(p.Rungs.Count == 4, "still K=4 when the structural level is missing");
            var r3 = T.Rung(p, 2);
            T.CheckBits(r3.Price, 18040.0, "rung 3 falls back to 4R");
            T.Check(!r3.IsStructural, "the fallback rung is not flagged structural");
        }

        // No runner target either -> the runner falls back to the far side of the fallback,
        // and the plan stays whole rather than dropping a contract on the floor.
        {
            var p = MtLadder.BuildLadder(MtDir.Long, 18000, 10.0, 1.0, 3.0, 4.0,
                                         null, double.NaN, 4, 0.25, 15);
            T.Check(p.Rungs.Count == 4, "still K=4 with no structural and no runner target");
            T.Check(p.TotalQuantity == 4, "no contract is lost");
            T.Check(T.Rung(p, 3).IsRunner, "the last rung is still the runner");
        }

        // MinRungTicks: a rung within 15 ticks (3.75 points) of the previous one is DROPPED
        // and its quantity folds into the next. Below that spacing a rung does not clear the
        // house's >=$5/contract bar and is pure commission.
        // Structural at 18032 is 2 points (8 ticks) above rung 2 at 18030 -> folded.
        {
            var p = MtLadder.BuildLadder(MtDir.Long, 18000, 10.0, 1.0, 3.0, 4.0,
                                         new[] { 18032.0 }, 18120, 4, 0.25, 15);
            T.Check(p.Rungs.Count == 3, "a too-close rung is dropped");
            T.Check(T.Rung(p, 2).Quantity == 2, "its quantity folds into the next rung");
            T.Check(p.TotalQuantity == 4, "total quantity is preserved by the fold");
            foreach (var r in p.Rungs) T.Check(r.Quantity >= 1, "no rung carries zero quantity");
        }

        // Runner guard: a runner within MinRungTicks of its predecessor must still
        // survive as its own rung -- it is the rung the whole design exists for, and
        // "tight but present" beats "folded away". Runner at 18055.5 sits 2 ticks
        // (0.5 points) from the structural rung at 18055, well inside the 15-tick
        // minimum that would drop any other rung at that spacing.
        {
            var p = MtLadder.BuildLadder(MtDir.Long, 18000, 10.0, 1.0, 3.0, 4.0,
                                         new[] { 18055.0 }, 18055.5, 4, 0.25, 15);
            T.Check(p.Rungs.Count == 4, "the tight runner is not folded away");
            var runner = T.Rung(p, 3);
            T.CheckBits(runner.Price, 18055.5, "the runner keeps its own price");
            T.Check(runner.IsRunner, "the last rung is still flagged runner");
            T.Check(p.TotalQuantity == 4, "no contract is lost to the near-runner fold");
        }

        // A structural level BEHIND the entry is nonsense and must not become a rung.
        {
            var p = MtLadder.BuildLadder(MtDir.Long, 18000, 10.0, 1.0, 3.0, 4.0,
                                         new[] { 17950.0 }, 18120, 4, 0.25, 15);
            T.CheckBits(T.Rung(p, 2).Price, 18040.0, "a structural level behind entry falls back to 4R");
        }

        // The fix this whole move exists for: a caller handing over ONE already-filtered
        // price could only accept or reject it. p2=18030. 18015 is nearer to entry but short of
        // p2, so it's skipped. That leaves TWO candidates that clear p2 -- 18055 and 18090 --
        // which is the case that actually pins "nearest, not farthest": a mutant that walked the
        // list backward, or returned the LAST clearing candidate instead of the first, would
        // still pass a fixture with only one clearing candidate.
        {
            var p = MtLadder.BuildLadder(MtDir.Long, 18000, 10.0, 1.0, 3.0, 4.0,
                                         new[] { 18015.0, 18055.0, 18090.0 }, 18120, 4, 0.25, 15);
            T.CheckBits(T.Rung(p, 2).Price, 18055.0, "falls through a rejected nearer candidate to the NEAREST clearing one, not the farthest");
            T.Check(T.Rung(p, 2).IsStructural, "the fallen-through-to candidate is still flagged structural");
        }

        // rung1R/rung2R monotonicity: an inverted pair (rung 2 nearer than rung 1) is
        // rejected outright rather than silently misordering the ladder or corrupting the
        // MinRungTicks spacing check, which assumes prices increase from prev to next.
        {
            var p = MtLadder.BuildLadder(MtDir.Long, 18000, 10.0, 3.0, 1.0, 4.0,
                                         new[] { 18055.0 }, 18120, 4, 0.25, 15);
            T.Check(!p.Valid, "rung2R <= rung1R yields an invalid plan");
        }

        // Short is the exact mirror.
        {
            var p = MtLadder.BuildLadder(MtDir.Short, 18000, 10.0, 1.0, 3.0, 4.0,
                                         new[] { 17945.0 }, 17880, 4, 0.25, 15);
            T.CheckBits(p.StopPrice, 18010.0, "short stop");
            T.CheckBits(T.Rung(p, 0).Price, 17990.0, "short rung 1 at 1R");
            T.CheckBits(T.Rung(p, 1).Price, 17970.0, "short rung 2 at 3R");
            T.CheckBits(T.Rung(p, 2).Price, 17945.0, "short structural rung");
            T.CheckBits(T.Rung(p, 3).Price, 17880.0, "short runner");
        }

        // Quantity remainder: 6 contracts over 4 rungs is 2/2/1/1, never 1/1/1/1 with two lost.
        {
            var p = MtLadder.BuildLadder(MtDir.Long, 18000, 10.0, 1.0, 3.0, 4.0,
                                         new[] { 18055.0 }, 18120, 6, 0.25, 15);
            T.Check(p.TotalQuantity == 6, "6 contracts are all allocated");
            var r1 = T.Rung(p, 0); var r2 = T.Rung(p, 1); var r3 = T.Rung(p, 2); var r4 = T.Rung(p, 3);
            T.Check(r1.Quantity == 2 && r2.Quantity == 2, "the remainder goes to the near rungs");
            T.Check(r3.Quantity == 1 && r4.Quantity == 1, "the far rungs take the base share");
        }

        // Fewer contracts than rungs: K shrinks to the contract count rather than emitting
        // zero-quantity orders, which NT8 would reject at submission.
        {
            var p = MtLadder.BuildLadder(MtDir.Long, 18000, 10.0, 1.0, 3.0, 4.0,
                                         new[] { 18055.0 }, 18120, 2, 0.25, 15);
            T.Check(p.Rungs.Count == 2, "K shrinks to the contract count");
            T.Check(T.Rung(p, p.Rungs.Count - 1).IsRunner, "the last surviving rung is still the runner");
            T.Check(p.TotalQuantity == 2, "quantities still sum to contracts");
            foreach (var r in p.Rungs) T.Check(r.Quantity >= 1, "no rung carries zero quantity");
        }

        // Off-grid inputs: the K=4 path must round every emitted price too, not just the
        // control arm from Task 6. entry=18000.13 and runner=18120.07 are not on the 0.25
        // grid; if RoundToTick were skipped anywhere in BuildLadder these numbers would show
        // raw fractional prices no MNQ order could actually submit at.
        {
            var p = MtLadder.BuildLadder(MtDir.Long, 18000.13, 10.0, 1.0, 3.0, 4.0,
                                         null, 18120.07, 4, 0.25, 15);
            T.CheckBits(p.StopPrice, 17990.25, "off-grid stop rounds to tick");
            T.CheckBits(T.Rung(p, 0).Price, 18010.25, "off-grid rung 1 rounds to tick");
            T.CheckBits(T.Rung(p, 1).Price, 18030.25, "off-grid rung 2 rounds to tick");
            T.CheckBits(T.Rung(p, 2).Price, 18040.25, "off-grid fallback rung 3 rounds to tick");
            T.CheckBits(T.Rung(p, 3).Price, 18120.0,  "off-grid runner rounds to tick");
            T.Check(p.TotalQuantity == 4, "off-grid fixture still allocates all contracts");
        }

        // Round before comparing, never after. Structural 18030.1 lies 0.1 points beyond
        // the RAW rung-2 price, but RoundToTick(18030.1) is 18030.0 -- exactly p2. Compared
        // raw-vs-rounded this passes the "beyond p2" check and rounds down onto a duplicate
        // of rung 2. MinRungTicks=0 disables the spacing fold on purpose, so a duplicate
        // would surface as its own rung here instead of being masked by the default fold.
        {
            var p = MtLadder.BuildLadder(MtDir.Long, 18000, 10.0, 1.0, 3.0, 4.0,
                                         new[] { 18030.1 }, 18120, 4, 0.25, 0);
            var r2 = T.Rung(p, 1);
            var r3 = T.Rung(p, 2);
            T.CheckBits(r3.Price, 18040.0, "rung 3 falls back to 4R instead of duplicating rung 2");
            T.Check(r3.Price != r2.Price, "rung 3 is not a duplicate of rung 2");
            T.Check(!r3.IsStructural, "the fallback rung is not flagged structural");
            T.Check(p.Rungs.Count == 4, "no extra rung is emitted");
            T.Check(p.TotalQuantity == 4, "quantities still sum to contracts");
        }

        T.Section("golden ladder CSV (three named fixtures for the Python mirror)");
        {
            string csvPath = GoldenCsvPath();
            using (var w = new System.IO.StreamWriter(csvPath, false))
            {
                w.WriteLine("fixture,dir,entry,stop,rung_index,price,qty,is_structural,is_runner");

                // rung1_only: fewer contracts than rungs collapses the ladder to a single
                // surviving rung -- still flagged as the runner, since it is the last one left.
                var rung1Only = MtLadder.BuildLadder(MtDir.Long, 18000, 10.0, 1.0, 3.0, 4.0,
                                                     new[] { 18055.0 }, 18120, 1, 0.25, 15);
                WriteGoldenLadderRow(w, "rung1_only", MtDir.Long, 18000, rung1Only);

                // all_rungs: the full K=4 textbook plan, with an off-grid entry and runner so
                // this fixture actually exercises RoundToTick end to end -- on-grid inputs would
                // pass even if a rounding call were silently dropped.
                var allRungs = MtLadder.BuildLadder(MtDir.Long, 18000.13, 10.0, 1.0, 3.0, 4.0,
                                                    null, 18120.07, 4, 0.25, 15);
                WriteGoldenLadderRow(w, "all_rungs", MtDir.Long, 18000.13, allRungs);

                // stop_between_rungs: the structural level sits inside MinRungTicks of rung 2
                // and is folded away, so K drops to 3 and the runner absorbs the folded qty.
                var stopBetween = MtLadder.BuildLadder(MtDir.Long, 18000, 10.0, 1.0, 3.0, 4.0,
                                                       new[] { 18032.0 }, 18120, 4, 0.25, 15);
                WriteGoldenLadderRow(w, "stop_between_rungs", MtDir.Long, 18000, stopBetween);
            }
            T.Check(System.IO.File.Exists(csvPath), "golden_ladder.csv was written");
        }
    }

    // Resolves to the tests/ source directory regardless of the process's working
    // directory, so the golden CSV always lands next to this file in the repo -- not
    // in whatever bin/ folder `dotnet run` happens to use as cwd.
    private static string GoldenCsvPath([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisFile), "golden_ladder.csv");
    }

    private static void WriteGoldenLadderRow(System.IO.StreamWriter w, string fixture, MtDir dir, double entry, MtExitPlan p)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var r in p.Rungs)
        {
            w.WriteLine(string.Join(",", new[]
            {
                fixture,
                dir.ToString(),
                entry.ToString("R", ic),
                p.StopPrice.ToString("R", ic),
                r.Index.ToString(ic),
                r.Price.ToString("R", ic),
                r.Quantity.ToString(ic),
                r.IsStructural ? "true" : "false",
                r.IsRunner ? "true" : "false"
            }));
        }
    }
}
