// probe/LadderProbe.cs — THROWAWAY. Answers one question: do three independent
// entry signals each keep their own bracket when the first one closes?
// Not a model, not a candidate, not deployed anywhere permanent.
#region Using declarations
using System;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class LadderProbe : Strategy
    {
        private bool _armed;

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
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;
            if (_armed || Position.MarketPosition != MarketPosition.Flat) return;

            // Three legs at the SAME price, one contract each, one bracket each.
            // Targets are deliberately close together so all three fill inside one
            // Replay session and the Orders tab shows what happens between fills.
            double entry = Close[0];

            SetStopLoss("PR1", CalculationMode.Ticks, 40, false);
            SetStopLoss("PR2", CalculationMode.Ticks, 40, false);
            SetStopLoss("PR3", CalculationMode.Ticks, 40, false);
            SetProfitTarget("PR1", CalculationMode.Ticks, 20);
            SetProfitTarget("PR2", CalculationMode.Ticks, 40);
            SetProfitTarget("PR3", CalculationMode.Ticks, 80);

            EnterLongLimit(0, true, 1, entry, "PR1");
            EnterLongLimit(0, true, 1, entry, "PR2");
            EnterLongLimit(0, true, 1, entry, "PR3");
            _armed = true;
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            // The whole point of the probe: print the surviving working orders every
            // time anything fills, so the collapse (if any) is visible in the log
            // and not only in the Orders tab.
            Print(string.Format("{0} EXEC {1} qty={2} px={3} | position={4} {5}",
                time, execution.Order.Name, quantity, price,
                Position.MarketPosition, Position.Quantity));
        }
    }
}
