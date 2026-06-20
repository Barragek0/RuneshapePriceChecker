using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

if (args.Length < 1)
{
    Console.WriteLine("Usage: UpdateTestServer <release-zip-path> [port] [version]");
    Console.WriteLine("  Serves a mock GitHub API + release zip download on the given port (default 8099).");
    Console.WriteLine("  version: the release tag to report (default reads from the exe in the zip and bumps patch).");
    return;
}

var zipPath = args[0];
var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 8099;

// Determine version: explicit arg, or bump patch from the exe in the zip
string releaseTag;
if (args.Length > 2)
{
    releaseTag = args[2].StartsWith("v", StringComparison.OrdinalIgnoreCase) ? args[2] : $"v{args[2]}";
}
else
{
    // Extract version from the exe in the zip
    var tempDir = Path.Combine(Path.GetTempPath(), $"rpc-version-{Guid.NewGuid():N}");
    _ = Directory.CreateDirectory(tempDir);
    try
    {
        ZipFile.ExtractToDirectory(zipPath, tempDir);
        var exePath = Directory.GetFiles(tempDir, "RuneshapePriceChecker.exe").FirstOrDefault();
        if (exePath is not null)
        {
            var ver = FileVersionInfo.GetVersionInfo(exePath);
            var v = ver.ProductVersion ?? "0.0.0";
            var parts = v.Split('.');
            if (parts.Length == 3 && int.TryParse(parts[2], out var patch))
            {
                releaseTag = $"v{parts[0]}.{parts[1]}.{patch + 1}";
            }
            else
            {
                releaseTag = $"v{v}";
            }
        }
        else
        {
            releaseTag = "v1.0.1";
        }
    }
    finally
    {
        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }
}

if (!File.Exists(zipPath))
{
    Console.WriteLine($"Zip not found: {zipPath}");
    return;
}

var zipBytes = File.ReadAllBytes(zipPath);
var zipName = Path.GetFileName(zipPath);

using var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{port}/");
listener.Start();
Console.WriteLine($"Update test server running on http://localhost:{port}/");
Console.WriteLine($"Serving: {zipPath}");
Console.WriteLine("Endpoints:");
Console.WriteLine("  GET /api/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10");
Console.WriteLine($"  GET /download/{zipName}");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    while (!cts.IsCancellationRequested)
    {
        var contextTask = listener.GetContextAsync();
        var completed = await Task.WhenAny(contextTask, Task.Delay(-1, cts.Token));
        if (completed != contextTask) break;

        var ctx = await contextTask;
        _ = Task.Run(() => HandleRequest(ctx, zipBytes, zipName, port));
    }
}
catch (OperationCanceledException) { }
finally
{
    listener.Stop();
}

void HandleRequest(HttpListenerContext ctx, byte[] zipBytes, string zipName, int port)
{
    var path = ctx.Request.Url!.AbsolutePath;
    Console.WriteLine($"{ctx.Request.HttpMethod} {path}");

    if (path.StartsWith("/download/", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.ContentType = "application/zip";
        ctx.Response.ContentLength64 = zipBytes.Length;
        ctx.Response.OutputStream.Write(zipBytes, 0, zipBytes.Length);
        ctx.Response.OutputStream.Close();
        return;
    }

    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/repos/", StringComparison.OrdinalIgnoreCase))
    {
        var mockRelease = new
        {
            tag_name = releaseTag,
            prerelease = false,
            html_url = $"http://localhost:{port}",
            body = "# 🎉 Runeshape Price Checker v1.0.0\n\n" +
                   "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat.\n\n" +
                   "---\n\n" +
                   "## 🚀 Major Features\n\n" +
                   "Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident.\n\n" +
                   "### Auto-Updater\n\n" +
                   "The app now checks for and applies updates automatically. Configure via `appsettings.json`:\n\n" +
                   "```json\n" +
                   "{\n" +
                   "  \"Update\": {\n" +
                   "    \"AutoUpdate\": true,\n" +
                   "    \"IgnorePrereleases\": false\n" +
                   "  }\n" +
                   "}\n" +
                   "```\n\n" +
                   "### Changelog Window\n\n" +
                   "- **Rich Markdown rendering** — supports *italic*, **bold**, ~~strikethrough~~, and `inline code`\n" +
                   "- **Spoiler blocks** for patch notes you want to hide\n" +
                   "- **Auto-display** after updates so you never miss what changed\n\n" +
                   "---\n\n" +
                   "## 🐛 Bug Fixes\n\n" +
                   "1. Fixed overlay click-through with `WS_EX_TRANSPARENT` — windows no longer eat clicks\n" +
                   "2. Fixed uncut gem pricing — *Support*, *Skill*, and *Spirit* gems now return proper ranges\n" +
                   "3. Fixed slot detection for missing categories: rings, amulets, belts, shields, foci, quivers, talismans\n" +
                   "4. Fixed Tesseract download blocking OCR on first launch\n" +
                   "5. Fixed config `reloadOnChange` not propagating to `IOptionsMonitor`\n\n" +
                   "## ⚡ Performance\n\n" +
                   "| Area | Before | After | Improvement |\n" +
                   "|------|--------|-------|-------------|\n" +
                   "| DWM capture | ~720 KB per read | ~2 KB anchor check | **360x less** |\n" +
                   "| Window hide | Always posts to UI | No-op when hidden | **0 overhead** |\n" +
                   "| Tesseract init | Blocks for 2+ min | Background download | **non-blocking** |\n\n" +
                   "## 📝 Upgrade Notes\n\n" +
                   "> **Important:** This is a major release. Your settings will be preserved.\n" +
                   "> If you encounter any issues, check the [GitHub repo](https://github.com/Barragek0/RuneshapePriceChecker) or use the Dashboard to report them.\n\n" +
                   "<details>\n" +
                   "<summary>📋 Full Patch Notes (click to expand)</summary>\n\n" +
                   "### Detailed Changes\n\n" +
                   "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Vivamus lacinia odio vitae vestibulum vestibulum. Cras venenatis euismod malesuada. Nullam ac erat ante. Suspendisse potenti.\n\n" +
                   "- **Pricing Pipeline:** Refactored `InMemoryPricingCache` to aggregate by base type across Poe2Scout categories\n" +
                   "- **OCR Engine:** Added live language switching via `OnChange` subscription in `OcrLeagueWindowReader`\n" +
                   "- **Dashboard:** New changelog rendering with `MarkdownRenderer` supporting full markdown syntax\n" +
                   "- **Updater:** `UpdateChecker` now uses `IOptionsMonitor` for `GitHubApiBaseUrl` instead of hardcoded URL\n\n" +
                   "```csharp\n" +
                   "// Example: new slot detection logic\n" +
                   "private static string? GetSlotFromBaseType(string? baseType)\n" +
                   "{\n" +
                   "    if (baseType is null) return null;\n" +
                   "    // Rings, amulets, belts, shields, foci, quivers, talismans\n" +
                   "    return baseType switch\n" +
                   "    {\n" +
                   "        \"Ring\" => \"ring\",\n" +
                   "        \"Amulet\" => \"amulet\",\n" +
                   "        _ => null\n" +
                   "    };\n" +
                   "}\n" +
                   "```\n\n" +
                   "> **Note:** This is mock data for testing the changelog rendering. Real release notes will accompany actual releases.\n\n" +
                   "</details>\n\n" +
                   "<details>\n" +
                   "<summary>🔧 Known Issues</summary>\n\n" +
                   "- ~~Pricing overlay may flicker on multi-monitor setups~~ *(fixed in v0.2.1)*\n" +
                   "- Tesseract requires `eng.traineddata` in the `tesseract` folder\n" +
                   "- League window must be in **foreground** for OCR to work\n\n" +
                   "</details>\n\n" +
                   "---\n\n" +
                   "**Full Changelog**: https://github.com/Barragek0/RuneshapePriceChecker/releases/tag/v1.0.0",
            assets = new[]
            {
                new { name = zipName, browser_download_url = $"http://localhost:{port}/download/{zipName}", size = (long)zipBytes.Length }
            }
        };

        // /releases/tags/ should return a single object; everything else returns an array
        var isSingle = path.Contains("/releases/tags/", StringComparison.OrdinalIgnoreCase);
        var json = isSingle
            ? JsonSerializer.Serialize(mockRelease, new JsonSerializerOptions { WriteIndented = true })
            : JsonSerializer.Serialize(new[] { mockRelease }, new JsonSerializerOptions { WriteIndented = true });
        var responseBytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = responseBytes.Length;
        ctx.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
        ctx.Response.OutputStream.Close();
        return;
    }

    ctx.Response.StatusCode = 404;
    ctx.Response.OutputStream.Close();
}
