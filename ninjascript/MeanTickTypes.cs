// MeanTickTypes.cs — the primitives both pure engines share: bars, swings, the
// house ATR, the house pivot detector, and the arithmetic helpers.
//
// ZERO `using NinjaTrader.*`, own namespace `MeanTickCore`, C# 7.3 only. NT8
// ships its own types under common names and compiles every file under
// bin/Custom into ONE assembly, so a NinjaTrader using here is how a CS0101
// duplicate-type clash starts. Purity is also what lets this exact file compile
// both inside that assembly and in the net8 test runner
// (tests/MeanTick.Tests.csproj), which has no NinjaTrader assemblies on its
// reference path — the behaviour has to be identical in both.
//
// Everything here is ported from PatternZoneCore.cs:15-124 and its sources.
// The ports are verbatim on purpose: the ATR recursion and the pivot reveal
// rule are pinned by another strategy's test suite and mirrored in Python, so
// "improving" either of them here silently forks two other implementations.
using System;
using System.Collections.Generic;

namespace MeanTickCore
{
    public struct MtBar
    {
        public DateTime Time;
        public double Open, High, Low, Close, Volume;
    }

    public struct MtSwing
    {
        public int BarIndex;
        public DateTime Time;
        public double Price;
        public bool IsHigh;
    }

    // House Wilder recursion (PatternZoneCore.cs:53-77, itself from
    // PullbackZoneStrategy.cs:1170-1180): seed = tr[0] itself (a running mean of
    // one sample), TR uses the previous bar's close, and it CROSSES SESSIONS —
    // it never resets. The first ~period bars of a session therefore carry the
    // overnight gap in their true range; every calibration measurement flags
    // those bars rather than pretending the recursion resets.
    public sealed class WilderAtr
    {
        private readonly int _period;
        private int _n;
        private double _value;
        private double _prevClose;

        public WilderAtr(int period)
        {
            _period = period;
        }

        public void Update(MtBar bar)
        {
            double tr = _n == 0
                ? bar.High - bar.Low
                : Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - _prevClose), Math.Abs(bar.Low - _prevClose)));
            _value = _n < _period ? (_value * _n + tr) / (_n + 1) : _value + (tr - _value) / _period;
            _prevClose = bar.Close;
            _n++;
        }

        public double Value { get { return _value; } }

        // Warm, not merely non-zero: a partially warmed ATR is positive and
        // shrinks every ATR-scaled gate proportionally, which reads as "the
        // strategy took a trade it should not have" rather than as a warmup bug.
        public bool IsWarm { get { return _n >= _period && _value > 0; } }

        public int BarsFed { get { return _n; } }
    }

    // House reveal rule (PatternZoneCore.cs:83-124, from
    // PullbackZoneStrategy.cs:459-471): the pivot sits `strength` bars back and
    // is confirmed once its window fills; strict-unique max/min over the
    // 2*strength+1 window — an equal extreme anywhere else in the window rejects
    // it. One bar can confirm a high AND a low at once.
    public sealed class SwingDetector
    {
        private readonly int _strength;
        private readonly int _windowSize;
        private readonly List<MtBar> _window = new List<MtBar>();

        public SwingDetector(int strength)
        {
            _strength = strength;
            _windowSize = 2 * strength + 1;
        }

        public List<MtSwing> Update(MtBar bar, int barIndex)
        {
            _window.Add(bar);
            if (_window.Count > _windowSize)
                _window.RemoveAt(0);

            var result = new List<MtSwing>();
            if (_window.Count < _windowSize)
                return result;

            MtBar candidate = _window[_strength];
            int candidateIndex = barIndex - _strength;
            double ph = candidate.High, pl = candidate.Low;
            bool hiMax = true, loMin = true;
            int hiEq = 0, loEq = 0;
            for (int i = 0; i < _window.Count; i++)
            {
                MtBar w = _window[i];
                if (w.High > ph) hiMax = false;
                else if (w.High == ph) hiEq++;
                if (w.Low < pl) loMin = false;
                else if (w.Low == pl) loEq++;
            }
            if (hiMax && hiEq == 1)
                result.Add(new MtSwing { BarIndex = candidateIndex, Time = candidate.Time, Price = ph, IsHigh = true });
            if (loMin && loEq == 1)
                result.Add(new MtSwing { BarIndex = candidateIndex, Time = candidate.Time, Price = pl, IsHigh = false });
            return result;
        }

        public void Reset()
        {
            _window.Clear();
        }
    }

    public static class MtMath
    {
        // Hand-rolled so both engines round identically. NT8's
        // Instrument.MasterInstrument.RoundToTickSize must NEVER touch a price
        // computed here: the Python mirror cannot call it, and a rounding that
        // disagrees drifts the two implementations one tick at a time.
        // Python mirror: math.floor(px / tick + 0.5) * tick
        public static double RoundToTick(double px, double tick)
        {
            if (tick <= 0)
                return px;
            return Math.Floor(px / tick + 0.5) * tick;
        }

        public static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public static double Lerp(double a, double b, double u)
        {
            return a + (b - a) * u;
        }

        // Maps a score in [lo, hi] onto [0, 1], clamped. `hi <= lo` collapses to
        // 0 rather than dividing by zero — a degenerate configuration must
        // behave like "never aggressive", not like NaN, because every
        // comparison against NaN is false and the guards would all fail open.
        public static double Unit(double v, double lo, double hi)
        {
            if (hi <= lo)
                return 0.0;
            return Clamp((v - lo) / (hi - lo), 0.0, 1.0);
        }
    }
}
