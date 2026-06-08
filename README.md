# RuneshapePriceChecker

RuneshapePriceChecker is a Path of Exile 2 pricing tool for the Runes of Aldur League Mechanic.

It reads visible rows on the runeshape page with OCR (Optical Character Recognition), looks up live prices from poe.ninja, and renders color-coded values beside each row.

## Requirements

- Windows
- .NET 8 SDK
- Your in-game `UI Brightness` setting, under `Graphics`, must be set above `-0.8`, with it ideally being at least `0.0`. If you set it below `0.0`, its more likely that it will incorrectly match the text with the wrong items, and if you set it below `-0.8`, it may not be able to detect the text on the interface at all.
- Borderless Windowed or Windowed display mode — exclusive fullscreen blocks screen capture entirely. The tool will warn you with a popup if it detects fullscreen mode.

## How it Looks
![example](https://i.vgy.me/1XkXx8.png)

## Known Issues / Limitations
- The new Skills and Supports don't have price data on poe.ninja, so the tool can't display prices for them.
- If you use Lossless Scaling with this tool, you should use the 'WGC' Capture API. The tool may cause frame pacing issues if you are using the 'DXGI' API.

## Troubleshooting

- No value on known items:
	- Confirm the item exists in the selected league on poe.ninja.
	- Enable debug logging in `config/appsettings.json` to inspect OCR output and normalized matches.
	- Check `ocr-debug/` for captured images when `SaveDebugImages` is enabled.
	- If n/a appears in logging next to an item that is available on poe ninja with a price, or if the text isn't matching correctly, submit an issue.
- No OCR output:
	- Confirm you're in `borderless windowed` or `windowed` mode on a supported resolution (exclusive fullscreen is not supported).
	- Confirm you're tabbed into the game, so the game is in the foreground.
	- A popup will warn you on startup if your UI Brightness is too low or if fullscreen mode is detected — follow the advice in those popups.
	- Enable `OCR:DebugOverlay` in `config/appsettings.json` to see what region the tool is capturing.
	- Enable `OCR:SaveDebugImages` in `config/appsettings.json` to inspect captured images in the `ocr-debug/` folder.
- No overlay visible:
	- Enable `OCR:DebugOverlay` in `config/appsettings.json` to show the red capture bounds overlay.
	- If the overlay appears but is misaligned, your resolution profile may need tuning (see `ADDING_A_RESOLUTION.md`).
- Lossless Scaling or other overlay tools behaving oddly:
	- The price overlay now fully destroys its window when there are no prices to show, so it shouldn't interfere with frame generation or scaling tools. If you still see issues, make sure you're on the latest version.
- My resolution isn't supported:
	- If you have the time and basic coding knowledge, you can add a new profile by following the step-by-step guide in `ADDING_A_RESOLUTION.md`. Interpolated profiles for 1440p, 3440×1440, and 4K are included but untested.
	- The `tests/ResolutionVisualizer` tool can help you tune capture region offsets visually.

## Quick Start

```powershell
cd "C:/1.Path stuff/RuneshapePriceChecker"
dotnet run --project RuneshapePriceChecker.csproj -c Release
```

For development with hot reload:

```powershell
dotnet watch --project RuneshapePriceChecker.csproj run
```

To produce a single-file release build:

```powershell
dotnet publish RuneshapePriceChecker.csproj -c Release
```

## Testing

The project includes an automated test suite that runs with mock data — no game window needed.

```powershell
powershell -ExecutionPolicy Bypass -File "./tests/run-all.ps1"
```

The suite covers pricing accuracy, OCR parsing, and updater logic. The release build runs these tests automatically before packaging.

Additional test tools in `tests/`:
- `OcrPricingSimulator` — drives the automated pricing and parsing checks.
- `ResolutionVisualizer` — helps tune capture region offsets when adding a new resolution profile (see `ADDING_A_RESOLUTION.md`).

## Configuration

Runtime settings live in `config/appsettings.json`.

```json
{
	"App": {
		"DebugLogging": false
	},
	"Pricing": {
		"League": "Runes of Aldur",
		"RedThreshold": 0.5,
		"OrangeThreshold": 1.0,
		"GreenThreshold": 5.0,
		"DisplayCurrency": "exalt"
	},
	"OCR": {
		"Language": "eng",
		"SaveDebugImages": false,
		"DebugOverlay": false,
		"HideDebugOverlayWhenInterfaceNotDetected": false
	},
	"Update": {
		"AutoUpdate": true,
		"IgnorePrereleases": false
	}
}
```

Settings reload automatically every 5 seconds through `SettingsController`.

| Pricing Key | Type | Default | Description |
|---|---|---|---|
| `League` | string | `"Runes of Aldur"` | Poe.ninja league name |
| `RedThreshold` | decimal | `0.5` | Chaos/exalt value at or below which the label shows red |
| `OrangeThreshold` | decimal | `1.0` | Chaos/exalt value at or below which the label shows orange (must be > RedThreshold) |
| `GreenThreshold` | decimal | `5.0` | Chaos/exalt value at or above which the label shows green (must be > OrangeThreshold) |
| `DisplayCurrency` | `"exalt"` or `"chaos"` | `"exalt"` | Currency used for rendered values |

| OCR Key | Type | Default | Description |
|---|---|---|---|
| `Language` | string | `"eng"` | Tesseract language data |
| `SaveDebugImages` | bool | `false` | Save captured/processed OCR images to `ocr-debug/` |
| `DebugOverlay` | bool | `false` | Show a red capture-bounds overlay on screen |
| `HideDebugOverlayWhenInterfaceNotDetected` | bool | `false` | Hide the overlay when the league panel isn't detected |

| Update Key | Type | Default | Description |
|---|---|---|---|
| `AutoUpdate` | bool | `true` | Check for and apply updates on startup |
| `IgnorePrereleases` | bool | `false` | Skip prerelease versions when checking for updates |

## What It Does

- Detects the PoE2 window and captures a profile-based OCR region.
- Reads item names from the list with Tesseract OCR using native auto-layout.
- Parses quantity prefixes like `1x`, `3x`, and OCR-misread quantities like `Lx` or `ix`.
- Fetches market prices from poe.ninja and caches them.
- Multiplies price by detected quantity before rendering.
- Displays a side overlay with value labels and threshold-based colors.
- Resolves range prices for unique item categories and uncut gems when exact prices aren't available.
- Applies tier fallbacks: GREATER/PERFECT orbs and runes fall back to their base item price.
- Matches OCR-smeared text with single-letter-off fuzzy correction against known pricing keys.

## How Pricing Works

1. The tool captures the OCR region and extracts text with Tesseract's auto-layout mode.
2. OCR text is normalized, cleaned of quantity prefixes and OCR artifacts, then mapped to pricing keys.
3. A pricing cache refreshes from poe.ninja on a fixed interval (default: 10 minutes).
4. Lookup uses multiple candidates per item — normalized name, level-stripped, orb-suffix-stripped, and alias-expanded forms (e.g. `gcp` → `Gemcutter's Prism`).
5. If no exact price is found, the system tries tier fallbacks, unique category min/max ranges, and uncut gem family ranges.
6. As a last resort, single-letter-off fuzzy matching corrects common OCR substitution errors against known pricing keys.
7. Matched quotes are adjusted by detected quantity before rendering.
8. Overlay output is rendered next to the captured item rows with threshold-based color coding.

If an item is not recognized or not available in the current pricing data, it doesn't show a value.

OCR resolution profiles define capture region offsets for each supported resolution.
If your PoE2 resolution is unsupported, OCR and overlay pricing are disabled and an error popup lists supported resolutions.
Untested resolutions will show a warning popup on startup.

## Price Sources

The app fetches PoE2 economy data from poe.ninja.

- Exchange endpoint: `/poe2/api/economy/exchange/current/overview`
- Stash item endpoint: `/poe2/api/economy/stash/current/item/overview`

Refresh types:

- `Currency`
- `Expedition`
- `UncutGems`
- `Runes`
- `Verisium`
- `UniqueWeapons`
- `UniqueArmours`
- `UniqueAccessories`

Unique item types (Weapons, Armours, Accessories) are fetched from the stash endpoint; all other types use the exchange endpoint.
