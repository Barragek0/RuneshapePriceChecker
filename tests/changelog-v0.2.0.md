### 🚀 Performance

The OCR engine has been completely rewritten to run directly inside the tool, allowing much more frequent screen scanning.

#### Upsides
  * The price overlay now updates much more frequently, and will appear much sooner when opening the interface.
  * The tool no longer needs to install `Tesseract` to function — you can safely uninstall it from your system without affecting the tool.

#### Downsides
  * The `.exe` file will be around ~40MB from now on, as it has `Tesseract` text training data and additional `DLL`s bundled with it.
  * With `DebugLogging` enabled, the console will be flooded with data a lot more than before. I'll be adding a dedicated app to the tool with a log window sometime before `1.0.0`, so this is just a temporary problem.

<details>
<summary>Technical details</summary>

#### What changed

In `0.1.3`, every OCR scan launched a separate `tesseract.exe` child process, wrote the screenshot to a temp file on disk, waited for the external engine to read it and produce output, then parsed the result from another temp file. This cost 500–2000ms per scan, with most of the time spent in process creation and disk I/O rather than actual text recognition.

In `0.2.0`, `Tesseract` runs directly inside the app via native `P/Invoke`. Screenshots stay in memory as `PNG` byte arrays and are fed straight to the recognition engine. The overlay now renders the moment a scan finishes, instead of waiting for the next fixed loop tick.

#### OS process metrics (`0.1.3` → `0.2.0`)

| Metric | Old (`0.1.3`) | New (`0.2.0`) | Change |
|---|---|---|---|
| CPU avg | 0.06% | 0.22% | still negligible |
| CPU max | 0.38% | 0.94% | still under 1% |
| Memory avg | 184 MB | 177 MB | **-7 MB (-4%)** |
| Memory max | 189 MB | 177 MB | **-12 MB (-6%)** |
| Handles | 776 | 793 | ≈ same |
| Threads | 38 | 43 | +5 (healthy) |

The script used to get these values was `monitor-performance.ps1` inside of the `scripts` folder.

#### .NET runtime metrics (`0.1.3` → `0.2.0`)

| Metric | Old (`0.1.3`) | New (`0.2.0`) | Change |
|---|---|---|---|
| Alloc rate | 26.3 KB/s | 26.3 KB/s | same |
| Working set (avg) | 190.7 MB | 184.5 MB | **-6.2 MB (-3%)** |
| Working set (max) | 198.2 MB | 186.1 MB | **-12.1 MB (-6%)** |
| GC pause time | 0.00 s/s | 0.00 s/s | same |
| Lock contentions | 0/s | 0/s | same |

The script used to get these values was `monitor-performance.ps1` inside of the `scripts` folder.

#### Per-scan improvement (`0.1.3` → `0.2.0`) 

These are estimations.

| Step | Old (`tesseract.exe`) | New (`P/Invoke`) | Speedup |
|---|---|---|---|
| Process launch | 500–2000ms | 0ms (in-process) | ∞ |
| Disk I/O (write + read) | 7–20ms | 0ms (memory) | ∞ |
| `Tesseract` `CLI` overhead | ~50ms | 0ms (engine reused) | ∞ |
| Recognition (`LSTM`) | 50–200ms | 5–20ms | **~10×** |
| **Total per scan** | **~500–2000ms** | **~5–20ms** | **100×–400×** |

#### Why CPU went up (but doesn't matter)

CPU rose from 0.06% to 0.22% because the app now performs ~8–10 scans per second instead of ~2. The old code spent most of its time waiting for `tesseract.exe` to launch and for disk I/O to complete — neither of which consumes the app's CPU time. Figures are still trivial: the tool still uses less than 1% of a single core on a `9800X3D`, and it updates ~5 times more frequently.

#### Why memory went down

No more child process. The old `tesseract.exe` brought its own heap, loaded its own copy of the model, and allocated its own image buffers — all outside the main process but still counting toward the system-wide footprint. The in-process engine shares the app's existing heap and reuses the same model instance across scans.

</details>

### ⚠️ Warnings / Errors
- A one-time popup now warns you if `PoE 2` is in exclusive fullscreen mode, which blocks screen capture entirely.
- A one-time popup now warns you if your in-game `UI Brightness` is too low, which can cause wrong item matches and OCR failures.
- Resolution changes during startup of the game are now handled silently — the tool waits for the window to stabilize instead of incorrectly sending an error when launching the game on a supported resolution.
- `N/A` is now shown in red next to items that couldn't be matched, instead of grey. 
- A warning message now appears at the top of the interface, indicating when new Skills or Supports have been read by OCR, indicating that pricing for them isn't currently supported on poe.ninja.
 


### ✅ Testing
* An automated test suite now runs pricing and OCR checks against mock data. The release build runs these automatically before packaging which will help prevent bugs from being included in future releases.
* A `Pricing Simulator` and `Resolution Visualizer` are included in the `tests` folder — the simulator drives the automated checks, and the visualizer helps when adding new resolutions.


### 🐛 Bug Fixes / General Improvements
- Unique item price ranges were broken in the last version. `GGG`'s latest patch changed the description indicator beneath unique items to a solid line, which the OCR already recognized as an underline — so range pricing started working again without any changes needed.
- Zip extraction in the auto-updater handles file conflicts more reliably, old updater binaries are cleaned up before the new one is written.
- `Gem` is sometimes misread by OCR as `Cem` — the tool now corrects this automatically, so uncut gems price correctly instead of showing `N/A`.
- 1440p resolution has been corrected and confirmed as working. Thanks again to the user who helped on discord! 


**Full Changelog**: https://github.com/Barragek0/RuneshapePriceChecker/compare/0.1.3...0.2.0
