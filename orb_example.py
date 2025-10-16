"""
Opening Range Breakout (ORB) Example with Schwab Synthetic Stops
================================================================

This example demonstrates how to integrate Schwab synthetic stops into a basic
Opening Range Breakout strategy using QuantConnect and Python.

Strategy Overview:
- Universe: 200 stocks with 52-week high/15-week low momentum
- Entry: Breakout above/below 3-minute opening range
- Risk Management: ATR-based stop losses with synthetic stop handling
- Broker: Charles Schwab (with synthetic stops) or other brokers (standard stops)
- Positions: Maximum 8 concurrent positions

Author: Evan L
License: MIT
"""

from AlgorithmImports import *
from dataclasses import dataclass
from typing import Optional

# =============================================================================
# SCHWAB SYNTHETIC STOPS IMPLEMENTATION
# =============================================================================

@dataclass
class SyntheticEntry:
    """Tracks synthetic entry orders for Schwab rejection handling."""
    symbol: str
    target_price: float
    quantity: int
    timeout: datetime
    side: int  # OrderSide.Buy = 1, OrderSide.Sell = -1
    original_order_id: Optional[str] = None

@dataclass
class SyntheticStop:
    """Tracks synthetic stop orders for Schwab rejection handling."""
    symbol: str
    target_price: float
    quantity: int
    timeout: datetime
    side: int  # OrderSide.Buy = 1, OrderSide.Sell = -1
    original_order_id: Optional[str] = None

class SchwabSyntheticStops:
    """
    Handles Schwab's stop order restrictions with synthetic monitoring.
    
    This class provides high-frequency monitoring and automatic execution
    when Schwab rejects stop orders within the bid-ask spread.
    """
    
    def __init__(self, algorithm):
        self.algorithm = algorithm
        self.synthetic_entries = {}
        self.synthetic_stops = {}
        self.price_tolerance = 0.01
        self.synthetic_timeout_minutes = 10
    
    def is_schwab_rejection(self, order_message: str) -> bool:
        """Check if order rejection is due to Schwab's stop price restrictions."""
        rejection_keywords = [
            "stop price must be",
            "stop order rejected", 
            "invalid stop price",
            "stop price outside spread"
        ]
        return any(keyword in order_message.lower() for keyword in rejection_keywords)
    
    def handle_entry_rejection(self, symbol: str, order_id: str, target_price: float, 
                             quantity: int, rejection_message: str):
        """Handle rejected entry orders by adding to synthetic monitoring."""
        if symbol in self.synthetic_entries:
            return  # Already monitoring
        
        # Add high-resolution data for monitoring
        if not any(sub.Resolution == Resolution.Second for sub in self.algorithm.Securities[symbol].Subscriptions):
            self.algorithm.AddEquity(symbol, Resolution.Second)
        
        self.synthetic_entries[symbol] = SyntheticEntry(
            symbol=symbol,
            target_price=target_price,
            quantity=quantity,
            timeout=self.algorithm.Time.AddMinutes(self.synthetic_timeout_minutes),
            side=1 if quantity > 0 else -1,  # OrderSide.Buy = 1, OrderSide.Sell = -1
            original_order_id=order_id
        )
        
        self.algorithm.Log(f"SYNTHETIC ENTRY MONITOR: {symbol} - Target={target_price:.2f}, Qty={quantity}")
    
    def handle_stop_rejection(self, symbol: str, order_id: str, target_price: float, 
                            quantity: int, rejection_message: str):
        """Handle rejected stop orders by adding to synthetic monitoring."""
        if symbol in self.synthetic_stops:
            return  # Already monitoring
        
        # Add high-resolution data for monitoring
        if not any(sub.Resolution == Resolution.Second for sub in self.algorithm.Securities[symbol].Subscriptions):
            self.algorithm.AddEquity(symbol, Resolution.Second)
        
        self.synthetic_stops[symbol] = SyntheticStop(
            symbol=symbol,
            target_price=target_price,
            quantity=quantity,
            timeout=self.algorithm.Time.AddMinutes(self.synthetic_timeout_minutes),
            side=-1 if quantity < 0 else 1,  # OrderSide.Sell = -1, OrderSide.Buy = 1
            original_order_id=order_id
        )
        
        self.algorithm.Log(f"SYNTHETIC STOP MONITOR: {symbol} - Target={target_price:.2f}, Qty={quantity}")
    
    def process_synthetic_entries(self, data_slice):
        """Process synthetic entry monitoring on high-frequency data."""
        for symbol, entry in list(self.synthetic_entries.items()):
            if not self.algorithm.Securities.ContainsKey(symbol):
                continue
            
            security = self.algorithm.Securities[symbol]
            current_price = security.Price
            bid_price = security.BidPrice
            ask_price = security.AskPrice
            
            # Check for timeout or dead stock
            if (self.algorithm.Time >= entry.timeout or 
                bid_price == 0 or ask_price == 0):
                self.algorithm.Log(f"SYNTHETIC TIMEOUT: {symbol} - Removing from monitoring")
                self.synthetic_entries.pop(symbol, None)
                continue
            
            # For long entries
            if entry.quantity > 0:
                if ask_price > 0 and ask_price <= entry.target_price + self.price_tolerance:
                    # Can place stop order now
                    self.algorithm.Log(f"SYNTHETIC STOP PLACED: {symbol} - Ask={ask_price:.2f}")
                    self.algorithm.StopMarketOrder(symbol, entry.quantity, entry.target_price, tag="Synthetic Entry")
                    self.synthetic_entries.pop(symbol, None)
                elif current_price > entry.target_price:
                    # Price crossed - execute market order
                    self.algorithm.Log(f"SYNTHETIC CROSS: {symbol} - Price={current_price:.2f}>Target={entry.target_price:.2f}")
                    self.algorithm.MarketOrder(symbol, entry.quantity, tag="Synthetic Entry (Cross)")
                    self.synthetic_entries.pop(symbol, None)
            
            # For short entries
            else:
                if bid_price > 0 and bid_price >= entry.target_price - self.price_tolerance:
                    # Can place stop order now
                    self.algorithm.Log(f"SYNTHETIC STOP PLACED: {symbol} - Bid={bid_price:.2f}")
                    self.algorithm.StopMarketOrder(symbol, entry.quantity, entry.target_price, tag="Synthetic Entry")
                    self.synthetic_entries.pop(symbol, None)
                elif current_price < entry.target_price:
                    # Price crossed - execute market order
                    self.algorithm.Log(f"SYNTHETIC CROSS: {symbol} - Price={current_price:.2f}<Target={entry.target_price:.2f}")
                    self.algorithm.MarketOrder(symbol, entry.quantity, tag="Synthetic Entry (Cross)")
                    self.synthetic_entries.pop(symbol, None)
    
    def process_synthetic_stops(self, data_slice):
        """Process synthetic stop monitoring on high-frequency data."""
        for symbol, stop in list(self.synthetic_stops.items()):
            if not self.algorithm.Securities.ContainsKey(symbol):
                continue
            
            # Check if position still exists
            current_position = int(self.algorithm.Portfolio[symbol].Quantity)
            if current_position == 0:
                self.algorithm.Log(f"SYNTHETIC STOP REMOVED: {symbol} - Position flat")
                self.synthetic_stops.pop(symbol, None)
                continue
            
            security = self.algorithm.Securities[symbol]
            current_price = security.Price
            bid_price = security.BidPrice
            ask_price = security.AskPrice
            
            # Check for timeout
            if self.algorithm.Time >= stop.timeout:
                self.algorithm.Log(f"SYNTHETIC STOP TIMEOUT: {symbol} - Forcing market order")
                self.algorithm.MarketOrder(symbol, stop.quantity, tag="Synthetic Stop (Timeout)")
                self.synthetic_stops.pop(symbol, None)
                continue
            
            # For long positions (need to sell)
            if stop.quantity < 0:  # Selling to exit long
                if bid_price > 0 and bid_price >= stop.target_price - self.price_tolerance:
                    # Can place stop order now
                    self.algorithm.Log(f"SYNTHETIC STOP PLACED: {symbol} - Bid={bid_price:.2f}")
                    self.algorithm.StopMarketOrder(symbol, stop.quantity, stop.target_price, tag="Synthetic Stop")
                    self.synthetic_stops.pop(symbol, None)
                elif current_price < stop.target_price:
                    # Price crossed - execute market order
                    self.algorithm.Log(f"SYNTHETIC STOP CROSS: {symbol} - Price={current_price:.2f}<Target={stop.target_price:.2f}")
                    self.algorithm.MarketOrder(symbol, stop.quantity, tag="Synthetic Stop (Cross)")
                    self.synthetic_stops.pop(symbol, None)
            
            # For short positions (need to buy)
            else:
                if ask_price > 0 and ask_price <= stop.target_price + self.price_tolerance:
                    # Can place stop order now
                    self.algorithm.Log(f"SYNTHETIC STOP PLACED: {symbol} - Ask={ask_price:.2f}")
                    self.algorithm.StopMarketOrder(symbol, stop.quantity, stop.target_price, tag="Synthetic Stop")
                    self.synthetic_stops.pop(symbol, None)
                elif current_price > stop.target_price:
                    # Price crossed - execute market order
                    self.algorithm.Log(f"SYNTHETIC STOP CROSS: {symbol} - Price={current_price:.2f}>Target={stop.target_price:.2f}")
                    self.algorithm.MarketOrder(symbol, stop.quantity, tag="Synthetic Stop (Cross)")
                    self.synthetic_stops.pop(symbol, None)
    
    def clear_all_monitoring(self):
        """Clear all synthetic monitoring."""
        self.synthetic_entries.clear()
        self.synthetic_stops.clear()

# =============================================================================
# END SYNTHETIC STOPS IMPLEMENTATION
# =============================================================================

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
        
        # Algorithm parameters (simplified for public release)
        self.max_positions = 8
        self.universe_size = 200
        self.atr_threshold = 0.7
        self.atr_period = 14
        self.opening_range_minutes = 3
        self.stop_loss_atr_distance = 0.15
        self.stop_loss_risk_size = 0.02  # 2% portfolio risk per position
        
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
            self.date_rules.every_day(),
            self.time_rules.after_market_open(self.spy, 0),
            self.ResetDaily
        )
        
        self.Schedule.On(
            self.date_rules.every_day(),
            self.time_rules.before_market_close(self.spy, 1),
            self.LiquidateAll
        )
        
        # Symbol data tracking
        self.symbol_data = {}
        self.entry_placed = False
        
        # Warm up indicators (just need enough for ATR calculation)
        self.SetWarmUp(timedelta(days=self.atr_period))
        
        self.Log("Opening Range Breakout Algorithm with Schwab Synthetic Stops initialized")
    
    def SelectUniverse(self, fundamental_data):
        """
        Select universe of top stocks by volume that hit 52-week high or 15-week low.
        
        Args:
            fundamental_data: Fundamental data for universe selection
            
        Returns:
            List of selected symbols
        """
        # Convert to list for processing
        fundamental_list = list(fundamental_data)
        
        # Filter for liquid stocks above $5
        filtered = [f for f in fundamental_list 
                   if f.Price > 5 and f.HasFundamentalData and f.Symbol != self.spy]
        
        self.Log(f"Universe selection: {len(fundamental_list)} total, {len(filtered)} after basic filters")
        
        # Sort by dollar volume and take top stocks (no momentum filtering)
        selected = sorted(filtered, key=lambda x: x.DollarVolume, reverse=True)[:self.universe_size]
        
        symbols = [f.Symbol for f in selected]
        self.Log(f"Universe selected: {len(symbols)} symbols (top liquidity)")
        
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
        
        # Check if it's after opening range (9:33 AM or later)
        if data.Time.hour < 9 or (data.Time.hour == 9 and data.Time.minute < 33):
            return
        
        self.Log(f"Entry time reached: {data.Time} - Checking for entry candidates")
        
        # Find candidates for entry
        candidates = self.GetEntryCandidates()
        
        if not candidates:
            self.Log("No entry candidates found")
            return
        
        self.Log(f"Found {len(candidates)} entry candidates")
        
        # Place entries for top candidates
        for symbol_data in candidates[:self.max_positions]:
            symbol_data.Scan()
        
        self.entry_placed = True
        self.Log(f"Entry phase completed for {len(candidates)} candidates")
    
    def GetEntryCandidates(self):
        """Get list of symbols ready for entry based on ORB criteria."""
        candidates = []
        
        self.Log(f"Checking {len(self.symbol_data)} symbols for entry candidates")
        
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
        
        # Handle backup stops
        if order_event.Symbol in self.symbol_data:
            symbol_data = self.symbol_data[order_event.Symbol]
            for backup in symbol_data.backup_stops[:]:  # Copy list to avoid modification during iteration
                if backup.OrderId == order_event.OrderId:
                    if order_event.Status == OrderStatus.Invalid:
                        self.Log(f"BACKUP STOP REJECTED: {order_event.Symbol} - Adding to synthetic")
                        # Add the rejected quantity to synthetic monitoring
                        symbol_data.add_synthetic_protection(int(backup.Quantity))
                        symbol_data.backup_stops.remove(backup)
                        break
                    
                    self.Log(f"BACKUP STOP FILLED: {order_event.Symbol}")
                    
                    # Get current position after backup fill
                    current_position = int(self.Portfolio[order_event.Symbol].Quantity)
                    
                    # If ANY position remains after backup fill, exit immediately at market
                    if current_position != 0:
                        self.Log(f"BACKUP PARTIAL: {order_event.Symbol} - Remaining={current_position}, Market exit")
                        self.MarketOrder(order_event.Symbol, -current_position, tag="Complete exit after backup")
                        
                        # Cancel main stop if exists
                        if (symbol_data.stop_loss_ticket and 
                            symbol_data.stop_loss_ticket.Status == OrderStatus.Submitted):
                            symbol_data.stop_loss_ticket.Cancel()
                        
                        # Clear all tracking
                        symbol_data.last_stop_quantity = 0
                        symbol_data.stop_loss_ticket = None
                    
                    symbol_data.backup_stops.remove(backup)
                    break
    
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
        self.Log(f"Daily reset completed at {self.Time} - Entry allowed for today")
    
    def LiquidateAll(self):
        """Liquidate all positions at end of day."""
        if self.Portfolio.Invested:
            self.Log("Liquidating all positions")
            
            # Cancel all stops before liquidating
            for symbol_data in self.symbol_data.values():
                symbol_data.cancel_all_stops()
            
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
        self.atr = algorithm.ATR(self.symbol, atr_period)
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
        
        # Backup stops tracking
        self.last_stop_quantity = 0
        self.backup_stops = []
        
        # Consolidator for opening range
        self.consolidator = algorithm.Consolidate(
            self.symbol, 
            timedelta(minutes=opening_range_minutes),
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
        if self.opening_bar and self.opening_bar.Time.date() == bar.Time.date():
            return
        
        # Update volume SMA
        self.volume_sma.Update(bar.EndTime, bar.Volume)
        
        # Calculate relative volume
        if self.volume_sma.IsReady and self.volume_sma.Current.Value > 0:
            self.relative_volume = bar.Volume / self.volume_sma.Current.Value
        
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
            
            # Place stop loss order with backup protection
            self.stop_loss_ticket = self.algorithm.StopMarketOrder(
                self.symbol, -actual_quantity, self.stop_loss_price, tag="Stop Loss"
            )
            
            # Ensure position is fully protected (backup stops)
            self.ensure_position_protected(order_event)
            
            self.algorithm.Log(
                f"ENTRY FILLED: {self.symbol} - Qty={actual_quantity} - "
                f"Fill={fill_price:.2f} - Stop={self.stop_loss_price:.2f}"
            )
    
    def ensure_position_protected(self, order_event):
        """Ensure the full position is protected with backup stops if needed."""
        # Get actual current position from portfolio
        current_position = int(self.algorithm.Portfolio[self.symbol].Quantity)
        
        # If somehow flat, clean up
        if current_position == 0:
            self.cancel_all_stops()
            return
        
        # Desired stop quantity (opposite of position)
        desired_stop_qty = -current_position
        
        # CASE 1: No stop exists yet - create it
        if (self.stop_loss_ticket is None or 
            self.stop_loss_ticket.Status == OrderStatus.Canceled or
            self.stop_loss_ticket.Status == OrderStatus.Invalid):
            
            self.algorithm.Log(f"STOP CREATE: {self.symbol} - Position={current_position}, StopQty={desired_stop_qty}")
            self.stop_loss_ticket = self.algorithm.StopMarketOrder(
                self.symbol, desired_stop_qty, self.stop_loss_price, tag="ATR Stop"
            )
            self.last_stop_quantity = desired_stop_qty
            self.quantity = current_position
            return
        
        # CASE 2: Stop exists but wrong size - UPDATE IT!
        if self.last_stop_quantity != desired_stop_qty:
            self.algorithm.Log(f"STOP UPDATE NEEDED: {self.symbol} - Current={self.last_stop_quantity}, Desired={desired_stop_qty}")
            
            # Try atomic update
            update_fields = UpdateOrderFields()
            update_fields.Quantity = desired_stop_qty
            update_fields.StopPrice = self.stop_loss_price
            update_fields.Tag = f"ATR Stop (Updated for {current_position} shares)"
            
            response = self.stop_loss_ticket.Update(update_fields)
            
            if response.IsSuccess:
                self.algorithm.Log(f"STOP UPDATE SUCCESS: {self.symbol} - NewQty={desired_stop_qty}")
                self.last_stop_quantity = desired_stop_qty
                self.quantity = current_position
            else:
                # UPDATE FAILED - Place backup stop for uncovered shares
                uncovered_qty = desired_stop_qty - self.last_stop_quantity
                self.algorithm.Log(f"STOP UPDATE FAILED: {self.symbol} - Placing backup for {uncovered_qty} shares")
                
                backup_stop = self.algorithm.StopMarketOrder(
                    self.symbol, uncovered_qty, self.stop_loss_price, tag="Backup Stop"
                )
                self.backup_stops.append(backup_stop)
                
                # Add synthetic protection for uncovered shares
                self.add_synthetic_protection(uncovered_qty)
        else:
            self.algorithm.Log(f"STOP CORRECT: {self.symbol} - Already protecting {current_position} shares")
    
    def add_synthetic_protection(self, uncovered_qty):
        """Add synthetic protection for uncovered shares."""
        # Get current position and validate FIRST
        current_position = int(self.algorithm.Portfolio[self.symbol].Quantity)
        if current_position == 0:
            return  # No position to protect
        
        # Calculate what's already protected
        already_protected = self.last_stop_quantity
        if self.symbol in self.algorithm.synthetic_stops.synthetic_stops:
            already_protected += self.algorithm.synthetic_stops.synthetic_stops[self.symbol].quantity
        
        # Check if we need any protection
        actually_needed = abs(current_position) - abs(already_protected)
        if actually_needed <= 0:
            self.algorithm.Log(f"SYNTHETIC SKIP: {self.symbol} - Already fully protected")
            return
        
        # Only add what's actually needed
        to_add = min(abs(uncovered_qty), actually_needed) * (1 if uncovered_qty > 0 else -1)
        
        # Add uncovered shares to synthetic stop monitoring
        if self.symbol not in self.algorithm.synthetic_stops.synthetic_stops:
            self.algorithm.synthetic_stops.synthetic_stops[self.symbol] = SyntheticStop(
                symbol=self.symbol,
                target_price=self.stop_loss_price,
                quantity=to_add,
                timeout=self.algorithm.Time.AddMinutes(10),
                side=-1 if current_position > 0 else 1,  # OrderSide.Sell = -1, OrderSide.Buy = 1
                original_order_id=None
            )
            
            # Add high-resolution data for monitoring
            if not any(sub.Resolution == Resolution.Second for sub in self.algorithm.Securities[self.symbol].Subscriptions):
                self.algorithm.AddEquity(self.symbol, Resolution.Second)
            
            self.algorithm.Log(f"SYNTHETIC PROTECTION ADDED: {self.symbol} - Qty={to_add}")
        else:
            # Accumulate with existing synthetic stop
            existing_stop = self.algorithm.synthetic_stops.synthetic_stops[self.symbol]
            existing_stop.quantity += to_add
            self.algorithm.Log(f"SYNTHETIC PROTECTION ACCUMULATED: {self.symbol} - Added={to_add}, Total={existing_stop.quantity}")
    
    def cancel_all_stops(self):
        """Cancel all stops and clean up tracking."""
        # Cancel main stop
        if (self.stop_loss_ticket and 
            (self.stop_loss_ticket.Status == OrderStatus.Submitted or 
             self.stop_loss_ticket.Status == OrderStatus.PartiallyFilled)):
            self.stop_loss_ticket.Cancel()
        
        # Cancel any backup stops
        for backup in self.backup_stops:
            if backup.Status == OrderStatus.Submitted:
                backup.Cancel()
        self.backup_stops.clear()
        self.last_stop_quantity = 0

    def Dispose(self):
        """Clean up resources."""
        if self.consolidator:
            self.algorithm.SubscriptionManager.RemoveConsolidator(
                self.symbol, self.consolidator
            )
            self.consolidator.Dispose()
