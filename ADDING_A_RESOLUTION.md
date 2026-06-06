# Adding a New OCR Resolution Profile

This guide explains how to add support for a new resolution.

## 1) Prepare

1. Run Path of Exile 2 in borderless windowed mode.
2. In `config/appsettings.json`, set these to `true` while tuning:
	- `App:DebugLogging`
	- `OCR:DebugOverlay`
	- `OCR:SaveDebugImages`
3. Start the app. If your resolution isn't listed, an unsupported-resolution popup will appear with the detected key (e.g. `2560x1440`).

## 2) Add the New Profile

1. Open `src/OCR/OcrResolutionProfiles.cs`.
2. Copy the `1920x1080` line and paste it below.
3. Change the key, and remove `{ Confirmed = true }`.

Example for 2560x1440:

```csharp
["2560x1440"] = new(320, 210, 400, 730),
```

The four numbers are: `CaptureOffsetX, CaptureOffsetY, CaptureWidth, CaptureHeight`. The resolution is read from the key (e.g. `"2560x1440"`).

- `CaptureOffsetX` — How many pixels from the left edge of the game window to the left edge of the red OCR box.
- `CaptureOffsetY` — How many pixels from the top edge of the game window to the top edge of the red OCR box.
- `CaptureWidth` — How wide the red OCR box should be, in pixels.
- `CaptureHeight` — How tall the red OCR box should be, in pixels.

4. Save and restart the app. You'll see an "Untested Resolution" warning — this is normal and will appear every time until the profile is marked with `{ Confirmed = true }`, which you should only do after you have confirmed its fully working.

## 3) Tune the Red OCR Box

The red box must cover the item list area so the tool can read it correctly. The black background should render from the left side of the box, covering the runes, so we can render the parsed values from the red box.

Here is an example of how the box looks on 1080p:

![1080p OCR bounds example](https://i.vgy.me/ohQ5zW.png)

Its vital that you get the box to look as close to the image as possible, so that everything functions correctly: 
- The left line of the red box should line up with the start of the 'a' in the Runeshape Combinations text above it.
- The right line should line up with the right edge of each item in the list.
- The top line should line up with the top of the first entry in the list.
- The bottom line should line up with the small black line at the bottom of the list.

Use these rules, adjusting by small amounts each time, restarting, and checking again:

| If this is wrong | Change this value |
|---|---|
| Box too far left | Increase the first value `CaptureOffsetX` |
| Box too far right | Decrease the first value `CaptureOffsetX` |
| Box too high | Increase the second value `CaptureOffsetY` |
| Box too low | Decrease the second value `CaptureOffsetY` |
| Box too narrow | Increase the third value `CaptureWidth` |
| Box too wide | Decrease the third value `CaptureWidth` |
| Box too short | Increase the fourth value `CaptureHeight` |
| Box too tall | Decrease the fourth value `CaptureHeight` |

You can also check the `ocr-debug` folder for `raw.png`, `text-extract.png`, and `preprocessed.png` to see what the tool is reading.

## 4) Mark as Confirmed

Once the red box is positioned correctly and OCR is producing good results:

1. Open `src/OCR/OcrResolutionProfiles.cs`.
2. Add `{ Confirmed = true }` to your profile:

```csharp
["2560x1440"] = new(320, 210, 400, 730) { Confirmed = true },
```

3. The untested-resolution warning will stop appearing.

## 5) Submit It (Optional)

If your resolution is working well, consider submitting it so others can benefit. Open a pull request or issue with your tuned values for `OcrResolutionProfiles.cs`.

