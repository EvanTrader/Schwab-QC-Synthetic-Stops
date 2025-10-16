# Schwab Synthetic Stops for QuantConnect

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Python 3.8+](https://img.shields.io/badge/python-3.8+-blue.svg)](https://www.python.org/downloads/)
[![QuantConnect](https://img.shields.io/badge/QuantConnect-Compatible-green.svg)](https://www.quantconnect.com/)

A sophisticated solution for Charles Schwab's stop order restrictions in QuantConnect algorithms. This library provides high-frequency monitoring and automatic market order execution when Schwab rejects stop orders within the bid-ask spread.

## 🎯 Problem Solved

**Charles Schwab rejects stop orders** when the stop price falls within the current bid-ask spread. This creates a significant challenge for algorithmic traders who rely on precise stop loss placement and execution for both position entry and risk management. Especially for QuantConnect algos, because QC symbol quote data is often different from Schwab’s - particularly right off the open and in high market volatility.

**Traditional approaches fail because:**
- Stop orders get rejected with "stop price must be outside bid-ask spread" errors
- Market orders execute immediately at potentially worse prices
- Manual monitoring is impractical for high-frequency strategies
- Limit orders defeat the purpose of breakout entries, stop losses

**My solution:**
- **Automatic detection** of Schwab's specific rejection messages
- **High-frequency monitoring** (second resolution) of price movements
- **Intelligent execution** with stop market orders when possible, market orders when necessary
- **Broker-agnostic design** that only activates on Schwab rejections

## 🚀 Key Features

### Core Functionality
- **Schwab Rejection Detection**: Automatically identifies Schwab's stop order rejection messages
- **High-Frequency Monitoring**: Uses second-resolution data for precise price tracking
- **Dual Execution Strategy**: Places stop market orders when bid tightens, market orders when price crosses target
- **Backup Stop System**: Ensures positions are fully protected with backup stops and synthetic monitoring
- **Position Validation**: Ensures synthetic stops match actual portfolio positions
- **Timeout Protection**: Prevents infinite monitoring with configurable timeouts

### Advanced Features
- **Price Improvement Detection**: Adjusts stop levels based on better-than-expected fills
- **Dead Stock Handling**: Automatically removes monitoring for stocks with invalid prices
- **Memory Management**: Proper cleanup of monitoring resources
- **Comprehensive Logging**: Detailed logs for debugging and performance analysis

### Broker Compatibility
- **Charles Schwab**: Full synthetic stops support with automatic activation
- **Other Brokers**: Standard stop orders work normally (no synthetic overhead)
- **Easy Switching**: Change brokers with a single line of code

## 📊 Technical Architecture

### Synthetic Entry Flow
```
1. Place Stop Order → 2. Schwab Rejects → 3. Add to Synthetic Monitoring
                                    ↓
4. High-Frequency Price Check → 5. Bid Tightens? → 6. Place Stop Market Order
                                    ↓
7. Price Crosses Target? → 8. Execute Market Order
```

### Synthetic Stop Flow
```
1. Place Stop Loss → 2. Schwab Rejects → 3. Add to Synthetic Monitoring
                                    ↓
4. High-Frequency Price Check → 5. Ask/Bid Tightens? → 6. Place Stop Market Order
                                    ↓
7. Price Crosses Target? → 8. Execute Market Order (Exit Position)
```

### Data Structures
- **SyntheticEntry**: Tracks entry orders with target price, quantity, timeout
- **SyntheticStop**: Tracks stop loss orders with position validation
- **SchwabSyntheticStops**: Main handler class with monitoring logic

### Backup Stop System
A comprehensive backup stop system that ensures positions are always protected:

- **Position Validation**: Continuously validates that stop orders match actual portfolio positions
- **Stop Updates**: Automatically updates stop orders when position sizes change
- **Backup Stops**: Places additional stop orders if primary stop updates fail
- **Synthetic Fallback**: Uses synthetic monitoring for any uncovered shares
- **Emergency Exits**: Market orders for remaining positions if backup stops fail

## 🛠️ Installation & Usage

### Basic Integration

The synthetic stops implementation is included directly in `orb_example.py`. To use it in your own algorithm:

```python
# Copy the synthetic stops classes from orb_example.py:
# - SyntheticEntry
# - SyntheticStop  
# - SchwabSyntheticStops

class MyAlgorithm(QCAlgorithm):
    def Initialize(self):
        # Initialize synthetic stops handler
        self.synthetic_stops = SchwabSyntheticStops(self)
        
        # Set up Schwab brokerage
        self.SetBrokerageModel(BrokerageName.CharlesSchwab, AccountType.Margin)
    
    def OnData(self, data):
        # Process synthetic stops on high-frequency data
        if data.Time.second != 0:
            self.synthetic_stops.process_synthetic_entries(data)
            self.synthetic_stops.process_synthetic_stops(data)
            return
        
        # Your main strategy logic here
        pass
    
    def OnOrderEvent(self, order_event):
        # Handle Schwab rejections
        if order_event.Status == OrderStatus.Invalid:
            if self.synthetic_stops.is_schwab_rejection(order_event.Message):
                self.HandleSchwabRejection(order_event)
```

### Complete ORB Example

See `orb_example.py` for a full Opening Range Breakout implementation that demonstrates:
- Universe selection (top liquidity stocks by dollar volume)
- ORB entry logic with synthetic stops
- Risk management with ATR-based stops
- Daily position management

**⚠️ Important Note**: This example algo uses completely random parameters that have not been tested or optimized. It serves only as a boilerplate to demonstrate synthetic stops integration during wide bid ask spreads, and should not itself be used for live trading.

## ⚙️ Configuration

### Algorithm Parameters
```python
# Risk management
self.stop_loss_risk_size = 0.02  # 2% portfolio risk per position
self.stop_loss_atr_distance = 0.15  # ATR multiplier for stop distance

# Synthetic stops
self.synthetic_timeout_minutes = 10  # Full trading day timeout
self.price_tolerance = 0.01  # Price tolerance for stop placement
```

### Brokerage Settings
```python
# For Schwab (with synthetic stops)
self.SetBrokerageModel(BrokerageName.CharlesSchwab, AccountType.Margin)

# For other brokers (standard stops)
self.SetBrokerageModel(BrokerageName.Alpaca, AccountType.Margin)
```

### Proactive Spread Detection (Optional)
For lower latency, you can detect wide bid/ask spreads before placing stop orders and go directly to synthetic monitoring:

```python
def should_use_synthetic_stops(self, symbol, stop_price):
    """Check if bid/ask spread is too wide for stop orders."""
    if self.BrokerageName != BrokerageName.CharlesSchwab:
        return False
    
    security = self.Securities[symbol]
    if not security.HasData:
        return False
    
    bid_ask_spread = security.AskPrice - security.BidPrice
    spread_threshold = security.Price * 0.005  # 0.5% of current price
    
    # If spread is wide, use synthetic stops directly
    if bid_ask_spread > spread_threshold:
        self.Log(f"Wide spread detected for {symbol}: {bid_ask_spread:.4f} > {spread_threshold:.4f}")
        return True
    
    return False

def place_stop_with_spread_check(self, symbol, quantity, stop_price):
    """Place stop order with proactive spread detection."""
    if self.should_use_synthetic_stops(symbol, stop_price):
        # Go directly to synthetic monitoring (skip rejection round trip)
        self.synthetic_stops.add_synthetic_stop(
            symbol=symbol,
            target_price=stop_price,
            quantity=quantity,
            order_id=None
        )
        self.Log(f"Proactive synthetic stop for {symbol} at {stop_price}")
    else:
        # Place normal stop order
        self.StopMarketOrder(symbol, quantity, stop_price)
```

**Benefits of Proactive Detection:**
- **Lower latency**: No waiting for rejection round trip
- **Better execution**: Synthetic monitoring starts immediately
- **Reduced order traffic**: Fewer rejected orders to Schwab
- **Configurable thresholds**: Adjust spread tolerance as needed

## 📈 Performance Characteristics

### Computational Overhead
- **Minimal impact** when not using Schwab (no synthetic monitoring)
- **Second-resolution data** only for symbols with rejected orders
- **Automatic cleanup** prevents memory leaks
- **Efficient filtering** with early exit conditions

### Execution Quality
- **Better fills** through stop market orders when possible
- **Guaranteed execution** through market orders when necessary
- **Price improvement** detection and stop adjustment
- **Reduced slippage** compared to immediate market orders

### Risk Management
- **Position validation** ensures synthetic stops match portfolio
- **Timeout protection** prevents infinite monitoring
- **Dead stock detection** handles invalid price scenarios
- **Backup stop system** for failed order updates

## 🔮 Extensibility & Future Development

### Schwab Developer Portal Integration
While this implementation is designed for QuantConnect, the core synthetic stop logic can be adapted for Schwab's Developer Portal:

```python
# Future integration possibilities
class SchwabDirectSyntheticStops:
    """Direct integration with Schwab's Developer Portal APIs"""
    
    def __init__(self, api_key, account_id):
        self.schwab_api = SchwabAPI(api_key, account_id)
        self.synthetic_monitor = SyntheticStopMonitor()
    
    def handle_rejection(self, order_id, rejection_reason):
        # Direct API integration for synthetic monitoring
        pass
```

### Multi-Broker Support
The architecture supports easy extension to other brokers with similar restrictions.


## 🧪 Testing & Validation

### Backtesting
```python
# Enable test mode for backtesting synthetic logic
self.test_live_logic = 1  # Simulates Schwab rejections in backtest
```

Schwab does not support paper trading via QuantConnect integration, so there’s no way to “test” the rejection handling without being live. However, in backtesting enabling test_live_logic = 1 can simulate synthetic monitoring (with bid/ask detection rather than a schwab rejection message).

### Live Trading
- **Gradual rollout** with small position sizes
- **Comprehensive logging** for performance analysis
- **Fallback mechanisms** for edge cases
- **Real-time monitoring** of synthetic stop performance

## 📚 Documentation & Support

### Code Examples
- **Basic Integration**: Simple synthetic stops setup
- **ORB Strategy**: Complete Opening Range Breakout implementation
- **Risk Management**: Advanced position sizing and stop management
- **Multi-Strategy**: Can be integrated into any stop order reliant strategy.



## ⚠️ Important Considerations

### QuantConnect Specific

This implementation is designed specifically for QuantConnect's cloud platform. For other environments, such as on raw LEAN or Python to direct broker APIs, code would need refactoring, different order management, or a complete rewrite - but functionality and architecture stands.

### Trading Risks
- **Past performance** does not guarantee future results
- **Synthetic stops** add computational overhead
- **Market conditions** can affect execution quality
- **Always test thoroughly** before live trading

### Legal & Compliance
- **Educational purpose**: This code is for educational and research purposes
- **No guarantees**: No warranties about performance or suitability
- **Use at your own risk**: Always understand the risks before trading

## 🤝 Contributing

Welcoming contributions from the community!
Have and of you experienced this stop order limitation on Schwab's API yourself?  

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- **QuantConnect** for the most amazing quant platform, community, and top notch support!

---

*Built for all reatil algo traders of the QuantConnect commuinity*

**Ready to solve Schwab's stop order restrictions?** Start with our [ORB Example](orb_example.py) and integrate synthetic stops into your own strategies!
