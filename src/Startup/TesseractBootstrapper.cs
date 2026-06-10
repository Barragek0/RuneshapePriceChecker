using System.Reflection;
using System.Runtime.InteropServices;

namespace RuneshapePriceChecker.Startup;

internal static class TesseractBootstrapper
{
    private const string Category = "Bootstrap.Tesseract[0]";
    private const string TessDataBestBaseUrl = "https://github.com/tesseract-ocr/tessdata_best/raw/main/";
    private const string TessDataSubDir = "tesseract";
    private const string RuntimesSubDir = "runtimes";

    static TesseractBootstrapper()
    {
        ExtractEmbeddedEngTrainedData();
        ExtractEmbeddedNativeDlls();
        ConfigureNativeDllResolution();
    }

    public static string ResolveTessDataPath()
    {
        var localPath = Path.Combine(AppContext.BaseDirectory, TessDataSubDir);
        if (Directory.Exists(localPath))
            return localPath;

        return string.Empty;
    }

    public static async Task EnsureLanguageDataAvailableAsync(string language, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(language))
            return;

        var tessDataDir = Path.Combine(AppContext.BaseDirectory, TessDataSubDir);
        var targetFile = Path.Combine(tessDataDir, $"{language}.traineddata");

        if (File.Exists(targetFile))
            return;

        if (string.Equals(language, "eng", StringComparison.OrdinalIgnoreCase))
            return; // Already extracted in static constructor

        await DownloadLanguageDataAsync(language, targetFile, cancellationToken).ConfigureAwait(false);
    }

    public static bool IsLanguageDataAvailable(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return false;

        var tessDataDir = Path.Combine(AppContext.BaseDirectory, TessDataSubDir);
        var targetFile = Path.Combine(tessDataDir, $"{language}.traineddata");
        return File.Exists(targetFile);
    }

    private static void ExtractEmbeddedEngTrainedData()
    {
        try
        {
            var tessDataDir = Path.Combine(AppContext.BaseDirectory, TessDataSubDir);
            var targetFile = Path.Combine(tessDataDir, "eng.traineddata");

            if (File.Exists(targetFile))
                return;

            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"{assembly.GetName().Name}.tesseract.eng.traineddata";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                LogInfo("English traineddata not embedded; will download on first use if needed.");
                return;
            }

            Directory.CreateDirectory(tessDataDir);
            using var fileStream = File.Create(targetFile);
            stream.CopyTo(fileStream);

            LogInfo($"English traineddata extracted to '{tessDataDir}'.");
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to extract embedded English traineddata: {ex.Message}");
        }
    }

    private static void ExtractEmbeddedNativeDlls()
    {
        var runtimesDir = Path.Combine(AppContext.BaseDirectory, RuntimesSubDir);
        Directory.CreateDirectory(runtimesDir);
        ExtractEmbeddedFile("tesseract50.dll", runtimesDir);
        ExtractEmbeddedFile("leptonica-1.82.0.dll", runtimesDir);
    }

    private static void ExtractEmbeddedFile(string fileName, string targetDir)
    {
        try
        {
            var targetPath = Path.Combine(targetDir, fileName);
            if (File.Exists(targetPath))
                return;

            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"{assembly.GetName().Name}.native.{fileName}";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                return;

            using var fileStream = File.Create(targetPath);
            stream.CopyTo(fileStream);
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to extract embedded file '{fileName}': {ex.Message}");
        }
    }

    private static void ConfigureNativeDllResolution()
    {
        try
        {
            var runtimesDir = Path.Combine(AppContext.BaseDirectory, RuntimesSubDir);
            if (!Directory.Exists(runtimesDir))
                return;

            NativeLibrary.SetDllImportResolver(
                typeof(OCR.NativeTesseractEngine).Assembly,
                (libraryName, assembly, searchPath) =>
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                        !libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        libraryName += ".dll";
                    }

                    var candidate = Path.Combine(runtimesDir, libraryName);
                    if (File.Exists(candidate))
                        return NativeLibrary.Load(candidate);

                    return IntPtr.Zero;
                });
        }
        catch
        {
            // Best-effort; failures surface as DllNotFoundException later.
        }
    }

    private static async Task DownloadLanguageDataAsync(string language, string targetFile, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{TessDataBestBaseUrl}{language}.traineddata";
            LogInfo($"Downloading {language}.traineddata...");

            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogWarning($"Failed to download {language}.traineddata (HTTP {(int)response.StatusCode}).");
                return;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > 0)
                LogInfo($"{language}.traineddata size: {contentLength / 1024 / 1024} MB");

            await using var fileStream = File.Create(targetFile);
            await response.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);

            LogInfo($"{language}.traineddata downloaded successfully.");
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(targetFile);
            LogWarning($"{language}.traineddata download cancelled.");
        }
        catch (Exception ex)
        {
            TryDeleteFile(targetFile);
            LogWarning($"Failed to download {language}.traineddata: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static void LogInfo(string message)
    {
        Console.WriteLine($"info: {Category} {message}");
    }

    private static void LogWarning(string message)
    {
        Console.WriteLine($"warn: {Category} {message}");
    }
}
