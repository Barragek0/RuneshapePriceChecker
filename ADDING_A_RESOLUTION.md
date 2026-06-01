# Adding a New OCR Resolution Profile

This guide explains how to add support for a new Path of Exile 2 client resolution.

## Before You Start

1. Run PoE2 in borderless windowed mode.
2. Keep `App:EnableDebugLogging` set to `true` in `src/appsettings.json` while tuning.
3. In `src/OCR/OcrOptions.cs`, set `ShowCaptureBoundsOverlay` to `true` while tuning.

## Step-by-Step

1. Find your PoE2 client resolution.
2. Run the app and wait for the unsupported-resolution popup.
3. Note the exact resolution key shown in the popup (example: `2560x1440`).
4. Open `src/OCR/OcrResolutionProfiles.cs`.
5. Duplicate the `1920x1080` profile entry.
6. Replace the key and first two numbers with your resolution.
7. Keep the existing capture and row values first, then fine-tune:

```csharp
["2560x1440"] = new(2560, 1440, 255, 160, 285, 537, 23, 24)
```

8. Restart the app.
9. Check the red overlay box and row lines:
10. If the box is too far left, increase `CaptureOffsetX`.
11. If the box is too far right, decrease `CaptureOffsetX`.
12. If the box is too high, increase `CaptureOffsetY`.
13. If the box is too low, decrease `CaptureOffsetY`.
14. If the box is too narrow, increase `CaptureWidth`.
15. If the box is too wide, decrease `CaptureWidth`.
16. If the box is too short, increase `CaptureHeight`.
17. If the box is too tall, decrease `CaptureHeight`.
18. Adjust row tuning for your resolution:
19. Increase `RowTextHeight` if each row's text area is too short.
20. Decrease `RowTextHeight` if each row overlaps into the next row.
21. Increase `RowGapHeight` if row spacing is too tight.
22. Decrease `RowGapHeight` if row spacing is too large.
23. Repeat small edits until OCR logs stable item names and overlay lines align.

## Quick Defaults

1. Start with `RowTextHeight=23` and `RowGapHeight=24`.
2. Change by 1-2 pixels at a time.
3. Restart after each edit and re-check overlay/OCR logs.

## Validation Checklist

1. No unsupported-resolution popup appears for your resolution.
2. Red capture box fully covers the in-game item list text area.
3. Red row lines align with each visible item row. Areas with black lines indicate areas that are not scanned.
4. Debug logs show consistent OCR item detection.
5. Overlay prices align next to the same rows.

Here is an example of how the box and prices should look:
![1080p OCR bounds example](https://i.vgy.me/3hhWcW.png)
