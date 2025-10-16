# QuantConnect Setup Instructions

## How to Use Synthetic Stops in QuantConnect

### Option 1: Upload Both Files (Recommended)
1. Upload `synthetic_stops.py` to your QuantConnect project
2. Upload `orb_example.py` to the same project
3. Set `orb_example.py` as your main algorithm file
4. The import will work automatically

### Option 2: Single File (Fallback)
If you only want to upload one file:
1. Copy the contents of `synthetic_stops.py` 
2. Paste it at the top of `orb_example.py` (before the algorithm class)
3. Remove the import statement: `from synthetic_stops import SchwabSyntheticStops`
4. Upload only the modified `orb_example.py`

### Option 3: Use Fallback Mode
The ORB example includes a fallback mode that will work even without the synthetic stops module. It will:
- Run the basic ORB strategy
- Log Schwab rejections but not handle them synthetically
- Work on any broker (not just Schwab)

## File Structure in QuantConnect
```
Your Project/
├── main.py (or orb_example.py)
└── synthetic_stops.py
```

## Testing
- The algorithm will work in backtest mode on any broker
- For live trading with Schwab, you need the full synthetic stops module
- The fallback mode is safe for testing and development

## Troubleshooting
- **Import Error**: Make sure both files are in the same project directory
- **Module Not Found**: Use Option 2 (single file) or Option 3 (fallback mode)
- **Schwab Rejections**: Only the full synthetic stops module handles these
