// probe/LadderProbe.cs — THROWAWAY. Answers one question: do three independent
// entry signals each keep their own bracket when the first one closes?
// Not a model, not a candidate, not deployed anywhere permanent.
#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class LadderProbe : Strategy
    {
        // ponytail: reuses PR1's original distance for the pooled test — that
        // test is about OCO grouping, not the stop's price.
        private const int PooledStopTicks = 40;

        private bool _armed;

        [NinjaScriptProperty]
        [Display(Name = "PooledStopTest", GroupName = "Parameters", Order = 1)]
        public bool PooledStopTest
        { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                        = "LadderProbe";
                Calculate                   = Calculate.OnBarClose;
                EntriesPerDirection         = 3;
                EntryHandling               = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds   = 30;
                BarsRequiredToTrade         = 20;
                TraceOrders                 = true;
                PooledStopTest              = false;
                // Explicit, not decorative: ByStrategyPosition resizes brackets to the
                // whole strategy position — the exact collapse this probe measures. Left
                // implicit, a platform-default change could produce a false "collapsed"
                // verdict that isn't NT8 semantics at all.
                StopTargetHandling          = StopTargetHandling.PerEntryExecution;
            }
            else if (State == State.DataLoaded)
            {
                // SetDefaults only seeds this — the Strategies-window "Stop & target
                // submission" property can override it, so this is the only way to
                // know what actually ran (managed_approach.md:131).
                Print(string.Format("Effective StopTargetHandling = {0}", StopTargetHandling));
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;
            if (_armed || Position.MarketPosition != MarketPosition.Flat) return;

            // Three legs, one contract each, one bracket each. Targets are
            // deliberately close together so all three fill inside one Replay
            // session and the Orders tab shows what happens between fills.
            // Stops are at DISTINCT distances (40/41/42 ticks): at one identical
            // price, three 1-lot stops render on the chart as a single marker
            // reading quantity 3, indistinguishable from one 3-lot stop — three
            // prices force three rows in the Orders tab instead.
            if (!PooledStopTest)
            {
                SetStopLoss("PR1", CalculationMode.Ticks, 40, false);
                SetStopLoss("PR2", CalculationMode.Ticks, 41, false);
                SetStopLoss("PR3", CalculationMode.Ticks, 42, false);
                SetProfitTarget("PR1", CalculationMode.Ticks, 20);
                SetProfitTarget("PR2", CalculationMode.Ticks, 40);
                SetProfitTarget("PR3", CalculationMode.Ticks, 80);
            }
            // else: PooledStopTest mode submits its exits from OnExecutionUpdate as
            // each leg fills — no Set* calls at all in that mode (see below).

            // Market, not limit: the probe measures what happens AFTER a fill, so
            // how the fill arrives doesn't matter, and a limit that never fills
            // (round 2) measures nothing — MeanTick's real entries stay limit orders.
            EnterLong(0, 1, "PR1");
            EnterLong(0, 1, "PR2");
            EnterLong(0, 1, "PR3");
            _armed = true;
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            // Timestamps each fill and prints the aggregate strategy position — not
            // each leg's remaining quantity, which is what "collapsed or not" actually
            // asks. The Orders tab, not this log, is the evidence for per-leg quantity.
            Print(string.Format("{0} EXEC {1} qty={2} px={3} | position={4} {5}",
                time, execution.Order.Name, quantity, price,
                Position.MarketPosition, Position.Quantity));

            if (!PooledStopTest) return;

            // Pooled-stop mode: no Set* calls anywhere. Each leg's own fill submits
            // its own per-leg target immediately (fromEntrySignal ties it to that
            // leg alone); once the third leg fills, ONE stop for the whole position
            // is submitted with an EMPTY fromEntrySignal, deliberately unscoped to
            // any single entry. The open question this answers: does a ""-scoped
            // pooled stop get OCO-killed when a per-leg target fills?
            string name = execution.Order.Name;
            if (name != "PR1" && name != "PR2" && name != "PR3") return;

            double targetTicks;
            string targetSignal;
            switch (name)
            {
                case "PR1": targetTicks = 20; targetSignal = "P_T1"; break;
                case "PR2": targetTicks = 40; targetSignal = "P_T2"; break;
                default:    targetTicks = 80; targetSignal = "P_T3"; break;
            }
            double targetPx = price + targetTicks * TickSize;
            ExitLongLimit(0, true, 1, targetPx, targetSignal, name);

            if (Position.Quantity == 3)
            {
                double stopPx = Position.AveragePrice - PooledStopTicks * TickSize;
                ExitLongStopMarket(0, true, 3, stopPx, "P_S", "");
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time,
            ErrorCode error, string comment)
        {
            // Order.Oco is the one field that settles whether the three brackets
            // share a single OCO group or carry three of their own — not visible
            // from OnExecutionUpdate, and the question the same-price-stop
            // rendering confound made impossible to read off the chart alone.
            Print(string.Format("{0} ORDER {1} fromEntry={2} oco={3} qty={4} state={5}",
                time, order.Name, order.FromEntrySignal, order.Oco, order.Quantity, order.OrderState));
        }
    }
}
