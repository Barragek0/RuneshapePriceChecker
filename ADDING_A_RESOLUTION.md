# Adding a New OCR Resolution Profile

This guide explains how to add support for a new resolution.

## 1) Prepare

1. Run Path of Exile 2 in borderless windowed mode.
2. In `src/appsettings.json`, set these to `true` while tuning:
	- `App:EnableDebugLogging`
	- `OCR:ShowCaptureBoundsOverlay`
3. Start the app once and note the unsupported resolution key from the popup (example: `2560x1440`).

## 2) Add the New Profile

1. Open `src/OCR/OcrResolutionProfiles.cs`.
2. Copy the `1920x1080` line and paste it below.
3. Change only the key and first two numbers to your resolution.

Example:

```csharp
["2560x1440"] = new(2560, 1440, 255, 160, 285, 537, 23, 24, 8, 2, 2, 26, 35, 20)
```

4. Save and restart the app.

## 3) Tune the Red OCR Box First

The red box must cover the item list area before row tuning will work.

Here is an example of how the box looks on 1080p, its vital that you match it to look as close to this as you can, so that the tool functions correctly on your resolution:

![1080p OCR bounds example](https://i.vgy.me/3hhWcW.png)


Use these rules:

| If this is wrong | Change this value |
|---|---|
| Box too far left | Increase `CaptureOffsetX` |
| Box too far right | Decrease `CaptureOffsetX` |
| Box too high | Increase `CaptureOffsetY` |
| Box too low | Decrease `CaptureOffsetY` |
| Box too narrow | Increase `CaptureWidth` |
| Box too wide | Decrease `CaptureWidth` |
| Box too short | Increase `CaptureHeight` |
| Box too tall | Decrease `CaptureHeight` |

Adjust by small amounts (1 to 2 px), restart, and check again.

## 4) Tune Row Height and Spacing

After the red box is correct, tune row geometry:

| Problem | Change |
|---|---|
| Text area is too short | Increase `RowTextHeight` |
| Rows overlap into each other | Decrease `RowTextHeight` |
| Rows are packed too tightly | Increase `RowGapHeight` |
| Rows are too far apart | Decrease `RowGapHeight` |

## 5) Tune Late-Row Drift (Only If Needed)

Use this only when lower rows drift or get cut off.

- `RowLateOffsetStartRow`: first row where drift starts.
- `RowLateOffsetStepRows`: how many rows share the same extra offset block.
- `RowLateOffsetStepPx`: pixels added per block.

Rule:

- If row `< RowLateOffsetStartRow`, extra offset is `0`.
- Otherwise:

`(((row - RowLateOffsetStartRow) / RowLateOffsetStepRows) + 1) * RowLateOffsetStepPx`

Example with `8, 2, 2`:

- Row 7: `+0`
- Row 8: `+2`
- Row 9: `+2`
- Row 10: `+4`
- Row 11: `+4`

## 6) Tune Adaptive Push-Down (Only If Needed)

When the runeshape page shows more than 5 runes on the left of the list for an item,
the game pushes the item text down to a second line. Adaptive shifting detects
this and bumps subsequent OCR rows down so they stay aligned with the text.

- `AdaptiveShiftProbeWidthPx`: width of the left probe strip that checks for text.
- `AdaptiveShiftStepPx`: pixels to shift rows down per detected overflow.
- `AdaptiveShiftProbeMinDarkPixels`: minimum dark-text-pixels required to treat a
  row as having content.

How it works:

1. A narrow probe strip at the left edge of each row is checked for dark text pixels.
2. If enough text-colored pixels are found, a prefix OCR check runs to determine if
   the text is a quantity prefix (like \"1x\") or a rune name.
3. Rows with quantity prefixes are left in place (they're already at the correct
   vertical position). Rows without prefixes get shifted down by
   `AdaptiveShiftStepPx`, and the shift cascades to rows below.
4. Overlay and OCR use the same row positioning based on the computed shifts.

Current 1080p reference values:

- `AdaptiveShiftProbeWidthPx = 26`
- `AdaptiveShiftStepPx = 35`
- `AdaptiveShiftMaxPx = 160`
- `AdaptiveShiftProbeMinDarkPixels = 20`

## 7) Final Checklist

1. No unsupported-resolution popup appears for your resolution.
2. Red capture box fully covers the in-game item list text area.
3. Red row lines match visible item rows.
4. Lower rows stay aligned and are not cropped.
5. OCR logs show stable item names.
6. Overlay prices align with the same rows.

