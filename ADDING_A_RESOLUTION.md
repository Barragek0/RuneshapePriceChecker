# Adding a New OCR Resolution Profile

This guide explains how to add support for a new Path of Exile 2 client resolution.

## Before You Start

1. Run PoE2 in borderless windowed mode.
2. Set `App:EnableDebugLogging` and `OCR:ShowCaptureBoundsOverlay` to `true` in `src/appsettings.json` while tuning.

## Step-by-Step

1. Find your PoE2 client resolution.
2. Run the app and wait for the unsupported-resolution popup.
3. Note the exact resolution key shown in the popup (example: `2560x1440`).
4. Open `src/OCR/OcrResolutionProfiles.cs`.
5. Duplicate the `1920x1080` profile entry.
6. Replace the key and first two numbers with your resolution.
7. Keep the existing capture and row values first, then fine-tune:

```csharp
["2560x1440"] = new(2560, 1440, 255, 160, 285, 537, 23, 24, 8, 2, 2)
```
8. Restart the app.
9. Check the red overlay box and row lines.
10. If the box is too far left, increase `CaptureOffsetX`.
11. If the box is too far right, decrease `CaptureOffsetX`.
12. If the box is too high, increase `CaptureOffsetY`.
13. If the box is too low, decrease `CaptureOffsetY`.
14. If the box is too narrow, increase `CaptureWidth`.
15. If the box is too wide, decrease `CaptureWidth`.
16. If the box is too short, increase `CaptureHeight`.
17. If the box is too tall, decrease `CaptureHeight`.
18. Adjust base row tuning:
19. Increase `RowTextHeight` if each row's text area is too short.
20. Decrease `RowTextHeight` if each row overlaps into the next row.
21. Increase `RowGapHeight` if row spacing is too tight.
22. Decrease `RowGapHeight` if row spacing is too large.
23. Adjust late-row correction only if lower rows drift/crop:
24. Set `RowLateOffsetStartRow` to the first row number where drift starts.
25. Set `RowLateOffsetStepRows` to how many rows share the same extra offset block.
26. Set `RowLateOffsetStepPx` to pixels added per block.
27. Repeat small edits until OCR logs stable item names and overlay lines align.

### Late-Row Offset Rule

For row number `r`:

- If `r < RowLateOffsetStartRow`, extra pixel offset is `0`.
- If `r >= RowLateOffsetStartRow`, extra pixel offset is:

`(((r - RowLateOffsetStartRow) / RowLateOffsetStepRows) + 1) * RowLateOffsetStepPx`

Example using `8, 2, 2`:

- Row 7: `+0`
- Row 8: `+2`
- Row 9: `+2`
- Row 10: `+4`
- Row 11: `+4`

## Quick Defaults

1. Start with `RowTextHeight=23` and `RowGapHeight=24`.
2. Start late-row correction disabled with `RowLateOffsetStepPx=0`.
3. Enable late-row correction only if lower rows are cut off.
4. Change by 1-2 pixels at a time.
5. Restart after each edit and re-check overlay/OCR logs.

## Validation Checklist

1. No unsupported-resolution popup appears for your resolution.
2. Red capture box fully covers the in-game item list text area.
3. Red row lines align with each visible item row. Areas with black lines indicate areas that are not scanned.
4. Lower rows (where drift usually happens) still align and are not cropped.
5. Debug logs show consistent OCR item detection.
6. Overlay prices align next to the same rows.

Here is an example of how the box and prices should look:
![1080p OCR bounds example](https://i.vgy.me/3hhWcW.png)
