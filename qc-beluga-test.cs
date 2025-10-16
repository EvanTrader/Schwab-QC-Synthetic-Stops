#region Imports
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Drawing;
using QuantConnect;
using QuantConnect.Algorithm.Framework;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Algorithm.Framework.Execution;
using QuantConnect.Algorithm.Framework.Risk;
using QuantConnect.Algorithm.Selection;
using QuantConnect.Parameters;
using QuantConnect.Benchmarks;
using QuantConnect.Brokerages; 
using QuantConnect.Util;
using QuantConnect.Interfaces;
using QuantConnect.Algorithm;
using QuantConnect.Indicators;
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Custom;
using QuantConnect.DataSource;
using QuantConnect.Data.Fundamental;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Notifications;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.Fills;
using QuantConnect.Orders.Slippage;
using QuantConnect.Scheduling;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Future;
using QuantConnect.Securities.Option;
using QuantConnect.Securities.Forex;
using QuantConnect.Securities.Crypto;   
using QuantConnect.Securities.Interfaces;
using QuantConnect.Storage;
using QCAlgorithmFramework = QuantConnect.Algorithm.QCAlgorithm;
using QCAlgorithmFrameworkBridge = QuantConnect.Algorithm.QCAlgorithm;
#endregion

namespace QuantConnect.Algorithm.CSharp
{
    public class OpeningRangeBreakoutUniverseAlgorithm : QCAlgorithm
    {
        // parameters
        [Parameter("MaxPositions")]
        public int MaxPositions = 20;
        [Parameter("universeSize")]
        private int _universeSize = 2000;
        [Parameter("excludeETFs")]
        private int _excludeETFs = 0;
        [Parameter("atrThreshold")]
        private decimal _atrThreshold = 0.5m; 
        [Parameter("indicatorPeriod")]
        private int _indicatorPeriod = 14; // days
        [Parameter("openingRangeMinutes")]
        private int _openingRangeMinutes = 1;       // when to place entries
        [Parameter("stopLossAtrDistance")]
        public decimal stopLossAtrDistance = 0.1m;  // distance for stop loss, fraction of ATR
        [Parameter("stopLossRiskSize")]
        public decimal stopLossRiskSize = 0.01m; // 0.01 => Lose maximum of 1% of the portfolio if stop loss is hit
        [Parameter("reversing")]
        public int reversing = 0;           // on stop loss also open reverse position and place stop loss at the original entry price
        [Parameter("maximisePositions")]
        private int _maximisePositions = 0; // sends twice as much entry orders, cancel remaining orders when all positions are filled
        [Parameter("secondsResolution")]
        private int _secondsResolution = 0; // switch to seconds resolution for more precision [SLOW!]
        // todo: implement doubling
        [Parameter("doubling")]
        private int _doubling = 0;
        [Parameter("fees")]
        private int _fees = 0;
        
        // Add test mode parameter
        [Parameter("testLiveLogic")]
        public int _testLiveLogic = 0;  // Set to 1 for testing in backtest

        private int _leverage = 4;
        // Removed the _lastMonth variable and monthly check.
        private Universe _universe;
        private bool _entryPlaced = false;
        private int _maxLongPositions = 0;
        private int _maxShortPositions = 0;
        private int _maxPositions = 0;
        private decimal _maxMarginUsed = 0.0m;

        private Dictionary<Symbol, SymbolData> _symbolDataBySymbol = new();
        
        private HashSet<Symbol> _activeSymbols = new();  // Add this line here
        
        // Add these fields for tracking
        public Dictionary<Symbol, SyntheticEntry> _syntheticEntries = new();
        public Dictionary<Symbol, SyntheticStop> _syntheticStops = new();

        public override void Initialize()
        {
            // Set dates and cash (as in your working version)
            SetStartDate(2025, 01, 01);
            SetEndDate(2025, 08,09);
            SetCash(30_000);

            Settings.AutomaticIndicatorWarmUp = true;

            // ADD THIS LINE RIGHT HERE! 
            DefaultOrderProperties.TimeInForce = TimeInForce.Day;

            // ***** Change: Replace Alpaca with IB (Margin) and apply the custom security initializer *****
            if (_fees == 0)
            {
                SetBrokerageModel(BrokerageName.CharlesSchwab, AccountType.Margin);
                SetSecurityInitializer(new CustomSecurityInitializer(BrokerageModel, new FuncSecuritySeeder(GetLastKnownPrices)));
            }

            // Add SPY so there is at least 1 asset at minute resolution to step the algorithm along.
            var spy = AddEquity("SPY").Symbol;

            // Add a universe of the most liquid US Equities.
            UniverseSettings.Leverage = _leverage;
            if (_secondsResolution == 1)
                UniverseSettings.Resolution = Resolution.Second;
            UniverseSettings.Asynchronous = false;
            UniverseSettings.Schedule.On(DateRules.MonthStart(spy));
            // More efficient universe settings
            //UniverseSettings.MinimumTimeInUniverse = TimeSpan.FromDays(20);  // Reduce churn
            //UniverseSettings.Resolution = Resolution.Minute;  // Be explicit
                    
            // *** Modified Universe Selection: update every time the lambda is called (no monthly check) ***
            _universe = AddUniverse(fundamentals =>
            {
                var selectedSymbols = fundamentals
                    .Where(f => f.Price > 5 && (_excludeETFs == 0 || f.HasFundamentalData) && f.Symbol != spy)
                    .Where(f => !f.Symbol.Value.Contains(".")) // Filter out symbols with dots
                    .OrderByDescending(f => f.DollarVolume)
                    .Take(_universeSize)
                    .Select(f => f.Symbol);
                Log($"Universe selected: {selectedSymbols.Count()} symbols");
                return selectedSymbols;
            });

            Schedule.On(DateRules.EveryDay(spy), TimeRules.AfterMarketOpen(spy, 0), () => ResetVars());
            Schedule.On(DateRules.EveryDay(spy), TimeRules.BeforeMarketClose(spy, 1), () => LogAndLiquidate());
            Schedule.On(DateRules.EveryDay(spy), TimeRules.BeforeMarketClose(spy, 1), () => UpdatePlots());
            SetWarmUp(TimeSpan.FromDays(2 * _indicatorPeriod));

            // Safety liquidation at multiple checkpoints
            //Schedule.On(DateRules.EveryDay(spy), TimeRules.BeforeMarketClose(spy, 0.5), () => SafetyLiquidate());
            //Schedule.On(DateRules.EveryDay(spy), TimeRules.At(15, 59, 45), () => SafetyLiquidate());
            //Schedule.On(DateRules.EveryDay(spy), TimeRules.At(15, 59, 50), () => SafetyLiquidate());

            if (LiveMode)
            {
                Schedule.On(DateRules.EveryDay(spy), TimeRules.At(13, 0), () => {
                    foreach (var symbolData in _symbolDataBySymbol.Values)
                    {
                        if (!Portfolio.ContainsKey(symbolData.Symbol))
                        {
                            symbolData.Dispose();
                        }
                    }
                    GC.Collect();
                });
            }
            

            Log(
                $"MaxPositions={MaxPositions}, universeSize={_universeSize}, excludeETFs={_excludeETFs}, atrThreshold={_atrThreshold}, " +
                $"indicatorPeriod={_indicatorPeriod}, openingRangeMinutes={_openingRangeMinutes}, stopLossAtrDistance={stopLossAtrDistance}, " +
                $"stopLossRiskSize={stopLossRiskSize}, reversing={reversing}, maximisePositions={_maximisePositions}, " +
                $"secondsResolution={_secondsResolution}, doubling={_doubling}, fees={_fees}"
            );
        }
        
        private void ResetVars()
        {
            _entryPlaced = false;
            _maxLongPositions = 0;
            _maxShortPositions = 0;
            _maxPositions = 0;
            _maxMarginUsed = 0.0m;
            
            // Clear tracking dictionaries
            _syntheticEntries.Clear();
            _syntheticStops.Clear();  // Add this
        }

        private void UpdatePlots()
        {
            Plot("Positions", "Long", _maxLongPositions);
            Plot("Positions", "Short", _maxShortPositions);
            Plot("Positions", "Total", _maxPositions);
            Plot("Margin", "Used", (double)_maxMarginUsed);
        }

        private void LogAndLiquidate()
        {
            var totalPnL = Portfolio.TotalProfit;
            var positionCount = 0;
            
            foreach (var kvp in Portfolio.Where(x => x.Value.Invested))
            {
                var pnl = kvp.Value.UnrealizedProfit;
                positionCount++;
                
                Log($"LIQUIDATE,{kvp.Key},Qty={kvp.Value.Quantity}...");
                
                // ADD THIS BLOCK:
                if (_symbolDataBySymbol.TryGetValue(kvp.Key, out var symbolData))
                {
                    symbolData.CancelAllStops();  // Clean state!
                }
            }
            
            Log($"DAILY SUMMARY: Positions={positionCount}, TotalP&L=${totalPnL:F2}");
            
            Liquidate();
        }

        // private void SafetyLiquidate()
        // {
        //     // Cancel all open orders
        //     var openOrders = Transactions.GetOpenOrders();
        //     if (openOrders.Count > 0)
        //     {
        //         foreach (var order in openOrders)
        //         {
        //             Transactions.CancelOrder(order.Id);
        //         }
        //     }
            
        //     // ADD THIS BLOCK - Clean ALL SymbolData:
        //     foreach (var symbolData in _symbolDataBySymbol.Values)
        //     {
        //         symbolData.CancelAllStops();
        //     }
            
        //     // Liquidate all positions
        //     if (Portfolio.Values.Any(x => x.Invested))
        //     {
        //         Liquidate();
        //     }
        // }
        
        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            // Clean up removed securities
            foreach (var security in changes.RemovedSecurities)
            {
                if (_symbolDataBySymbol.TryGetValue(security.Symbol, out var symbolData))
                {
                    symbolData.Dispose();  // Using our new IDisposable implementation
                    _symbolDataBySymbol.Remove(security.Symbol);
                }
                _activeSymbols.Remove(security.Symbol);

                if (security.Invested)
                {
                    Liquidate(security.Symbol);
                }
            }

            // For each new security, reapply the fee model so that fees are forced to 0.
            foreach (var security in changes.AddedSecurities)
            {
                // security.SetFeeModel(new ConstantFeeModel(0.27m, "USD")); *not used right now*
                
                // ✅ THE FIX: ONLY CREATE IF IT DOESN'T EXIST! ✅
                if (!_symbolDataBySymbol.ContainsKey(security.Symbol))
                {
                    _symbolDataBySymbol[security.Symbol] = new SymbolData(this, security, _openingRangeMinutes, _indicatorPeriod);
                }
                
                _activeSymbols.Add(security.Symbol);
            }
        }


        public override void OnData(Slice slice)
        {
            if (!slice.HasData || IsWarmingUp) return;  // Early exit if no data
            
            // Process synthetic entries on second data (runs continuously after 9:31)
            if (slice.Time.Second != 0 && _syntheticEntries.Count > 0)
            {
                ProcessSyntheticEntries(slice);
            }
            
            // In OnData, after ProcessSyntheticEntries:
            if (slice.Time.Second != 0 && _syntheticStops.Count > 0)
            {
                ProcessSyntheticStops(slice);
            }



            // Check entry stops every minute
            //if (slice.Time.Second == 0 && _entryThresholds.Count > 0)
            //{
               // TightenEntryStops();
            //}
                        
            if (_entryPlaced) return;  // Early exit after processing
            
            if (Time.Hour != 9 || Time.Minute != (30 + _openingRangeMinutes)) return;  // Early exit if not entry time

            var take = _maximisePositions == 1 ? 2 : 1;

            // More efficient filtering using HashSet
            var filtered = ActiveSecurities.Values
                .Where(s => s.Price > 0 && _symbolDataBySymbol.ContainsKey(s.Symbol))
                .Select(s => _symbolDataBySymbol[s.Symbol])
                .Where(s => s.RelativeVolume > 1 && s.ATR > _atrThreshold)
                .OrderByDescending(s => s.RelativeVolume)
                .Take(MaxPositions * take)
                .ToList();  // Materialize the query once

            Log($"SCAN LIST ({filtered.Count}): {string.Join(",", filtered.Select(s => s.Symbol.Value))}");

            foreach (var symbolData in filtered)
            {
                symbolData.Scan();
            }

            _entryPlaced = true;

            // Update metrics once at end
            UpdateMetrics();
        }

        private void UpdateMetrics()
        {
            var (longPos, shortPos) = Portfolio.Values
                .Where(x => x.Invested)
                .Aggregate((0, 0), (acc, holding) => 
                    holding.Quantity > 0 ? 
                        (acc.Item1 + 1, acc.Item2) : 
                        (acc.Item1, acc.Item2 + 1));

            _maxLongPositions = Math.Max(_maxLongPositions, longPos);
            _maxShortPositions = Math.Max(_maxShortPositions, shortPos);
            _maxPositions = Math.Max(_maxPositions, longPos + shortPos);
            _maxMarginUsed = Math.Max(_maxMarginUsed, 
                Portfolio.TotalMarginUsed / Portfolio.TotalPortfolioValue);
        }


        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            // Handle rejected orders first
            if (orderEvent.Status == OrderStatus.Invalid)
            {
                Log($"ORDER REJECTED,{orderEvent.Symbol},OrderId={orderEvent.OrderId},Message={orderEvent.Message}");
                
                // Check if it's a stop order rejection due to price
                if (orderEvent.Message.Contains("stop price must be") && _symbolDataBySymbol.ContainsKey(orderEvent.Symbol))
                {
                    var symbolData = _symbolDataBySymbol[orderEvent.Symbol];
                    
                    // Determine if this was an entry or exit stop
                    if (symbolData.EntryTicket != null && symbolData.EntryTicket.OrderId == orderEvent.OrderId)
                    {
                        // Entry stop rejected - add to synthetic monitoring
                        Log($"ENTRY STOP REJECTED,{orderEvent.Symbol},Adding to synthetic monitoring");
                        
                        // Use appropriate resolution
                        var resolution = _testLiveLogic == 1 ? Resolution.Tick : Resolution.Second;
                        if (!Securities[orderEvent.Symbol].Subscriptions.Any(x => x.Resolution == resolution))
                        {
                            AddEquity(orderEvent.Symbol, resolution);
                        }
                        
                        _syntheticEntries[orderEvent.Symbol] = new SyntheticEntry
                        {
                            TargetPrice = symbolData.EntryPrice,
                            Quantity = symbolData.Quantity,
                            Timeout = Time.AddMinutes(10), // Full trading day
                            SymbolData = symbolData
                        };
                    }
                    else if (symbolData.StopLossTicket != null && symbolData.StopLossTicket.OrderId == orderEvent.OrderId)
                    {
                        // Exit stop rejected - add to synthetic monitoring
                        Log($"EXIT STOP REJECTED,{orderEvent.Symbol},Adding to synthetic stop monitoring");
                        
                        // Add appropriate resolution based on mode
                        var resolution = _testLiveLogic == 1 ? Resolution.Tick : Resolution.Second;
                        if (!Securities[orderEvent.Symbol].Subscriptions.Any(x => x.Resolution == resolution))
                        {
                            AddEquity(orderEvent.Symbol, resolution);
                        }
                        
                        _syntheticStops[orderEvent.Symbol] = new SyntheticStop
                        {
                            TargetPrice = symbolData.StopLossPrice,
                            Quantity = -symbolData.Quantity, // Negative to exit
                            Timeout = Time.AddMinutes(10), // Full trading day
                            SymbolData = symbolData,
                            IsLong = symbolData.Quantity > 0
                        };
                    }
                }
                return;
            }
            
            if (orderEvent.Status != OrderStatus.Filled && 
                orderEvent.Status != OrderStatus.PartiallyFilled) return;
            
            // Try direct lookup first
            if (_symbolDataBySymbol.ContainsKey(orderEvent.Symbol))
            {
                _symbolDataBySymbol[orderEvent.Symbol].OnOrderEvent(orderEvent.Ticket);
            }
            else
            {
                // Symbol mapping issue - search by order ID
                var symbolData = _symbolDataBySymbol.Values
                    .FirstOrDefault(sd => (sd.EntryTicket != null && sd.EntryTicket.OrderId == orderEvent.OrderId) || 
                                        (sd.StopLossTicket != null && sd.StopLossTicket.OrderId == orderEvent.OrderId));
                
                if (symbolData != null)
                {
                    Log($"SYMBOL MAPPING RESOLVED,{orderEvent.Symbol},Found via OrderId={orderEvent.OrderId}");
                    symbolData.OnOrderEvent(orderEvent.Ticket);
                }
                else
                {
                    Log($"WARNING: No SymbolData found for {orderEvent.Symbol} OrderId={orderEvent.OrderId}");
                }
            }
            
            // Only remove synthetic monitoring if THIS order was from synthetic
            if (_syntheticEntries.ContainsKey(orderEvent.Symbol))
            {
                // Check if this fill was actually the synthetic entry
                var entry = _syntheticEntries[orderEvent.Symbol];
                if (entry.SymbolData.EntryTicket != null && 
                    entry.SymbolData.EntryTicket.OrderId == orderEvent.OrderId)
                {
                    Log($"SYNTHETIC ENTRY COMPLETED,{orderEvent.Symbol},Removing from monitoring");
                    _syntheticEntries.Remove(orderEvent.Symbol);
                    // Don't RemoveSecurity yet - might have position!
                }
            }

            if (_syntheticStops.ContainsKey(orderEvent.Symbol))
            {
                // Check if this fill was actually the synthetic stop
                var stop = _syntheticStops[orderEvent.Symbol];
                if (stop.SymbolData.StopLossTicket != null && 
                    stop.SymbolData.StopLossTicket.OrderId == orderEvent.OrderId)
                {
                    Log($"SYNTHETIC STOP COMPLETED,{orderEvent.Symbol},Removing from monitoring");
                    _syntheticStops.Remove(orderEvent.Symbol);
                    
                    // Only remove security if position is flat
                    if (!Portfolio[orderEvent.Symbol].Invested)
                    {
                        RemoveSecurity(orderEvent.Symbol);
                    }
                }
            }
        }

        public void CheckToCancelRemainingEntries()
        {
            if (_maximisePositions == 0) return;

            int openPositionsCount = 0;
            foreach (var kvp in Portfolio)
            {
                if (kvp.Value.Invested)
                    openPositionsCount += 1;
            }
            if (openPositionsCount >= MaxPositions)
            {
                foreach (var symbolData in _symbolDataBySymbol.Values)
                {
                    if (symbolData.EntryTicket != null && symbolData.EntryTicket.Status == OrderStatus.Submitted)
                    {
                        symbolData.EntryTicket.Cancel();
                        
                        // REMOVE FROM SYNTHETIC MONITORING TOO!
                        if (_syntheticEntries.ContainsKey(symbolData.Symbol))
                        {
                            _syntheticEntries.Remove(symbolData.Symbol);
                            RemoveSecurity(symbolData.Symbol);
                        }
                        
                        symbolData.EntryTicket = null;  // Now safe to null
                    }
                }
            }
        }
        private void ProcessSyntheticEntries(Slice slice)
        {
            foreach (var kvp in _syntheticEntries.ToList())
            {
                var symbol = kvp.Key;
                var entry = kvp.Value;
                
                if (!Securities.ContainsKey(symbol)) continue;

                var currentSymbol = Securities[symbol].Symbol;
                if (!_symbolDataBySymbol.ContainsKey(currentSymbol))
                {
                    _symbolDataBySymbol[currentSymbol] = entry.SymbolData;
                }
                
                var symbolData = entry.SymbolData;
                var security = Securities[symbol];                
                var currentPrice = security.Price;
                var bidPrice = security.BidPrice;
                var askPrice = security.AskPrice;
                
                // Check for dead stock timeout (no valid prices after 10 minutes )
                if (Time >= entry.Timeout || (bidPrice == 0 || askPrice == 0))
                {
                    Log($"SYNTHETIC TIMEOUT - DEAD STOCK,{symbol},No valid prices for full day");
                    _syntheticEntries.Remove(symbol);
                    RemoveSecurity(symbol);
                    continue;
                }
                
                // For longs
                if (entry.Quantity > 0)
                {
                    // Check if we can place stop now
                    if (askPrice > 0 && askPrice <= entry.TargetPrice + 0.01m)
                    {
                        // ADD THIS CHECK BEFORE PLACING ORDER:
                        if (symbolData.EntryTicket == null || 
                            symbolData.EntryTicket.Status == OrderStatus.Invalid ||
                            symbolData.EntryTicket.Status == OrderStatus.Canceled)
                        {
                            var stopPrice = askPrice <= entry.TargetPrice ? entry.TargetPrice : entry.TargetPrice + 0.01m;
                            symbolData.EntryTicket = StopMarketOrder(symbol, entry.Quantity, stopPrice, tag: "Entry");
                            Log($"SYNTHETIC STOP PLACED,{symbol},Ask={askPrice:F2},Placed={stopPrice:F2}");
                            _syntheticEntries.Remove(symbol);
                        }
                        // DON'T REMOVE FROM MONITORING - stays until fill
                    }

                    // Check for cross above
                    else if (currentPrice > entry.TargetPrice)
                    {
                        // MISSING CHECK!
                        if (symbolData.EntryTicket == null || 
                            symbolData.EntryTicket.Status == OrderStatus.Invalid ||
                            symbolData.EntryTicket.Status == OrderStatus.Canceled)
                        {
                            Log($"SYNTHETIC CROSS,{symbol},Price={currentPrice:F2}>Target={entry.TargetPrice:F2},Market order");
                            symbolData.EntryTicket = MarketOrder(symbol, entry.Quantity, asynchronous: true, tag: "Entry (Cross)");
                        }
                    }
                }    
                // For shorts
                else
                {
                    // Check if we can place stop now
                    if (bidPrice > 0 && bidPrice >= entry.TargetPrice - 0.01m)
                    {
                        // ADD THIS CHECK:
                        if (symbolData.EntryTicket == null || 
                            symbolData.EntryTicket.Status == OrderStatus.Invalid ||
                            symbolData.EntryTicket.Status == OrderStatus.Canceled)
                        {
                            var stopPrice = bidPrice >= entry.TargetPrice ? entry.TargetPrice : entry.TargetPrice - 0.01m;
                            symbolData.EntryTicket = StopMarketOrder(symbol, entry.Quantity, stopPrice, tag: "Entry");
                            Log($"SYNTHETIC STOP PLACED,{symbol},Bid={bidPrice:F2},Placed={stopPrice:F2}");
                            _syntheticEntries.Remove(symbol);
                        }
                    }   
                    // Check for cross below
                    else if (currentPrice < entry.TargetPrice)
                    {
                        // MISSING CHECK!
                        if (symbolData.EntryTicket == null || 
                            symbolData.EntryTicket.Status == OrderStatus.Invalid ||
                            symbolData.EntryTicket.Status == OrderStatus.Canceled)
                        {
                            Log($"SYNTHETIC CROSS,{symbol},Price={currentPrice:F2}<Target={entry.TargetPrice:F2},Market order");
                            symbolData.EntryTicket = MarketOrder(symbol, entry.Quantity, asynchronous: true, tag: "Entry (Cross)");
                        }
                    }
                }
            }
        }
        private void ProcessSyntheticStops(Slice slice)
        {
            foreach (var kvp in _syntheticStops.ToList())
            {
                var symbol = kvp.Key;
                var stop = kvp.Value;
                
                if (!Securities.ContainsKey(symbol)) continue;
                
                var currentSymbol = Securities[symbol].Symbol;
                if (!_symbolDataBySymbol.ContainsKey(currentSymbol))
                {
                    _symbolDataBySymbol[currentSymbol] = stop.SymbolData;
                }

                // Add position size validation
                var currentPosition = (int)Portfolio[symbol].Quantity;
                if (currentPosition == 0)
                {
                    Log($"SYNTHETIC STOP REMOVED,{symbol},Position already flat");
                    _syntheticStops.Remove(symbol);
                    RemoveSecurity(symbol);
                    continue;
                }

                // Cap synthetic quantity at actual position size
                if (Math.Abs(stop.Quantity) > Math.Abs(currentPosition))
                {
                    stop.Quantity = -currentPosition;
                    Log($"SYNTHETIC STOP ADJUSTED,{symbol},Resized to match position={currentPosition}");
                }

                var security = Securities[symbol];
                var currentPrice = security.Price;
                var bidPrice = security.BidPrice;
                var askPrice = security.AskPrice;
                
                // Check for timeout (full trading day)
                if (Time >= stop.Timeout)
                {
                    // ADD CHECK to prevent spam!
                    if (stop.SymbolData.StopLossTicket == null || 
                        stop.SymbolData.StopLossTicket.Status == OrderStatus.Invalid ||
                        stop.SymbolData.StopLossTicket.Status == OrderStatus.Canceled)
                    {
                        Log($"SYNTHETIC STOP TIMEOUT,{symbol},Forcing market order");
                        stop.SymbolData.StopLossTicket = MarketOrder(symbol, stop.Quantity, asynchronous: true, tag: "Stop (Timeout)");
                        _syntheticStops.Remove(symbol);
                        // Only remove security if position is flat
                        if (!Portfolio[symbol].Invested)
                        {
                            RemoveSecurity(symbol);
                        }
                    }
                    continue;
                }
                
                // For long positions (need to sell)
                if (stop.IsLong)
                {
                    // Check if bid is now above our stop target
                    if (bidPrice > 0 && bidPrice >= stop.TargetPrice - 0.02m)
                    {
                        // ADD CHECK:
                        if (stop.SymbolData.StopLossTicket == null || 
                            stop.SymbolData.StopLossTicket.Status == OrderStatus.Invalid ||
                            stop.SymbolData.StopLossTicket.Status == OrderStatus.Canceled)
                        {
                            stop.SymbolData.StopLossTicket = StopMarketOrder(symbol, stop.Quantity, stop.TargetPrice - 0.02m, tag: "Stop");
                            Log($"SYNTHETIC STOP PLACED,{symbol},Bid={bidPrice:F2},Target={stop.TargetPrice:F2}");
                        }
                    }    // DON'T REMOVE FROM MONITORING - Wait for OnOrderEvent confirmation
                    
                    // Check for cross below
                    else if (currentPrice < stop.TargetPrice)
                    {
                        // ADD CHECK!
                        if (stop.SymbolData.StopLossTicket == null || 
                            stop.SymbolData.StopLossTicket.Status == OrderStatus.Invalid ||
                            stop.SymbolData.StopLossTicket.Status == OrderStatus.Canceled)
                        {
                            Log($"SYNTHETIC STOP CROSS,{symbol},Price={currentPrice:F2}<Target={stop.TargetPrice:F2},Market order");
                            stop.SymbolData.StopLossTicket = MarketOrder(symbol, stop.Quantity, asynchronous: true, tag: "Stop (Cross)");
                            // _syntheticStops.Remove(symbol);
                            // RemoveSecurity(symbol);
                        }
                    }
                }
                // For short positions (need to buy)
                else
                {
                    // Check if ask is now below our stop target
                    if (askPrice > 0 && askPrice <= stop.TargetPrice + 0.02m)
                    {
                        // ADD THIS CHECK:
                        if (stop.SymbolData.StopLossTicket == null || 
                            stop.SymbolData.StopLossTicket.Status == OrderStatus.Invalid ||
                            stop.SymbolData.StopLossTicket.Status == OrderStatus.Canceled)
                        {
                            stop.SymbolData.StopLossTicket = StopMarketOrder(symbol, stop.Quantity, stop.TargetPrice + 0.02m, tag: "Stop");
                            Log($"SYNTHETIC STOP PLACED,{symbol},Ask={askPrice:F2},Target={stop.TargetPrice:F2}");
                        }
                    }
                    // Check for cross above
                    else if (currentPrice > stop.TargetPrice)
                    {
                        // MISSING CHECK!
                        if (stop.SymbolData.StopLossTicket == null || 
                            stop.SymbolData.StopLossTicket.Status == OrderStatus.Invalid ||
                            stop.SymbolData.StopLossTicket.Status == OrderStatus.Canceled)
                        {
                            Log($"SYNTHETIC STOP CROSS,{symbol},Price={currentPrice:F2}>Target={stop.TargetPrice:F2},Market order");
                            stop.SymbolData.StopLossTicket = MarketOrder(symbol, stop.Quantity, asynchronous: true, tag: "Stop (Cross)");
                        }
                    }
                }
            }
        }
    }

    public class SymbolData : IDisposable
    {

        public Symbol Symbol => _security.Symbol;  // Add this        
        public decimal? RelativeVolume;
        public TradeBar OpeningBar = new();
        private OpeningRangeBreakoutUniverseAlgorithm _algorithm;
        private Security _security;
        public IDataConsolidator Consolidator;
        public AverageTrueRange ATR;
        private SimpleMovingAverage VolumeSMA;
        public decimal EntryPrice { get; private set; }
        public decimal StopLossPrice { get; set; }
        public int Quantity { get; private set; }
        public OrderTicket EntryTicket, StopLossTicket;
        public bool Reversed = false;
        private int _lastStopQuantity = 0;  // Track what stop currently covers
        private List<OrderTicket> _backupStops = new List<OrderTicket>();  // Backup stops if update fails
        private bool _disposed;

        public SymbolData(OpeningRangeBreakoutUniverseAlgorithm algorithm, Security security, int openingRangeMinutes, int indicatorPeriod)
        {
            _algorithm = algorithm;
            _security = security;
            Consolidator = algorithm.Consolidate(security.Symbol, TimeSpan.FromMinutes(openingRangeMinutes), ConsolidationHandler);
            // Using Daily resolution as in the Python algo:
            ATR = algorithm.ATR(security.Symbol, indicatorPeriod, resolution: Resolution.Daily);
            VolumeSMA = new SimpleMovingAverage(indicatorPeriod);
        }

        void ConsolidationHandler(TradeBar bar)
        {
            if (OpeningBar.Time.Date == bar.Time.Date) return;
            // Update the asset's indicators and save the day's opening bar.
            RelativeVolume = VolumeSMA.IsReady && VolumeSMA > 0 ? bar.Volume / VolumeSMA : null;
            VolumeSMA.Update(bar.EndTime, bar.Volume);
            OpeningBar = bar;
        }

        public void Scan()
        {
            var range = OpeningBar.High - OpeningBar.Low;
            var barType = OpeningBar.Close > OpeningBar.Open ? "GREEN" : 
                        OpeningBar.Close < OpeningBar.Open ? "RED" : "DOJI";
            _algorithm.Log($"ORB,{_security.Symbol.Value},O={OpeningBar.Open:F2},H={OpeningBar.High:F2},L={OpeningBar.Low:F2},C={OpeningBar.Close:F2},V={OpeningBar.Volume},Range={range:F3},{barType}");            
            
            if (OpeningBar.Close > OpeningBar.Open)
            {
                PlaceTrade(OpeningBar.High + 0.01m, OpeningBar.High + 0.01m - _algorithm.stopLossAtrDistance * ATR);
            }
            else if (OpeningBar.Close < OpeningBar.Open)
            {
                PlaceTrade(OpeningBar.Low - 0.01m, OpeningBar.Low - 0.01m + _algorithm.stopLossAtrDistance * ATR);
            }
            Reversed = false;
        }

        public void PlaceTrade(decimal entryPrice, decimal stopPrice)
        {
            var quantity = (int)((_algorithm.stopLossRiskSize * _algorithm.Portfolio.TotalPortfolioValue / _algorithm.MaxPositions) / (entryPrice - stopPrice));
            var quantityLimit = _algorithm.CalculateOrderQuantity(_security.Symbol, 2.6m / _algorithm.MaxPositions);
            quantity = (int)(Math.Min(Math.Abs(quantity), quantityLimit) * Math.Sign(quantity));
            
            if (quantity != 0)
            {
                EntryPrice = entryPrice;
                StopLossPrice = stopPrice;
                Quantity = quantity;
                
                // Test mode - just place stop orders (no Schwab restrictions)
                if (_algorithm._testLiveLogic == 1 && !_algorithm.LiveMode)
                {
                    EntryTicket = _algorithm.StopMarketOrder(_security.Symbol, quantity, entryPrice, tag: "Entry");
                    _algorithm.Log($"TEST MODE STOP PLACED,{_security.Symbol.Value},Target={entryPrice:F2}");
                }
                // Live mode - check bid/ask
                else if (_algorithm.LiveMode)
                {
                    var bidPrice = _security.BidPrice;
                    var askPrice = _security.AskPrice;
                    
                    if (bidPrice > 0 && askPrice > 0)
                    {
                        bool canPlaceStop = false;
                        decimal stopOrderPrice = entryPrice;
                        
                        if (quantity > 0) // Long entry
                        {
                            if (askPrice <= entryPrice)
                            {
                                stopOrderPrice = entryPrice;
                                canPlaceStop = true;
                            }
                            else if (askPrice == entryPrice + 0.01m)
                            {
                                stopOrderPrice = entryPrice + 0.01m;
                                canPlaceStop = true;
                            }
                        }
                        else // Short entry
                        {
                            if (bidPrice >= entryPrice)
                            {
                                stopOrderPrice = entryPrice;
                                canPlaceStop = true;
                            }
                            else if (bidPrice == entryPrice - 0.01m)
                            {
                                stopOrderPrice = entryPrice - 0.01m;
                                canPlaceStop = true;
                            }
                        }
                        
                        if (canPlaceStop)
                        {
                            EntryTicket = _algorithm.StopMarketOrder(_security.Symbol, quantity, stopOrderPrice, tag: "Entry");
                            _algorithm.Log($"STOP PLACED,{_security.Symbol.Value},Target={entryPrice:F2},Placed={stopOrderPrice:F2}");
                        }
                        else
                        {
                            // Add to synthetic monitoring
                            if (!_algorithm.Securities[_security.Symbol].Subscriptions.Any(x => x.Resolution == Resolution.Second))
                            {
                                _algorithm.AddEquity(_security.Symbol, Resolution.Second);
                            }
                            _algorithm._syntheticEntries[_security.Symbol] = new SyntheticEntry
                            {
                                TargetPrice = entryPrice,
                                Quantity = quantity,
                                Timeout = _algorithm.Time.AddMinutes(10), // Full trading day
                                SymbolData = this
                            };
                            _algorithm.Log($"SYNTHETIC ENTRY MONITOR,{_security.Symbol.Value},Target={entryPrice:F2},Ask={askPrice:F2}");
                        }
                    }
                    else
                    {
                        // No prices - place stop order at target
                        _algorithm.Log($"NO PRICES,{_security.Symbol.Value},Placing stop at target");
                        EntryTicket = _algorithm.StopMarketOrder(_security.Symbol, quantity, entryPrice, tag: "Entry");
                    }
                }
                else // Normal backtest mode
                {
                    EntryTicket = _algorithm.StopMarketOrder(_security.Symbol, quantity, entryPrice, tag: "Entry");
                }
            }
        }        

        public void OnOrderEvent(OrderTicket orderTicket)
        {
            var order = orderTicket.OrderEvents.Last();

            if (EntryTicket != null && orderTicket.OrderId == EntryTicket.OrderId) 
            {
                var side = Quantity > 0 ? "LONG" : "SHORT";
                var slippage = Math.Abs(order.FillPrice - EntryPrice);
                
                // Calculate adjusted stop loss based on price improvement
                decimal adjustedStopLossPrice = StopLossPrice;
                bool gotPriceImprovement = false;
                
                if (Quantity > 0) // Long position
                {
                    // For longs, price improvement means fill price < target entry price
                    if (order.FillPrice < EntryPrice)
                    {
                        gotPriceImprovement = true;
                        adjustedStopLossPrice = order.FillPrice - _algorithm.stopLossAtrDistance * ATR;
                        _algorithm.Log($"PRICE IMPROVEMENT,{_security.Symbol.Value},LONG,Target={EntryPrice:F2},Fill={order.FillPrice:F2},OrigStop={StopLossPrice:F2},NewStop={adjustedStopLossPrice:F2}");
                    }
                }
                else // Short position
                {
                    // For shorts, price improvement means fill price > target entry price
                    if (order.FillPrice > EntryPrice)
                    {
                        gotPriceImprovement = true;
                        adjustedStopLossPrice = order.FillPrice + _algorithm.stopLossAtrDistance * ATR;
                        _algorithm.Log($"PRICE IMPROVEMENT,{_security.Symbol.Value},SHORT,Target={EntryPrice:F2},Fill={order.FillPrice:F2},OrigStop={StopLossPrice:F2},NewStop={adjustedStopLossPrice:F2}");
                    }
                }
                
                // Update the stop loss price if we got price improvement
                if (gotPriceImprovement)
                {
                    StopLossPrice = adjustedStopLossPrice;
                }
                
                _algorithm.Log($"ENTRY,{_security.Symbol.Value},{side},Fill={order.FillPrice:F2},Target={EntryPrice:F2},Slippage={slippage:F3},Qty={order.FillQuantity},StopLoss={StopLossPrice:F2},StopDist={Math.Abs(order.FillPrice - StopLossPrice):F3},RVol={RelativeVolume:F2},ATR={ATR.Current.Value:F2}");
                
                // TEST MODE LOGIC
                if (_algorithm._testLiveLogic == 1 && !_algorithm.LiveMode)
                {
                    var bidPrice = _security.BidPrice;
                    var askPrice = _security.AskPrice;
                    
                    // Mock rejection if spread too wide
                    var mockReject = false;
                    if (Quantity > 0 && bidPrice < StopLossPrice)  // REMOVED - 0.02m
                    {
                        mockReject = true;
                        _algorithm.Log($"TEST MODE MOCK REJECT STOP,{_security.Symbol.Value},Bid={bidPrice:F2}<Target={StopLossPrice:F2}");
                    }
                    else if (Quantity < 0 && askPrice > StopLossPrice)  // REMOVED + 0.02m
                    {
                        mockReject = true;
                        _algorithm.Log($"TEST MODE MOCK REJECT STOP,{_security.Symbol.Value},Ask={askPrice:F2}>Target={StopLossPrice:F2}");
                    }
                    
                    if (mockReject)
                    {
                        // Add to synthetic monitoring with tick resolution
                        if (!_algorithm.Securities[_security.Symbol].Subscriptions.Any(x => x.Resolution == Resolution.Tick))
                        {
                            _algorithm.AddEquity(_security.Symbol, Resolution.Tick);
                        }
                        _algorithm._syntheticStops[_security.Symbol] = new SyntheticStop
                        {
                            TargetPrice = StopLossPrice,
                            Quantity = -Quantity,
                            Timeout = _algorithm.Time.AddMinutes(10),  // Already fixed
                            SymbolData = this,
                            IsLong = Quantity > 0
                        };
                    }
                    else
                    {
                        if (StopLossTicket == null || StopLossTicket.Status == OrderStatus.Invalid)
                        {
                            Quantity = (int)order.FillQuantity;  // Update to actual fill
                            StopLossTicket = _algorithm.StopMarketOrder(_security.Symbol, -order.FillQuantity, StopLossPrice, tag: "ATR Stop");
                        }
                    }
                }
                // LIVE MODE LOGIC
                else if (_algorithm.LiveMode)
                {
                    EnsureFullPositionProtected(order);  // NEW METHOD CALL
                }
                else
                {
                    // NO CHECK - like the old code!
                    StopLossTicket = _algorithm.StopMarketOrder(_security.Symbol, -Quantity, StopLossPrice, tag: "ATR Stop");
                }
                
                _algorithm.CheckToCancelRemainingEntries();
            }

            // LOG STOP LOSS FOR EVERYONE
            if (StopLossTicket != null && orderTicket.OrderId == StopLossTicket.OrderId)
            {
                var entryFillPrice = EntryTicket.AverageFillPrice;
                var exitPrice = order.FillPrice;

                // ADD THESE LINES:
                _lastStopQuantity = 0;  // Reset tracking since position is closing              

                var pnl = Quantity > 0 ? 
                    (exitPrice - entryFillPrice) * Quantity :  
                    (entryFillPrice - exitPrice) * Math.Abs(Quantity);
                _algorithm.Log($"STOPLOSS,{_security.Symbol.Value},Entry={entryFillPrice:F2},Exit={exitPrice:F2},P&L=${pnl:F2}");
                
                // THEN CHECK IF WE NEED TO REVERSE
                if (_algorithm.reversing == 1 && !Reversed)
                {
                    _algorithm.MarketOrder(_security.Symbol, -Quantity, asynchronous: false, tag: "Reversed");
                    
                    // Only validate reversed stop in live mode
                    if (_algorithm.LiveMode)
                    {
                        var bidPrice = _security.BidPrice;
                        var askPrice = _security.AskPrice;
                        
                        // Check if we have valid prices for reversed stop
                        if (bidPrice > 0 && askPrice > 0)
                        {
                            if (Quantity > 0 && EntryPrice <= askPrice)  // Reversed short stop already invalid
                            {
                                _algorithm.Log($"REVERSED STOP ADJUSTMENT,{_security.Symbol.Value},StopTarget={EntryPrice:F2},Ask={askPrice:F2},Using MARKET order");
                                StopLossTicket = _algorithm.MarketOrder(_security.Symbol, Quantity, asynchronous: false, tag: "Reversed ATR Stop (Market)");
                            }
                            else if (Quantity < 0 && EntryPrice >= bidPrice)  // Reversed long stop already invalid
                            {
                                _algorithm.Log($"REVERSED STOP ADJUSTMENT,{_security.Symbol.Value},StopTarget={EntryPrice:F2},Bid={bidPrice:F2},Using MARKET order");
                                StopLossTicket = _algorithm.MarketOrder(_security.Symbol, Quantity, asynchronous: false, tag: "Reversed ATR Stop (Market)");
                            }
                            else
                            {
                                StopLossTicket = _algorithm.StopMarketOrder(_security.Symbol, Quantity, EntryPrice, tag: "Reversed ATR Stop");
                            }
                        }
                        else
                        {
                            // NO VALID PRICES for reversed stop - place stop order anyway
                            _algorithm.Log($"NO PRICES for reversed stop,{_security.Symbol.Value},Bid={bidPrice},Ask={askPrice},Placing stop anyway at {EntryPrice:F2}");
                            StopLossTicket = _algorithm.StopMarketOrder(_security.Symbol, Quantity, EntryPrice, tag: "Reversed ATR Stop");
                        }
                    }
                    else
                    {
                        // Backtest mode - just place stop order normally
                        StopLossTicket = _algorithm.StopMarketOrder(_security.Symbol, Quantity, EntryPrice, tag: "Reversed ATR Stop");
                    }
                    
                    Reversed = true;
                }
            }

            // CHECK BACKUP STOPS (MOVED OUTSIDE!)
            foreach (var backup in _backupStops.ToList())
            {
                if (backup.OrderId == orderTicket.OrderId)
                {
                    // If backup was REJECTED, not filled
                    if (order.Status == OrderStatus.Invalid)
                    {
                        _algorithm.Log($"BACKUP STOP REJECTED,{_security.Symbol},Adding to synthetic");
                        
                        // Add the rejected quantity to synthetic monitoring
                        AddSyntheticProtection((int)backup.Quantity);
                        _backupStops.Remove(backup);
                        break;
                    }
                    
                    _algorithm.Log($"BACKUP STOP FILLED,{_security.Symbol}");
                    
                    // Get current position after backup fill
                    var currentPosition = (int)_algorithm.Portfolio[_security.Symbol].Quantity;
                    
                    // If ANY position remains after backup fill, exit immediately at market
                    if (currentPosition != 0)
                    {
                        _algorithm.Log($"BACKUP PARTIAL,{_security.Symbol},Remaining={currentPosition},Market exit");
                        _algorithm.MarketOrder(_security.Symbol, -currentPosition, tag: "Complete exit after backup");
                        
                        // Cancel main stop if exists
                        if (StopLossTicket != null && StopLossTicket.Status == OrderStatus.Submitted)
                        {
                            StopLossTicket.Cancel();
                        }
                        
                        // Clear all tracking
                        _lastStopQuantity = 0;
                        StopLossTicket = null;
                    }
                    
                    _backupStops.Remove(backup);
                    // REMOVED EnsureFullPositionProtected line - we market exit instead
                    
                    break;
                }
            }
        }
        
        private void EnsureFullPositionProtected(OrderEvent order)
        {
            // Get ACTUAL current position from portfolio
            var currentPosition = (int)_algorithm.Portfolio[_security.Symbol].Quantity;
            
            // If somehow flat, clean up
            if (currentPosition == 0)
            {
                CancelAllStops();  // NOW WE USE THE HELPER!
                return;
            }
            
            // Desired stop quantity (opposite of position)
            var desiredStopQty = -currentPosition;
            
            // CASE 1: No stop exists yet - create it
            if (StopLossTicket == null || 
                StopLossTicket.Status == OrderStatus.Canceled ||
                StopLossTicket.Status == OrderStatus.Invalid)
            {
                _algorithm.Log($"STOP CREATE,{_security.Symbol},Position={currentPosition},StopQty={desiredStopQty}");
                StopLossTicket = _algorithm.StopMarketOrder(_security.Symbol, desiredStopQty, StopLossPrice, tag: "ATR Stop");
                _lastStopQuantity = desiredStopQty;
                
                // Update Quantity to match what we're protecting
                Quantity = currentPosition;
                return;
            }
            
            // CASE 2: Stop exists but wrong size - UPDATE IT!
            if (_lastStopQuantity != desiredStopQty)
            {
                _algorithm.Log($"STOP UPDATE NEEDED,{_security.Symbol},Current={_lastStopQuantity},Desired={desiredStopQty}");
                
                // Try atomic update
                var updateFields = new UpdateOrderFields
                {
                    Quantity = desiredStopQty,
                    StopPrice = StopLossPrice,
                    Tag = $"ATR Stop (Updated for {currentPosition} shares)"
                };
                
                var response = StopLossTicket.Update(updateFields);
                
                if (response.IsSuccess)
                {
                    _algorithm.Log($"STOP UPDATE SUCCESS,{_security.Symbol},NewQty={desiredStopQty}");
                    _lastStopQuantity = desiredStopQty;
                    Quantity = currentPosition;
                }
                else
                {
                    // UPDATE FAILED - Place backup stop for uncovered shares
                    var uncoveredQty = desiredStopQty - _lastStopQuantity;
                    _algorithm.Log($"STOP UPDATE FAILED,{_security.Symbol},Placing backup for {uncoveredQty} shares");
                    
                    var backupStop = _algorithm.StopMarketOrder(_security.Symbol, uncoveredQty, StopLossPrice, tag: "Backup Stop");
                    _backupStops.Add(backupStop);
                    
                    // USE THE HELPER METHOD HERE!
                    AddSyntheticProtection(uncoveredQty);
                }
            }
            else
            {
                _algorithm.Log($"STOP CORRECT,{_security.Symbol},Already protecting {currentPosition} shares");
            }
        }

        public void CancelAllStops()
        {
            // Cancel main stop
            if (StopLossTicket != null && 
                (StopLossTicket.Status == OrderStatus.Submitted || 
                StopLossTicket.Status == OrderStatus.PartiallyFilled))
            {
                StopLossTicket.Cancel();
            }
            
            // Cancel any backup stops
            foreach (var backup in _backupStops)
            {
                if (backup.Status == OrderStatus.Submitted)
                {
                    backup.Cancel();
                }
            }
            _backupStops.Clear();
            _lastStopQuantity = 0;
        }

        private void AddSyntheticProtection(int uncoveredQty)
        {
            // Get current position and validate FIRST
            var currentPosition = (int)_algorithm.Portfolio[_security.Symbol].Quantity;
            if (currentPosition == 0) return; // No position to protect
            
            // Calculate what's already protected
            var alreadyProtected = _lastStopQuantity;
            if (_algorithm._syntheticStops.ContainsKey(_security.Symbol))
            {
                alreadyProtected += _algorithm._syntheticStops[_security.Symbol].Quantity;
            }
            
            // Check if we need any protection
            var actuallyNeeded = Math.Abs(currentPosition) - Math.Abs(alreadyProtected);
            if (actuallyNeeded <= 0)
            {
                _algorithm.Log($"SYNTHETIC SKIP,{_security.Symbol},Already fully protected");
                return;
            }
            
            // Only add what's actually needed
            var toAdd = Math.Min(Math.Abs(uncoveredQty), actuallyNeeded) * Math.Sign(uncoveredQty);
            
            // Add uncovered shares to synthetic stop monitoring
            if (!_algorithm._syntheticStops.ContainsKey(_security.Symbol))
            {
                _algorithm._syntheticStops[_security.Symbol] = new SyntheticStop
                {
                    TargetPrice = StopLossPrice,
                    Quantity = toAdd,
                    Timeout = _algorithm.Time.AddMinutes(10),
                    SymbolData = this,
                    IsLong = currentPosition > 0
                };
                
                // Add high-resolution data for monitoring
                if (!_algorithm.Securities[_security.Symbol].Subscriptions.Any(x => x.Resolution == Resolution.Second))
                {
                    _algorithm.AddEquity(_security.Symbol, Resolution.Second);
                }
                _algorithm.Log($"SYNTHETIC PROTECTION ADDED,{_security.Symbol},Qty={toAdd}");
            }
            else
            {
                // VALIDATE before accumulating
                var existingStop = _algorithm._syntheticStops[_security.Symbol];
                existingStop.Quantity += toAdd;
                _algorithm.Log($"SYNTHETIC PROTECTION ACCUMULATED,{_security.Symbol},Added={toAdd},Total={existingStop.Quantity}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            if (Consolidator != null)
            {
                _algorithm.SubscriptionManager.RemoveConsolidator(_security.Symbol, Consolidator);
                Consolidator.Dispose();
            }
            
            _disposed = true;
        }
    } 

    public class CustomSecurityInitializer : BrokerageModelSecurityInitializer
    {
        private readonly ISecuritySeeder _seeder;
        
        public CustomSecurityInitializer(IBrokerageModel brokerageModel, ISecuritySeeder securitySeeder)
            : base(brokerageModel, securitySeeder)
        { 
            _seeder = securitySeeder;
        }

        public override void Initialize(Security security)
        {
            base.Initialize(security);
            security.SetDataNormalizationMode(DataNormalizationMode.Raw);
            security.SetBuyingPowerModel(new SecurityMarginModel(4.0m));
            _seeder.SeedSecurity(security);
        }
    }
    
    public class SyntheticEntry
    {
        public decimal TargetPrice { get; set; }
        public int Quantity { get; set; }
        public DateTime Timeout { get; set; }
        public SymbolData SymbolData { get; set; }
    }

    public class SyntheticStop
    {
        public decimal TargetPrice { get; set; }
        public int Quantity { get; set; }
        public DateTime Timeout { get; set; }
        public SymbolData SymbolData { get; set; }
        public bool IsLong { get; set; }
    }
}// Paste your QuantConnect C# algorithm code here
