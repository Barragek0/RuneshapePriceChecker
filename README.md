# RuneshapePriceChecker

RuneshapePriceChecker is a Path of Exile 2 pricing tool for the Runes of Aldur League Mechanic.

It reads visible rows on the runeshape page with OCR, looks up live prices from poe.ninja, and renders color-coded values beside each row.

## Requirements

- Windows
- .NET 8 SDK
- Tesseract OCR installed and available in `PATH`. The tool will automatically install it if you don't have it.

## How it Looks
![example](https://i.vgy.me/1XkXx8.png)

## Limitations
- poeninja doesn't currently have prices listed for the new Skills or Supports, so prices for these can't be shown.
- Currently only supports 1080p, if you have a higher resolution monitor and a bit of spare time and basic coding knowledge please read `ADDING_A_RESOLUTION.md` for a step-by-step guide on how you can add another resolution.

## Troubleshooting

- No value on known items:
	- Confirm the item exists in the selected league on poe.ninja.
	- Enable debug logging in `src/appsettings.json` to inspect OCR output and normalized matches.
	- If n/a appears in logging next to an item that is available on poe ninja with a price, or if the text isn't matching correctly, submit an issue.
- No OCR output:
	- Confirm Tesseract is installed and in `PATH`.
	- Confirm you're in `borderless windowed` on a supported resolution.
    - Confirm you're tabbed into the game, so the game is in the foreground.
- My resolution isn't supported:
	- I unfortunately only have 1080p monitors, so I couldn't add higher resolutions. If you have the time and basic coding knowledge, you can add a new profile by following a simple step-by-step guide in `ADDING_A_RESOLUTION.md`.

## Quick Start

```powershell
cd "C:/1.Path stuff/RuneshapePriceChecker"
dotnet build RuneshapePriceChecker.csproj -c Release
dotnet run --project RuneshapePriceChecker.csproj
```

For development with hot reload:

```powershell
dotnet watch --project RuneshapePriceChecker.csproj run
```

## Configuration

Runtime settings live in `src/appsettings.json`.

```json
{
	"App": {
		"EnableDebugLogging": true
	},
	"Pricing": {
		"League": "Runes of Aldur",
		"RedThresholdChaos": 0.5,
		"OrangeThresholdChaos": 1.0,
		"GreenThresholdChaos": 5.0
	},
	"OCR": {
		"Language": "eng",
		"SaveDebugImages": false,
		"ShowCaptureBoundsOverlay": false
	}
}
```

Settings reload automatically every 5 seconds through `SettingsController`.

## What It Does

- Detects the PoE2 window and captures a profile-based OCR region.
- Reads item names from the list with Tesseract OCR.
- Parses quantity prefixes like `1x`, `3x`.
- Fetches market prices from poe.ninja and caches them.
- Multiplies price by detected quantity before rendering.
- Displays a side overlay with value labels and threshold-based colors.

## How Pricing Works

1. A background worker captures OCR text from each row.
2. OCR text is normalized and mapped to pricing keys.
3. A pricing cache refreshes from poe.ninja on a fixed interval.
4. Each OCR row is matched to a quote, then adjusted by quantity.
5. Overlay output is rendered next to the captured exchange rows.

If an item is not recognized or not available in the current pricing data, it doesn't show a value.

OCR resolution profiles now include both static late-row offsets and adaptive row-shift detection parameters.

If your PoE2 resolution is unsupported, OCR and overlay pricing are disabled and an error popup lists supported resolutions.

## Price Sources

The app fetches PoE2 economy data from poe.ninja.

- Exchange endpoint: `/poe2/api/economy/exchange/current/overview`
- Stash item endpoint: `/poe2/api/economy/stash/current/item/overview`

Refresh types:

- `Currency`
- `Runes`
- `Verisium`
- `UniqueWeapons`
- `UniqueArmours`
- `UniqueAccessories`
