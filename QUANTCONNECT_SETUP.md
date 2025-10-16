# QuantConnect Setup Instructions

## How to Use Synthetic Stops in QuantConnect

### Single File Deployment (Current)
The `orb_example.py` file now contains everything in one file:
1. Upload `orb_example.py` to your QuantConnect project
2. Set it as your main algorithm file
3. Everything works automatically - no imports needed!

### What's Included
- ✅ Complete synthetic stops implementation
- ✅ ORB strategy example
- ✅ Backup stops system
- ✅ Schwab rejection handling
- ✅ No external dependencies

### File Structure in QuantConnect
```
Your Project/
└── main.py (orb_example.py)
```

### Testing
- ✅ Works in backtest mode on any broker
- ✅ Full synthetic stops functionality included
- ✅ Schwab rejection handling built-in
- ✅ Safe for testing and development

### Using in Your Own Algorithm
To use synthetic stops in your own algorithm:
1. Copy the synthetic stops classes from `orb_example.py`:
   - `SyntheticEntry`
   - `SyntheticStop`
   - `SchwabSyntheticStops`
2. Add them to your algorithm file
3. Initialize and use as shown in the example

### Troubleshooting
- **No Import Errors**: Everything is self-contained
- **Works on Any Broker**: Synthetic stops only activate on Schwab rejections
- **Full Functionality**: All features included in single file
