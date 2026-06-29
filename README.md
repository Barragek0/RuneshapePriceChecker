# RuneshapePriceChecker

RuneshapePriceChecker is a Path of Exile 2 pricing overlay for the Runes of Aldur league mechanic.

It reads item rows from the runeshape panel with OCR (Optical Character Recognition), looks up prices from poe2scout or poe.ninja, and renders color-coded values next to each row.

## Requirements

- Windows 7 or later (Windows 10 build 1809+ recommended for Windows OCR)
- Your in-game **UI Brightness** setting under **Graphics** must be above `-0.8` (ideally `0.0` or higher). Lower values may cause incorrect item matching or prevent text detection entirely.
- **Borderless Windowed** or **Windowed** display mode — exclusive fullscreen blocks screen capture. The tool warns you if it detects fullscreen.

## Download
The latest version can be downloaded here: https://github.com/Barragek0/RuneshapePriceChecker/releases/
- If you want the portable version, download `RuneshapePriceChecker.zip` and extract it to any folder, then run the .exe.
- If you'd rather use an installer, download `RuneshapePriceChecker-Installer.exe` and run it.

## How it Looks

![example](https://i.vgy.me/4Huhu4.png)

## Known Issues / Limitations

- New Skills and Supports don't have price data from pricing sources yet — the tool warns when it detects them.
- If you use Lossless Scaling, use the **WGC** Capture API. The tool may cause frame pacing issues with DXGI.
- When starting the tool with Lossless Scaling active, the cursor may disappear. Enable **Multi-display mode** in Lossless Scaling, then tab out and back in to restore the cursor.

## Troubleshooting

- **Bug report tool** — Click the 🐛 icon in the dashboard to automatically collect logs, crash reports, debug images, and system info into a `.zip` file.
- **No price on known items:**
	- Re-run the initial setup from Settings and verify the capture box matches the example.
	- Confirm the item exists in your selected league and pricing source.
	- Change `Log Level` to **Debug** or **Trace** in Settings — takes effect immediately without restarting the app.
	- Enable Show Debug Overlay in Settings and verify the scan brackets cover the item rows correctly.
	- If "n/a" appears in logs for an item that should have a price, or if text isn't matching correctly, use the 🐛 bug report tool to submit an issue.
- **No OCR output:**
	- Re-run the initial setup from Settings and verify the capture box matches the example.
	- Confirm you're in borderless windowed or windowed mode (exclusive fullscreen not supported).
	- Confirm the game is in the foreground (tabbed in).
	- A popup warns on startup if UI Brightness is too low or fullscreen is detected — follow its advice.
	- Enable Show Debug Overlay in Settings to see what region is being captured.
	- Enable Save Debug Images in Settings to inspect captured images in the `debug-images/` folder.
- **No overlay visible:**
	- Enable Show Debug Overlay in Settings to see the red capture bounds overlay.
	- If the overlay appears misaligned, re-run the initial setup to reposition the capture region.
- **Lossless Scaling or other overlay tools behaving oddly:**
	- If you're using Lossless Scaling, ensure that you set the capture mode to `WGC`.
	- There's a known issue that I cannot find a fix for, causing your mouse cursor to disappear when the app overlays initially get created. To workaround this, enable `Multi-display mode`, scale, tab into the game, tab out of the game, and tab back into the game, and your cursor should then be visible.
	- The price overlay fully destroys its window when there are no prices to show, minimizing interference with frame generation tools. If issues persist on the latest version, use the 🐛 bug report tool to submit an issue.

## Quick Start

```powershell
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

The release build runs these tests automatically before packaging.

Additional test tools in `tests/`:
- `OcrPricingSimulator` — drives the automated pricing and parsing checks.

## Configuration

All settings can be changed from the in-app Settings window (click the gear icon). They're stored in `config/appsettings.json` and reload automatically every 5 seconds.

```json
{
	"App": {
		"LogLevel": "Information",
		"BringToForeground": true,
		"AlwaysOnTop": false,
		"RememberDebugPanel": false,
		"CloseWithPoE2": false,
		"OpenWithPoE2": false,
		"AllOverlaysDisabled": false,
		"PricingOverlay": true,
		"Banner": true
	},
	"Pricing": {
		"PricingSource": "poe2scout",
		"League": "Runes of Aldur",
		"AutoPriceThresholds": true,
		"RedThreshold": 0.5,
		"OrangeThreshold": 1.0,
		"GreenThreshold": 5.0,
		"DisplayCurrency": "exalt"
	},
	"OCR": {
		"Language": "eng",
		"OcrBackend": "windows",
		"CaptureMode": "printwindow",
		"SaveDebugImages": false,
		"DebugImageIntervalSeconds": 15,
		"DebugOverlay": false,
		"HideDebugOverlayWhenInterfaceNotDetected": false,
		"ScanIntervalMs": 100,
		"OverlayScale": null,
		"EnableImagePreprocessing": true,
		"BinarizationThreshold": 145,
		"EnableTextColorFiltering": true,
		"TextColorTargetR": 50,
		"TextColorTargetG": 42,
		"TextColorTargetB": 34,
		"TextColorTolerance": 47,
		"TextColorMaxLuminance": 145,
		"TextColorMaxChannelSpread": 29,
		"OcrEngineMode": 2,
		"BypassOcrCache": false,
		"TesseractDataPath": ""
	},
	"Update": {
		"AutoUpdate": true,
		"GithubToken": null
	},
	"Window": {
		"InitialSetupComplete": false,
		"CustomOffsetX": null,
		"CustomOffsetY": null,
		"CustomWidth": null,
		"CustomHeight": null
	}
}
```

### App Settings

| Key | Type | Default | Description |
|---|---|---|---|
| `LogLevel` | `"Trace"`, `"Debug"`, `"Information"`, `"Warning"`, `"Error"` | `"Information"` | How much detail to show in the log window. Changes take effect immediately. |
| `BringToForeground` | bool | `true` | Bring the app window to the front when launched or on update |
| `AlwaysOnTop` | bool | `false` | Keep the dashboard window above other windows at all times |
| `RememberDebugPanel` | bool | `false` | Remember whether the debug panel was open across restarts |
| `CloseWithPoE2` | bool | `false` | Automatically close the app when Path of Exile 2 is no longer running |
| `OpenWithPoE2` | bool | `false` | Launch the app automatically when PoE2 starts (uses a background watcher service) |
| `AllOverlaysDisabled` | bool | `false` | Disable all in-game overlays (pricing and banner) |
| `PricingOverlay` | bool | `true` | Show the side pricing overlay in-game |
| `Banner` | bool | `true` | Show the unpriceable-items banner (skill gems, supports) |

### Pricing Settings

| Key | Type | Default | Description |
|---|---|---|---|
| `PricingSource` | `"poe2scout"` or `"poe.ninja"` | `"poe2scout"` | Which pricing API to use |
| `League` | string | `"Runes of Aldur"` | League name for your pricing source |
| `AutoPriceThresholds` | bool | `true` | Automatically set color thresholds based on the highest-priced item in each scan |
| `RedThreshold` | decimal | `0.5` | Items at or below this value appear red |
| `OrangeThreshold` | decimal | `1.0` | Items at or below this value appear orange (must be > RedThreshold) |
| `GreenThreshold` | decimal | `5.0` | Items at or above this value appear green (must be > OrangeThreshold) |
| `DisplayCurrency` | `"exalt"` or `"chaos"` | `"exalt"` | Currency used for displayed values |

### OCR Settings

| Key | Type | Default | Description |
|---|---|---|---|
| `Language` | string | `"eng"` | OCR language (must match your game client). Available: `eng`, `deu`, `fra`, `spa`, `por`, `rus`, `jpn`, `kor`, `chi_tra` |
| `OcrBackend` | `"windows"` or `"tesseract"` | `"windows"` | OCR engine — Windows is faster and uses less CPU; Tesseract is a fallback for compatibility |
| `CaptureMode` | `"printwindow"` or `"desktop"` | `"printwindow"` | Screen capture method. `printwindow` is faster. `desktop` is compatible with Lossless Scaling WGC. |
| `SaveDebugImages` | bool | `false` | Save captured and processed OCR images to `debug-images/` |
| `DebugImageIntervalSeconds` | number (1–30) | `15` | How often to save debug images when enabled |
| `DebugOverlay` | bool | `false` | Show the capture-bounds overlay on screen |
| `HideDebugOverlayWhenInterfaceNotDetected` | bool | `false` | Hide the overlay when the league panel isn't detected |
| `ScanIntervalMs` | number (50–200) | `100` | Milliseconds between OCR scan cycles |
| `OverlayScale` | float or null | `null` | Scale factor for overlay text (e.g. `1.5` for 150% size). Set via the slider in Settings. |
| `EnableImagePreprocessing` | bool | `true` | Enable OCR image preprocessing pipeline (binarization + color filtering) |
| `BinarizationThreshold` | number (0–255) | `145` | Threshold for converting grayscale pixels to black or white |
| `EnableTextColorFiltering` | bool | `true` | Filter out pixels that don't match the in-game text color |
| `TextColorTargetR` | number (0–255) | `50` | Target red channel for in-game text color |
| `TextColorTargetG` | number (0–255) | `42` | Target green channel for in-game text color |
| `TextColorTargetB` | number (0–255) | `34` | Target blue channel for in-game text color |
| `TextColorTolerance` | number (0–255) | `47` | Max Euclidean distance from the target color for a pixel to be kept |
| `TextColorMaxLuminance` | number (0–255) | `145` | Maximum luminance for a pixel to be considered text (filters out bright artifacts) |
| `TextColorMaxChannelSpread` | number (0–255) | `29` | Maximum difference between the highest and lowest RGB channel (filters out colored UI elements) |
| `OcrEngineMode` | number (0–2) | `2` | Tesseract engine mode. Default (`2`) is LSTM-only. `1` is LSTM + legacy, `0` is legacy-only. |
| `BypassOcrCache` | bool | `false` | Skip cached OCR results and re-process every frame (debugging only) |
| `TesseractDataPath` | string | `""` (auto) | Path to Tesseract training data. Leave empty to use the bundled `ocr/tesseract/` directory. |

### Update Settings

| Key | Type | Default | Description |
|---|---|---|---|
| `AutoUpdate` | bool | `true` | Check for updates on startup and every 5 minutes |

### Window Settings

| Key | Type | Default | Description |
|---|---|---|---|
| `InitialSetupComplete` | bool | `false` | Whether the initial capture-region setup has been completed |
| `CustomOffsetX` | number or null | `null` | Override the auto-detected capture region X offset (pixels from game window top-left) |
| `CustomOffsetY` | number or null | `null` | Override the auto-detected capture region Y offset |
| `CustomWidth` | number or null | `null` | Override the auto-detected capture region width |
| `CustomHeight` | number or null | `null` | Override the auto-detected capture region height |

Set these to override the auto-detected capture region. `null` means auto-detect. Values are in pixels relative to the game window's top-left corner.

## What It Does

- Detects the PoE2 window and captures a profile-based OCR region.
- Reads item names using Windows OCR (or Tesseract as fallback).
- Translates non-English item names using community-maintained translation data.
- Parses quantity prefixes like `1x`, `3x`, and OCR-misread quantities like `Lx` or `ix`.
- Fetches market prices from community pricing APIs and caches them.
- Multiplies price by detected quantity before rendering.
- Displays a side overlay with value labels and threshold-based colors.
- Resolves range prices for unique item categories and uncut gems when exact prices aren't available.
- Applies tier fallbacks: GREATER/PERFECT orbs and runes fall back to their base item price.
- Matches OCR-smeared text with fuzzy correction against known pricing keys.

## How Pricing Works

1. The tool captures the OCR region and extracts text with the selected OCR engine.
2. OCR text is normalized, cleaned of quantity prefixes and artifacts, then mapped to pricing keys.
3. A pricing cache refreshes from the selected pricing source on a fixed interval (default: 15 minutes).
4. Lookup uses multiple candidates per item — normalized name, level-stripped, orb-suffix-stripped, and alias-expanded forms (e.g. `gcp` → `Gemcutter's Prism`).
5. If no exact price is found, the system tries tier fallbacks, unique category min/max ranges, and uncut gem family ranges.
6. As a last resort, fuzzy matching corrects common OCR substitution errors against known pricing keys.
7. Matched quotes are adjusted by detected quantity before rendering.
8. Overlay output is rendered next to the captured item rows with threshold-based color coding.

If an item is not recognized or not available in the current pricing data, it doesn't show a value.

## Pricing Sources

### poe2scout (default)
- **Website:** [poe2scout.com](https://poe2scout.com)
- **API:** `https://api.poe2scout.com/poe2`
- **Accuracy:** Averages prices from the last 24 hours for more stable values.
- Fetches currency, expedition, rune, verisium, uncut gem, and unique item prices.
- League data is fetched automatically — just select your league in Settings.

### poe.ninja
- **Website:** [poe.ninja](https://poe.ninja)
- **API:** `https://poe.ninja/poe2/api/economy/`
- **Accuracy:** Uses the latest listed price only (no 24-hour averaging available via the API).
- Supports currency, fragment, and item category pricing.
- League name must match the poe.ninja league slug (e.g. `Runes of Aldur`).

You can switch pricing sources anytime in Settings under **Pricing**.

## Credits

- **[Exiled Exchange 2](https://github.com/Kvan7/Exiled-Exchange-2)** — Item name translation data sourced from their curated game data files (`items.ndjson` generated from PoE2's `BaseItemTypes.json`).
- **AI** — While the vast majority of code in this project was written by a person, AI was used to:
 - Update documentation
 - Help fix bugs I couldn't find a solution for
 - Analyse the codebase and look for the areas in need of refactoring the most for readability.
 - Write all tests in the /tests/ project (this would have taken me weeks to do myself, with AI it only took a day or so)
 - Generate translation data files (`ocr/unique-category-map.json`) by extracting and mapping base-type keywords across all 7 supported languages — a task that would have required manual translation of hundreds of game terms
