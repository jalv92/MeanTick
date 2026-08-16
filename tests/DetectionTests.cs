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
        // Body top = max(O,C) = 18010, upper wick = 18030-18010 = 20.
        // Body bottom = min(O,C) = 18000, lower wick = 18000-17990 = 10 -> ratio 10/20 = 0.5 > 0.33.
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

        // Mirror: a bullish-shaped wick (long lower wick, small upper) that closes DOWN
        // is not a bullish rejection block. Body top = max(O,C) = 18010, upper wick = 1;
        // body bottom = min(O,C) = 18000, lower wick = 20 -- textbook bullish shape, but
        // the close is on the wrong side.
        {
            MtArray a;
            bool ok = MtDetect.TryRejectionBlock(B(18010, 18011, 17980, 18000), 42, MtDir.Long,
                                                 0.25, 8, 0.33, out a);
            T.Check(!ok, "bullish rejection requires a bullish close");
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

        T.Section("4H fair value gap");

        // Ported arithmetic (VeeSnapCore.cs:455-467), exercised directly against the new
        // (a,b,c) signature: gap up = demand below (bullish), gap down = supply above
        // (bearish). Bar b is the FVG's middle candle and is never touched by the gap
        // comparison -- that matches the source, which reads only the outer two bars.
        {
            MtArray a;
            bool ok = MtDetect.TryFvg(B(18018, 18030, 18015, 18028), B(18028, 18060, 18026, 18058),
                                      B(18058, 18075, 18035, 18070), 7, 0.0, 0.25, out a);
            T.Check(ok, "bullish gap (low[c] > high[a]) qualifies");
            T.Check(a.Dir == MtDir.Long, "gap up is a Long array");
            T.CheckBits(a.Top, 18035.0, "top is the newest bar's low");
            T.CheckBits(a.Bottom, 18030.0, "bottom is the oldest bar's high");
            T.CheckBits(a.Level, 18032.5, "level is the consequent encroachment, 50% of the gap");
            T.Check(a.Kind == MtArrayKind.Fvg, "kind is fvg");
        }

        // Mirror bearish gap: high[c] < low[a].
        {
            MtArray a;
            bool ok = MtDetect.TryFvg(B(18100, 18110, 18090, 18095), B(18085, 18090, 18070, 18075),
                                      B(18065, 18080, 18055, 18060), 7, 0.0, 0.25, out a);
            T.Check(ok, "bearish gap (high[c] < low[a]) qualifies");
            T.Check(a.Dir == MtDir.Short, "gap down is a Short array");
            T.CheckBits(a.Level, 18085.0, "level is the consequent encroachment, 50% of the gap");
        }

        // A gap narrower than minHeight is not a qualifying array -- same bars as the
        // bullish case above (gap = 5), gated at minHeight = 10.
        {
            MtArray a;
            bool ok = MtDetect.TryFvg(B(18018, 18030, 18015, 18028), B(18028, 18060, 18026, 18058),
                                      B(18058, 18075, 18035, 18070), 7, 10.0, 0.25, out a);
            T.Check(!ok, "gap smaller than minHeight is rejected");
        }

        // Overlapping candles leave no gap on either side.
        {
            MtArray a;
            bool ok = MtDetect.TryFvg(B(18000, 18010, 17995, 18005), B(18005, 18015, 17998, 18008),
                                      B(18000, 18012, 17994, 18006), 7, 0.0, 0.25, out a);
            T.Check(!ok, "overlapping bars produce no fair value gap");
        }

        T.Section("4H order block via FVG");

        // Bullish FVG across bars 5,6,7: low[7] > high[5]. The order block is the LAST
        // down-close candle at or before bar 5. Bar 4 closes up, bar 3 closes DOWN -> bar 3.
        // Bar 3: O=18010 H=18015 L=17995 C=18000 -> mean threshold = (18015+17995)/2 = 18005.
        {
            var bars = new List<MtBar>();
            bars.Add(B(17980, 17990, 17970, 17985)); // 0
            bars.Add(B(17985, 17995, 17975, 17990)); // 1
            bars.Add(B(17990, 18000, 17985, 17995)); // 2
            bars.Add(B(18010, 18015, 17995, 18000)); // 3  <- last down-close before the impulse
            bars.Add(B(18000, 18020, 17998, 18018)); // 4  up close
            bars.Add(B(18018, 18030, 18015, 18028)); // 5  FVG first bar
            bars.Add(B(18028, 18060, 18026, 18058)); // 6
            bars.Add(B(18058, 18075, 18035, 18070)); // 7  low 18035 > high[5] 18030 -> bullish FVG

            MtArray ob;
            bool ok = MtDetect.TryOrderBlockFromFvg(bars, 5, MtDir.Long, 0, 0.25, out ob);
            T.Check(ok, "bullish order block is located from the FVG, not from a displacement threshold");
            T.Check(ob.BarIndex == 3, "it is the LAST down-close candle before the impulse");
            T.CheckBits(ob.Level, 18005.0, "level is the mean threshold, 50% of the candle's range");
            T.Check(ob.Kind == MtArrayKind.OrderBlock, "kind is order block");
        }

        // No opposite-close candle anywhere before the impulse -> no order block, and the
        // function must say so rather than returning bar 0 as a consolation prize.
        {
            var bars = new List<MtBar>();
            for (int i = 0; i < 8; i++) bars.Add(B(18000 + i, 18005 + i, 17999 + i, 18004 + i));
            MtArray ob;
            bool ok = MtDetect.TryOrderBlockFromFvg(bars, 5, MtDir.Long, 0, 0.25, out ob);
            T.Check(!ok, "no opposite-close candle means no order block");
        }

        // dir == MtDir.None is an explicit guard, not a fall-through: it must return
        // false outright rather than searching the bars for an "opposite" of nothing.
        {
            var bars = new List<MtBar>();
            bars.Add(B(17980, 17990, 17970, 17985)); // 0
            bars.Add(B(17985, 17995, 17975, 17990)); // 1
            bars.Add(B(17990, 18000, 17985, 17995)); // 2
            MtArray ob;
            bool ok = MtDetect.TryOrderBlockFromFvg(bars, 1, MtDir.None, 0, 0.25, out ob);
            T.Check(!ok, "MtDir.None is rejected outright, not searched");
        }

        T.Section("HTF qualification");

        // Freshness: formed within the last FreshBars4H candles.
        {
            var a = new MtArray { Valid = true, BarIndex = 100, Level = 18000, Dir = MtDir.Long };
            T.Check( MtDetect.IsQualifiedHtf(a, 104, 18000, 5, 40.0, 1.5), "4 bars old is fresh at FreshBars=5");
            T.Check( MtDetect.IsQualifiedHtf(a, 105, 18000, 5, 40.0, 1.5), "exactly FreshBars old is still fresh (boundary is inclusive)");
            T.Check(!MtDetect.IsQualifiedHtf(a, 106, 18000, 5, 40.0, 1.5), "6 bars old is stale");
        }

        // A non-positive ATR must fail closed even at zero distance -- without the guard,
        // proximityAtrMult * 0.0 is 0.0 and |price - level| <= 0.0 would pass at price ==
        // level. The house ATR crosses sessions and is cold at the start of a run, and
        // MeanTick's proximity gate fires at 09:30, so this is a real state, not a hypothetical.
        {
            var a = new MtArray { Valid = true, BarIndex = 100, Level = 18000, Dir = MtDir.Long };
            T.Check(!MtDetect.IsQualifiedHtf(a, 101, 18000, 5, 0.0, 1.5), "zero ATR never qualifies, even at zero distance");
        }

        // Proximity: |price - level| <= ProximityAtrMult * ATR(14) on the 4H series.
        // ATR 40, mult 1.5 -> 60 points of tolerance.
        {
            var a = new MtArray { Valid = true, BarIndex = 100, Level = 18000, Dir = MtDir.Long };
            T.Check( MtDetect.IsQualifiedHtf(a, 101, 18059, 5, 40.0, 1.5), "59 points away is within 1.5*ATR");
            T.Check(!MtDetect.IsQualifiedHtf(a, 101, 18061, 5, 40.0, 1.5), "61 points away is too far");
            T.Check( MtDetect.IsQualifiedHtf(a, 101, 18060, 5, 40.0, 1.5), "exactly 1.5*ATR passes");
        }

        // An invalid array never qualifies, whatever the distances say.
        {
            var a = new MtArray { Valid = false, BarIndex = 100, Level = 18000 };
            T.Check(!MtDetect.IsQualifiedHtf(a, 101, 18000, 5, 40.0, 1.5), "an invalid array never qualifies");
        }

        T.Section("Gate 2 -- 15m rejection off the 4H level");

        // TryRejectionOffLevel is the WHOLE Gate 2 rule (spec 4.2): touch, close-outside-in-
        // the-rejection-direction, and wick geometry. The close-direction condition (2) was
        // missing entirely before this section existed -- these three cases pin it.
        {
            // Accept: touches, closes outside (above) the level in the rejection direction,
            // and leaves a clean one-sided wick. Same bar shape as the "bullish rejection
            // block qualifies" fixture above; entry (block.Level) is the wick midpoint,
            // independent of exactly where the 4H level sits inside the wick.
            var htf = new MtArray { Valid = true, Dir = MtDir.Long, Level = 17995 };
            MtArray block;
            bool ok = MtDetect.TryRejectionOffLevel(B(18000, 18025, 17980, 18020), 42, htf, 0.25, 8, 0.33, out block);
            T.Check(ok, "touches + closes outside + clean wick accepts");
            T.CheckBits(block.Level, 17990.0, "entry price is still the wick midpoint");
        }
        {
            // Reject: the counter-example that shipped past every prior review. 4H level
            // 18000 (Long -- rejected upward). 15m O=17990 H=18001 L=17960 C=17999: the range
            // touches (High >= 18000), the wick ratio passes (opposite=1, reject=30, ratio
            // 0.033), but the CLOSE (17999) never got back above the level it supposedly
            // rejected upward from -- without condition 2 this armed a Long on a candle that
            // closed BELOW its own level.
            var htf = new MtArray { Valid = true, Dir = MtDir.Long, Level = 18000 };
            MtArray block;
            bool ok = MtDetect.TryRejectionOffLevel(B(17990, 18001, 17960, 17999), 42, htf, 0.25, 8, 0.33, out block);
            T.Check(!ok, "a wick that never closes back outside the level is rejected");
        }
        {
            // Reject: the candle's range never reaches the level at all (condition 1).
            var htf = new MtArray { Valid = true, Dir = MtDir.Long, Level = 18000 };
            MtArray block;
            bool ok = MtDetect.TryRejectionOffLevel(B(18010, 18020, 18005, 18015), 42, htf, 0.25, 8, 0.33, out block);
            T.Check(!ok, "a candle whose range never touches the level is rejected");
        }

        T.Section("HTF candidate selection");

        // FindQualifiedHtfCandidates moved out of the shell (was untested there, and the shell
        // cannot compile into this project) because it IS a decision, not mechanics: which of
        // several qualifying arrays a day trades off. Two things pinned here: the per-bar-type
        // priority (a bar that qualifies as both a rejection block AND an FVG returns the
        // rejection block first) and the "most-recent-qualifying-bar wins" tie-break across bars.

        // Same bar, two qualifying types at once. Bar 2 is a clean Short rejection block
        // (bodyTop=18010, reject=20, opposite=2, ratio=0.1, level=18020 -- same shape as the
        // rejection-block section above) AND the 'c' bar of a bearish FVG against bar 0
        // (c.High=18030 < a.Low=18040, gap=10, level=18035). Bar 1 is unread by TryFvg and
        // produces no order block (its own Close is not opposite-closing), so exactly these
        // two candidates exist.
        {
            var bars = new List<MtBar> {
                B(18050, 18055, 18040, 18048),   // a
                B(18045, 18048, 18035, 18040),   // b (unread by TryFvg)
                B(18010, 18030, 17998, 18000),   // c -- rejection block AND FVG's newest bar
            };
            var found = MtDetect.FindQualifiedHtfCandidates(bars, 18025, 0.25, 5, 100.0, 1.5, 8, 0.33);
            T.Check(found.Count == 2, "both a rejection block and an FVG qualify on the same bar");
            T.Check(found[0].Kind == MtArrayKind.RejectionBlock, "rejection block wins the per-bar-type priority");
            T.Check(found[1].Kind == MtArrayKind.Fvg, "the FVG on the same bar is still returned, just second");
        }

        // Two DIFFERENT bars, each independently a qualifying rejection block: an older one at
        // index 3 (Long, level 17980) and the newest, current bar at index 7 (Short, level
        // 18020). Every other bar is an inert doji (Open==Close fails both RB directions, and
        // its range is too tight to ever pair into an FVG with its neighbors) so the two real
        // candidates are the only output -- this isolates the tie-break from the priority rule
        // above instead of re-testing it.
        {
            MtBar filler = B(18000, 18000.5, 17999.5, 18000);
            var bars = new List<MtBar> {
                filler, filler, filler,
                B(18000, 18003, 17960, 18002),   // index 3: older Long rejection block, level 17980
                filler, filler, filler,
                B(18010, 18030, 17998, 18000),   // index 7 (current): newer Short rejection block, level 18020
            };
            var found = MtDetect.FindQualifiedHtfCandidates(bars, 18000, 0.25, 5, 100.0, 1.5, 8, 0.33);
            T.Check(found.Count == 2, "only the two real candidates are found, fillers produce nothing");
            T.Check(found[0].BarIndex == 7, "the most recent qualifying bar wins the top slot");
            T.CheckBits(found[0].Level, 18020.0, "...and it is the newer level, not the older one");
            T.Check(found[1].BarIndex == 3, "the older candidate is still found, just not first");
        }

        T.Section("session window");

        T.Check(MtSession.Window(9 * 3600 + 29 * 60) == MtWindowState.Closed,      "09:29 is closed");
        T.Check(MtSession.Window(9 * 3600 + 30 * 60) == MtWindowState.Open,        "09:30 opens the window");
        T.Check(MtSession.Window(9 * 3600 + 59 * 60) == MtWindowState.Open,        "09:59 is still the first window");
        T.Check(MtSession.Window(10 * 3600)          == MtWindowState.SecondChance,"10:00 is the second chance");
        T.Check(MtSession.Window(10 * 3600 + 59 * 60)== MtWindowState.SecondChance,"10:59 is still second chance");
        T.Check(MtSession.Window(11 * 3600)          == MtWindowState.Closed,      "11:00 closes the window");

        // Window() is structurally exhaustive over every int (the branches are two
        // one-sided comparisons with no gap between them), but that was an inference
        // from reading the code, not a fact pinned by a test. Cover both tails.
        T.Check(MtSession.Window(-1)     == MtWindowState.Closed, "negative seconds-of-day is closed");
        T.Check(MtSession.Window(90000)  == MtWindowState.Closed, "seconds-of-day past 24h is closed");

        // London is the SOURCE's 02:00-05:00, deliberately different from VeeSnap's
        // 03:00-09:30 and Apertura4HMSS's 03:00-05:00. Spec 5.3 records the divergence
        // so a future reader does not 'harmonize' it and silently move the runner's target.
        T.Check(!MtSession.InLondon(1 * 3600 + 59 * 60), "01:59 is before London");
        T.Check( MtSession.InLondon(2 * 3600),           "02:00 starts London");
        T.Check( MtSession.InLondon(4 * 3600 + 59 * 60), "04:59 is inside London");
        T.Check(!MtSession.InLondon(5 * 3600),           "05:00 ends London (exclusive)");

        // TTL is wall-clock from placement, capped by the window close.
        T.Check(!MtSession.IsExpired(10 * 3600, 9 * 3600 + 30 * 60, 90), "30 min in, a 90 min TTL is alive");
        T.Check( MtSession.IsExpired(11 * 3600, 9 * 3600 + 30 * 60, 90), "the window close expires it regardless");
        T.Check( MtSession.IsExpired(10 * 3600 + 31 * 60, 9 * 3600, 90), "91 min after placement it is expired");

        // The three asserts above are ordinary cases, but they do not pin "whichever
        // comes first" -- they are mutually confounded. The 11:00 case in particular
        // LOOKS like it isolates the window-close branch, but a 90-minute TTL placed
        // at 09:30 happens to expire exactly AT 11:00 too (09:30 + 90min = 11:00), so
        // both the window-close check and the elapsed check independently return true
        // there. Deleting the window-close early return, or loosening its >= to a
        // narrower comparison, would not fail any of the three asserts above. The
        // three below isolate each branch on purpose:
        {
            // 1. Window-close only: elapsed (1s) is nowhere near the huge TTL limit
            //    (999999 min), so only the window-close line can produce `true` here.
            //    This dies if that line is removed.
            T.Check(MtSession.IsExpired(MtSession.SessionEndSec, MtSession.SessionEndSec - 1, 999999),
                    "window close expires an order regardless of a huge TTL");

            // 2. Elapsed >= on its exact boundary, inside the open window so the
            //    window-close branch cannot fire: elapsed is exactly ttl*60 (60s = 1min).
            //    This dies if >= becomes >.
            T.Check(MtSession.IsExpired(9 * 3600 + 31 * 60, 9 * 3600 + 30 * 60, 1),
                    "elapsed exactly equal to the TTL is expired (boundary is inclusive)");

            // 3. The other side of that boundary: one second short of the TTL.
            T.Check(!MtSession.IsExpired(9 * 3600 + 31 * 60 - 1, 9 * 3600 + 30 * 60, 1),
                    "one second short of the TTL is still alive");
        }
    }
}
