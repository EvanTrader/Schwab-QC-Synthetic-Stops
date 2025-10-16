# Discord Announcement - #livetrading Channel

## 🚀 New Open Source: Schwab Synthetic Stops for QuantConnect

Hey everyone! 👋

I've just open-sourced a solution that many of us have been struggling with - **Schwab's stop order restrictions** in QuantConnect algorithms.

### The Problem We All Know
Charles Schwab rejects stop orders when the stop price falls within the bid-ask spread. This breaks risk management for any strategy that needs precise stop execution, especially momentum strategies like ORB.

### The Solution
**Synthetic Stops** - a high-frequency monitoring system that:
- ✅ **Detects Schwab rejections** automatically
- ✅ **Monitors prices** at second resolution
- ✅ **Places stop market orders** when bid tightens
- ✅ **Executes market orders** when price crosses target
- ✅ **Works with any strategy** - just drop in the module

### What's Included
- **Complete Python implementation** ready for QuantConnect
- **ORB example** with 52-week high universe (500 stocks)
- **Broker-agnostic design** - only activates on Schwab
- **Comprehensive documentation** and integration guide

### Key Technical Features
- **High-frequency monitoring** without performance impact
- **Intelligent execution** (stop orders when possible, market when necessary)
- **Position validation** and timeout protection
- **Price improvement detection** and stop adjustment

### Performance Impact
- **Zero overhead** on other brokers (Alpaca, IBKR, etc.)
- **Minimal overhead** on Schwab (only for rejected orders)
- **Better execution** than immediate market orders
- **Guaranteed execution** when Schwab blocks stops

### Ready to Use
The code is production-ready and includes:
- Complete ORB strategy example
- Risk management with ATR-based stops
- Daily position management
- Comprehensive logging

### Community Impact
This solves a real problem that affects many QuantConnect + Schwab users. The solution is:
- **Open source** under MIT license
- **Well documented** with examples
- **Extensible** for other brokers
- **Community driven** development

### Next Steps
1. **Try the ORB example** - it's ready to run
2. **Integrate into your strategies** - just import the module
3. **Contribute improvements** - we welcome PRs
4. **Share your results** - let's see how it performs

### Repository
🔗 **GitHub**: [Link to be added]
📚 **Documentation**: Complete README with technical details
🧪 **Examples**: ORB strategy with synthetic stops integration

This has been a game-changer for my Schwab strategies. Hope it helps others dealing with the same restrictions!

**Questions?** Drop them below - happy to help with integration or explain the technical details.

---

*Built for the QuantConnect community with innovative solutions for real trading challenges.*
