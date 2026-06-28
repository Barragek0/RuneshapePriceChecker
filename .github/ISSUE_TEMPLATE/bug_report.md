---
name: Bug report
about: Create a report to help us improve the tool
title: "[BUG] Describe the issue here"
labels: bug
assignees: Barragek0

---

**Describe the bug**
A clear and concise description of what the bug is.

**Additional data**
The easiest way to provide diagnostic data is using the built-in **bug report tool**:
- Click the 🐛 icon in the app near the top right corner.
- Follow the prompts to reproduce the issue.
- The tool automatically collects logs, crash reports, and debug images into a `.zip` file and opens a pre-filled GitHub issue for you.

If the bug report tool isn't working for you, please follow these manual steps:
- Go to settings in the app
- Change `Log Level` to `Trace`.
- Enable `Show debug overlay` and `Save debug images`.
- Go back to Path of Exile with the rune window open, then screenshot the full overlay and provide the image below.
- Go to the `logs` folder and find the most recent `*-log.txt`, `*-crash.txt` and `*-caught.txt` files, drag and drop them here.
- Go to the `ocr/your-ocr-backend-setting-here/images` folder, copy the images that are in there and paste them below.

Its important that you include this data, otherwise it's unlikely we'll be able to find the cause of the issue and fix it, so please include it if you can.