### ✨ What's New

- **Dashboard** — A new window for the tool that replaces the old console window. You can now see logs, change settings, and view update notes all in one place. The Dashboard opens automatically when you start the app.
- **Settings Panel** — Change your pricing thresholds, preferred currency, OCR language, overlay options, and more through the Dashboard instead of editing config files by hand.
- **First-Run Setup** — When you use the tool for the first time, it now guides you through positioning the overlay correctly. No more guessing where to place things — just follow the on-screen instructions.
- **Changelog After Updates** — When a new version is installed, a changelog window appears in the Dashboard showing what changed. No more checking GitHub to see what's new.
- **Better Prices** — The tool now uses Poe2Scout as its main pricing source, with PoeNinja as a backup. Poe2Scout prices are averaged over 24 hours, giving more stable values. PoeNinja only provides the latest prices, which can fluctuate wildly.
- **Language Switching** — Changing the OCR language now takes effect right away without restarting the app.

### 🚀 Performance

- **Lower Idle CPU Usage** — The tool now checks just a tiny portion of the screen to see if the league panel is open, instead of capturing the whole area every time.

<details>
<summary>📊 Technical Details</summary>

Performance was measured by running both versions idle for 10 seconds on the same machine:

| Metric | v0.2.2 | v1.0.0 | Change |
|--------|--------|--------|--------|
| CPU (average) | 0.51% | 0.40% | **22% lower** |
| CPU (peak) | 1.16% | 0.68% | **41% lower** |
| Memory (average) | 92 MB | 182 MB | +90 MB |
| Memory (peak) | 96 MB | 183 MB | — |
| DWM CPU | 0% | 0% | No change |

The CPU reduction comes from the anchor-based screen check — instead of capturing ~720KB of screen data every time it looks for the league panel, it now only reads ~2KB. The memory increase is expected: the Dashboard is a full WPF window replacing the old console window, which adds framework overhead but provides a much better user experience.

</details>

### 🐛 Bug Fixes

- Several item types that were missing prices now show them correctly.

### ⚠️ Notes

- If the tool's language data file (`eng.traineddata`) gets corrupted, it now detects this and repairs itself automatically.
- The updater now handles errors more gracefully instead of silently failing.

### ✅ Testing

The test suite has been fully rebuilt with 566 automated tests covering all major features — pricing, OCR, settings, updates, and the new Dashboard. These tests run on every release build, which means fewer bugs make it into releases.

Full Changelog: [0.2.2...1.0.0](https://github.com/Barragek0/RuneshapePriceChecker/compare/0.2.2...1.0.0)
