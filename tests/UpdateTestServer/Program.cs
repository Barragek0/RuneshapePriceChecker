using System.Net;
using System.Text;
using System.Text.Json;

if (args.Length < 1)
{
    Console.WriteLine("Usage: UpdateTestServer <release-zip-path> [port]");
    Console.WriteLine("  Serves a mock GitHub API + release zip download on the given port (default 8099).");
    return;
}

var zipPath = args[0];
var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 8099;

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

static void HandleRequest(HttpListenerContext ctx, byte[] zipBytes, string zipName, int port)
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

    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
    {
        var mockRelease = new
        {
            tag_name = "v1.0.0",
            prerelease = false,
            html_url = $"http://localhost:{port}",
            body = "## What's New in v1.0.0\n\n### 🚀 Features\n\n- **New Changelog Window** — See what's new after each update\n- **Auto-update improvements** — More reliable update process\n- **Performance optimizations** — Reduced CPU and memory usage\n\n### 🐛 Bug Fixes\n\n- Fixed overlay click-through issue with `WS_EX_TRANSPARENT`\n- Fixed settings validation for threshold values\n- Fixed window position not saving during resize\n\n### 📝 Notes\n\n> This is a **major release** with significant changes.",
            assets = new[]
            {
                new { name = zipName, browser_download_url = $"http://localhost:{port}/download/{zipName}", size = (long)zipBytes.Length }
            }
        };

        var json = JsonSerializer.Serialize(new[] { mockRelease }, new JsonSerializerOptions { WriteIndented = true });
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
