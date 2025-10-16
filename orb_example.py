"""
Opening Range Breakout (ORB) Example with Schwab Synthetic Stops
================================================================

This example demonstrates how to integrate Schwab synthetic stops into a basic
Opening Range Breakout strategy using QuantConnect and Python.

Strategy Overview:
- Universe: Top 500 stocks by volume that hit 52-week high monthly
- Entry: Breakout above/below 1-minute opening range
- Risk Management: ATR-based stop losses with synthetic stop handling
- Broker: Charles Schwab (with synthetic stops) or other brokers (standard stops)

Author: Evan L
License: MIT
"""

from AlgorithmImports import *
from synthetic_stops import SchwabSyntheticStops

class OpeningRangeBreakoutAlgorithm(QCAlgorithm):
    """
    Opening Range Breakout algorithm with Schwab synthetic stops integration.
    
    This algorithm demonstrates the complete integration of synthetic stops
    for handling Schwab's stop order restrictions while maintaining
    compatibility with other brokers.
    """
    
    def Initialize(self):
        """Initialize the algorithm with universe selection and synthetic stops."""
        # Set algorithm parameters
        self.SetStartDate(2025, 1, 1)
        self.SetEndDate(2025, 8, 9)
        self.SetCash(30000)
        
        # Algorithm parameters
        self.max_positions = 20
        self.universe_size = 500
        self.atr_threshold = 0.5
        self.atr_period = 14
        self.opening_range_minutes = 1
        self.stop_loss_atr_distance = 0.1
        self.stop_loss_risk_size = 0.01  # 1% portfolio risk per position
        
        # Initialize synthetic stops handler
        self.synthetic_stops = SchwabSyntheticStops(self)
        
        # Set up brokerage (Schwab for synthetic features, others for standard)
        self.SetBrokerageModel(BrokerageName.CharlesSchwab, AccountType.Margin)
        
        # Add SPY for market timing
        self.spy = self.AddEquity("SPY").Symbol
        
        # Universe selection - top 500 by volume with 52-week high
        self.UniverseSettings.Leverage = 4
        self.UniverseSettings.Resolution = Resolution.Minute
        
        # Monthly universe selection
        self.AddUniverse(self.SelectUniverse)
        
        # Schedule daily operations
        self.Schedule.On(
            DateRules.EveryDay(self.spy),
            TimeRules.AfterMarketOpen(self.spy, 0),
            self.ResetDaily
        )
        
        self.Schedule.On(
            DateRules.EveryDay(self.spy),
            TimeRules.BeforeMarketClose(self.spy, 1),
            self.LiquidateAll
        )
        
        # Symbol data tracking
        self.symbol_data = {}
        self.entry_placed = False
        
        # Warm up indicators
        self.SetWarmUp(TimeSpan.FromDays(2 * self.atr_period))
        
        self.Log("Opening Range Breakout Algorithm with Schwab Synthetic Stops initialized")
    
    def SelectUniverse(self, fundamental_data):
        """
        Select universe of top 500 stocks by volume that hit 52-week high.
        
        Args:
            fundamental_data: Fundamental data for universe selection
            
        Returns:
            List of selected symbols
        """
        # Filter for liquid stocks above $5
        filtered = [f for f in fundamental_data 
                   if f.Price > 5 and f.HasFundamentalData and f.Symbol != self.spy]
        
        # Sort by dollar volume and take top 500
        selected = sorted(filtered, key=lambda x: x.DollarVolume, reverse=True)[:self.universe_size]
        
        symbols = [f.Symbol for f in selected]
        self.Log(f"Universe selected: {len(symbols)} symbols")
        
        return symbols
    
    def OnSecuritiesChanged(self, changes):
        """Handle security additions and removals."""
        # Clean up removed securities
        for security in changes.RemovedSecurities:
            if security.Symbol in self.symbol_data:
                self.symbol_data[security.Symbol].Dispose()
                del self.symbol_data[security.Symbol]
            
            if security.Invested:
                self.Liquidate(security.Symbol)
        
        # Initialize new securities
        for security in changes.AddedSecurities:
            if security.Symbol not in self.symbol_data:
                self.symbol_data[security.Symbol] = SymbolData(
                    self, security, self.opening_range_minutes, self.atr_period
                )
    
    def OnData(self, data):
        """Process market data and synthetic stops."""
        if not data or self.IsWarmingUp:
            return
        
        # Process synthetic stops on high-frequency data
        if data.Time.second != 0:
            self.synthetic_stops.process_synthetic_entries(data)
            self.synthetic_stops.process_synthetic_stops(data)
            return
        
        # Skip if entry already placed today
        if self.entry_placed:
            return
        
        # Check if it's entry time (9:31 AM)
        if data.Time.hour != 9 or data.Time.minute != (30 + self.opening_range_minutes):
            return
        
        # Find candidates for entry
        candidates = self.GetEntryCandidates()
        
        if not candidates:
            self.Log("No entry candidates found")
            return
        
        # Place entries for top candidates
        for symbol_data in candidates[:self.max_positions]:
            symbol_data.Scan()
        
        self.entry_placed = True
        self.Log(f"Entry phase completed for {len(candidates)} candidates")
    
    def GetEntryCandidates(self):
        """Get list of symbols ready for entry based on ORB criteria."""
        candidates = []
        
        for symbol, symbol_data in self.symbol_data.items():
            if not self.Securities.ContainsKey(symbol):
                continue
                
            security = self.Securities[symbol]
            
            # Basic filters
            if (security.Price <= 0 or 
                not symbol_data.IsReady or
                symbol_data.RelativeVolume <= 1 or
                symbol_data.ATR <= self.atr_threshold):
                continue
            
            candidates.append(symbol_data)
        
        # Sort by relative volume (momentum)
        candidates.sort(key=lambda x: x.RelativeVolume, reverse=True)
        
        return candidates
    
    def OnOrderEvent(self, order_event):
        """Handle order events including Schwab rejections."""
        # Handle rejected orders
        if order_event.Status == OrderStatus.Invalid:
            self.Log(f"ORDER REJECTED: {order_event.Symbol} - {order_event.Message}")
            
            # Check if this is a Schwab stop order rejection
            if self.synthetic_stops.is_schwab_rejection(order_event.Message):
                self.HandleSchwabRejection(order_event)
            return
        
        # Handle filled orders
        if order_event.Status in [OrderStatus.Filled, OrderStatus.PartiallyFilled]:
            if order_event.Symbol in self.symbol_data:
                self.symbol_data[order_event.Symbol].OnOrderEvent(order_event)
    
    def HandleSchwabRejection(self, order_event):
        """Handle Schwab-specific order rejections with synthetic stops."""
        symbol = order_event.Symbol
        
        if symbol not in self.symbol_data:
            return
        
        symbol_data = self.symbol_data[symbol]
        
        # Determine if this was an entry or stop order
        if (symbol_data.EntryTicket and 
            symbol_data.EntryTicket.OrderId == order_event.OrderId):
            # Entry order rejected
            self.synthetic_stops.handle_entry_rejection(
                symbol=symbol,
                order_id=order_event.OrderId,
                target_price=float(symbol_data.EntryPrice),
                quantity=symbol_data.Quantity,
                rejection_message=order_event.Message
            )
        
        elif (symbol_data.StopLossTicket and 
              symbol_data.StopLossTicket.OrderId == order_event.OrderId):
            # Stop order rejected
            self.synthetic_stops.handle_stop_rejection(
                symbol=symbol,
                order_id=order_event.OrderId,
                target_price=float(symbol_data.StopLossPrice),
                quantity=-symbol_data.Quantity,  # Negative for exits
                rejection_message=order_event.Message
            )
    
    def ResetDaily(self):
        """Reset daily variables."""
        self.entry_placed = False
        self.synthetic_stops.clear_all_monitoring()
        self.Log("Daily reset completed")
    
    def LiquidateAll(self):
        """Liquidate all positions at end of day."""
        if self.Portfolio.Invested:
            self.Log("Liquidating all positions")
            self.Liquidate()
        
        # Clear synthetic monitoring
        self.synthetic_stops.clear_all_monitoring()
        
        # Log daily summary
        total_pnl = self.Portfolio.TotalProfit
        position_count = len([p for p in self.Portfolio.Values if p.Invested])
        self.Log(f"Daily Summary: Positions={position_count}, P&L=${total_pnl:.2f}")


class SymbolData:
    """Tracks individual symbol data and indicators."""
    
    def __init__(self, algorithm, security, opening_range_minutes, atr_period):
        self.algorithm = algorithm
        self.security = security
        self.symbol = security.Symbol
        
        # Indicators
        self.atr = algorithm.ATR(self.symbol, atr_period, Resolution.Daily)
        self.volume_sma = SimpleMovingAverage(atr_period)
        
        # Opening range data
        self.opening_bar = None
        self.relative_volume = None
        
        # Order tracking
        self.entry_price = 0
        self.stop_loss_price = 0
        self.quantity = 0
        self.entry_ticket = None
        self.stop_loss_ticket = None
        
        # Consolidator for opening range
        self.consolidator = algorithm.Consolidate(
            self.symbol, 
            TimeSpan.FromMinutes(opening_range_minutes),
            self.OnDataConsolidated
        )
    
    @property
    def IsReady(self):
        """Check if indicators are ready."""
        return self.atr.IsReady and self.volume_sma.IsReady
    
    @property
    def ATR(self):
        """Get current ATR value."""
        return self.atr.Current.Value if self.atr.IsReady else 0
    
    @property
    def RelativeVolume(self):
        """Get current relative volume."""
        return self.relative_volume or 0
    
    def OnDataConsolidated(self, bar):
        """Handle consolidated bar data for opening range."""
        if self.opening_bar and self.opening_bar.Time.Date == bar.Time.Date:
            return
        
        # Update volume SMA
        self.volume_sma.Update(bar.EndTime, bar.Volume)
        
        # Calculate relative volume
        if self.volume_sma.IsReady and self.volume_sma > 0:
            self.relative_volume = bar.Volume / self.volume_sma
        
        # Store opening bar
        self.opening_bar = bar
    
    def Scan(self):
        """Scan for ORB entry opportunities."""
        if not self.opening_bar or not self.IsReady:
            return
        
        # Determine bar type
        bar_type = "GREEN" if self.opening_bar.Close > self.opening_bar.Open else "RED"
        range_size = self.opening_bar.High - self.opening_bar.Low
        
        self.algorithm.Log(
            f"ORB: {self.symbol} - {bar_type} - "
            f"O={self.opening_bar.Open:.2f} H={self.opening_bar.High:.2f} "
            f"L={self.opening_bar.Low:.2f} C={self.opening_bar.Close:.2f} "
            f"Range={range_size:.3f} RV={self.relative_volume:.2f}"
        )
        
        # Place trades based on bar type
        if self.opening_bar.Close > self.opening_bar.Open:
            # Green bar - long breakout
            entry_price = self.opening_bar.High + 0.01
            stop_price = entry_price - self.algorithm.stop_loss_atr_distance * self.ATR
            self.PlaceTrade(entry_price, stop_price)
        
        elif self.opening_bar.Close < self.opening_bar.Open:
            # Red bar - short breakout
            entry_price = self.opening_bar.Low - 0.01
            stop_price = entry_price + self.algorithm.stop_loss_atr_distance * self.ATR
            self.PlaceTrade(entry_price, stop_price)
    
    def PlaceTrade(self, entry_price, stop_price):
        """Place entry and stop loss orders."""
        # Calculate position size based on risk
        risk_amount = (self.algorithm.stop_loss_risk_size * 
                      self.algorithm.Portfolio.TotalPortfolioValue / 
                      self.algorithm.max_positions)
        
        quantity = int(risk_amount / abs(entry_price - stop_price))
        
        # Apply position limits
        max_quantity = self.algorithm.CalculateOrderQuantity(
            self.symbol, 2.6 / self.algorithm.max_positions
        )
        quantity = int(min(abs(quantity), max_quantity) * (1 if quantity > 0 else -1))
        
        if quantity == 0:
            return
        
        # Store trade parameters
        self.entry_price = entry_price
        self.stop_loss_price = stop_price
        self.quantity = quantity
        
        # Place entry stop order
        self.entry_ticket = self.algorithm.StopMarketOrder(
            self.symbol, quantity, entry_price, tag="Entry"
        )
        
        self.algorithm.Log(
            f"ENTRY ORDER: {self.symbol} - Qty={quantity} - "
            f"Entry={entry_price:.2f} - Stop={stop_price:.2f}"
        )
    
    def OnOrderEvent(self, order_event):
        """Handle order events for this symbol."""
        if not self.entry_ticket or order_event.OrderId != self.entry_ticket.OrderId:
            return
        
        if order_event.Status in [OrderStatus.Filled, OrderStatus.PartiallyFilled]:
            # Entry filled - place stop loss
            fill_price = order_event.FillPrice
            actual_quantity = int(order_event.FillQuantity)
            
            # Update quantity to actual fill
            self.quantity = actual_quantity
            
            # Place stop loss order
            self.stop_loss_ticket = self.algorithm.StopMarketOrder(
                self.symbol, -actual_quantity, self.stop_loss_price, tag="Stop Loss"
            )
            
            self.algorithm.Log(
                f"ENTRY FILLED: {self.symbol} - Qty={actual_quantity} - "
                f"Fill={fill_price:.2f} - Stop={self.stop_loss_price:.2f}"
            )
    
    def Dispose(self):
        """Clean up resources."""
        if self.consolidator:
            self.algorithm.SubscriptionManager.RemoveConsolidator(
                self.symbol, self.consolidator
            )
            self.consolidator.Dispose()
