using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace RuneshapePriceChecker.Startup;

internal static partial class SentryNativeMethods
{
    private const string LibraryName = "sentry";
    private static string? _libraryPath;

    static SentryNativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(SentryNativeMethods).Assembly, (name, _, _) =>
        {
            if (!string.Equals(name, LibraryName, StringComparison.OrdinalIgnoreCase) || _libraryPath is null)
                return IntPtr.Zero;
            return NativeLibrary.Load(_libraryPath);
        });
    }

    internal static void SetLibraryPath(string path) => _libraryPath = path;

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr sentry_options_new();

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_free(IntPtr options);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_dsn(IntPtr options, IntPtr dsn);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_release(IntPtr options, IntPtr release);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_environment(IntPtr options, IntPtr environment);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_database_pathw(IntPtr options, string path);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_handler_pathw(IntPtr options, string path);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_auto_session_tracking(IntPtr options, int value);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_sample_rate(IntPtr options, double value);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_cache_keep(IntPtr options, int mode);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_cache_max_items(IntPtr options, nuint items);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_cache_max_size(IntPtr options, nuint bytes);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_cache_max_age(IntPtr options, long seconds);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_http_retry(IntPtr options, int enabled);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_shutdown_timeout(IntPtr options, ulong milliseconds);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_transfer_timeout(IntPtr options, ulong milliseconds);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_enable_logs(IntPtr options, int enabled);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_options_set_enable_metrics(IntPtr options, int enabled);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int sentry_init(IntPtr options);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr sentry_attach_filew(string path);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sentry_set_tag(IntPtr key, IntPtr value);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int sentry_close();
}

internal sealed record SentryCrashReporterOptions(
    bool Enabled,
    string Dsn,
    string Release,
    string Environment,
    string DatabaseDirectory,
    string HandlerPath,
    string CurrentLogPath,
    string PreviousLogPath,
    string ManagedCrashPath,
    string SystemContextPath);

internal sealed class SentryCrashReporter : IDisposable
{
    private const int CacheKeepOffline = 1;
    private bool Initialized { get; }
    private bool _closed;

    private SentryCrashReporter(bool initialized)
    {
        Initialized = initialized;
    }

    internal bool IsEnabled => Initialized;

    internal static SentryCrashReporter Start(SentryCrashReporterOptions options)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Dsn))
        {
            TryDeleteDirectory(options.DatabaseDirectory);
            return new(false);
        }

        try
        {
            Directory.CreateDirectory(options.DatabaseDirectory);
            var assembly = typeof(SentryCrashReporter).Assembly;
            var (sentryPath, handlerPath) = ExtractRuntimeFiles(assembly, SentryPaths.RootDirectory);
            SentryNativeMethods.SetLibraryPath(sentryPath);
            var nativeOptions = SentryNativeMethods.sentry_options_new();
            if (nativeOptions == IntPtr.Zero)
                throw new InvalidOperationException("Sentry options could not be created.");

            SetUtf8(nativeOptions, options.Dsn, SentryNativeMethods.sentry_options_set_dsn);
            SetUtf8(nativeOptions, options.Release, SentryNativeMethods.sentry_options_set_release);
            SetUtf8(nativeOptions, options.Environment, SentryNativeMethods.sentry_options_set_environment);
            SentryNativeMethods.sentry_options_set_database_pathw(nativeOptions, options.DatabaseDirectory);
            SentryNativeMethods.sentry_options_set_handler_pathw(nativeOptions, handlerPath);
            SentryNativeMethods.sentry_options_set_auto_session_tracking(nativeOptions, 0);
            SentryNativeMethods.sentry_options_set_sample_rate(nativeOptions, 1.0);
            SentryNativeMethods.sentry_options_set_cache_keep(nativeOptions, CacheKeepOffline);
            SentryNativeMethods.sentry_options_set_cache_max_items(nativeOptions, 3);
            SentryNativeMethods.sentry_options_set_cache_max_size(nativeOptions, 64u * 1024u * 1024u);
            SentryNativeMethods.sentry_options_set_cache_max_age(nativeOptions, 30L * 24 * 60 * 60);
            SentryNativeMethods.sentry_options_set_http_retry(nativeOptions, 1);
            SentryNativeMethods.sentry_options_set_shutdown_timeout(nativeOptions, 2000);
            SentryNativeMethods.sentry_options_set_transfer_timeout(nativeOptions, 10000);
            SentryNativeMethods.sentry_options_set_enable_logs(nativeOptions, 0);
            SentryNativeMethods.sentry_options_set_enable_metrics(nativeOptions, 0);

            if (SentryNativeMethods.sentry_init(nativeOptions) != 0)
                throw new InvalidOperationException("Sentry initialization failed.");

            Attach(options.CurrentLogPath);
            Attach(options.PreviousLogPath);
            Attach(options.ManagedCrashPath);
            Attach(options.SystemContextPath);
            return new(true);
        }
        catch (Exception ex)
        {
            CrashLogger.WriteCaught("Automatic crash reporting initialization failed", ex);
            TryDeleteDirectory(options.DatabaseDirectory);
            return new(false);
        }
    }

    internal void SetTag(string key, string value)
    {
        if (!Initialized) return;
        SetUtf8(key, value, SentryNativeMethods.sentry_set_tag);
    }

    public void Dispose()
    {
        if (!Initialized || _closed) return;
        _closed = true;
        _ = SentryNativeMethods.sentry_close();
    }

    private static void Attach(string path)
    {
        _ = SentryNativeMethods.sentry_attach_filew(path);
    }

    private static void SetUtf8(IntPtr options, string value, Action<IntPtr, IntPtr> setter)
    {
        var ptr = Marshal.StringToCoTaskMemUTF8(value);
        try { setter(options, ptr); } finally { Marshal.FreeCoTaskMem(ptr); }
    }

    private static void SetUtf8(string key, string value, Action<IntPtr, IntPtr> setter)
    {
        var keyPtr = Marshal.StringToCoTaskMemUTF8(key);
        var valuePtr = Marshal.StringToCoTaskMemUTF8(value);
        try { setter(keyPtr, valuePtr); }
        finally
        {
            Marshal.FreeCoTaskMem(keyPtr);
            Marshal.FreeCoTaskMem(valuePtr);
        }
    }

    private static (string SentryPath, string HandlerPath) ExtractRuntimeFiles(Assembly assembly, string destination)
    {
        Directory.CreateDirectory(destination);
        var files = new[] { "sentry.dll", "crashpad_handler.exe", "crashpad_wer.dll" };
        var manifestName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith("SentryNative.runtime.win_x64.manifest.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Embedded Sentry runtime manifest is missing.");
        using var manifestStream = assembly.GetManifestResourceStream(manifestName)
            ?? throw new FileNotFoundException("Embedded Sentry runtime manifest cannot be opened.");
        using var manifest = JsonDocument.Parse(manifestStream);
        var expectedHashes = manifest.RootElement.EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("name").GetString()!,
                item => item.GetProperty("sha256").GetString()!,
                StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var resource = assembly.GetManifestResourceNames()
                .SingleOrDefault(name => name.EndsWith(file, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException($"Embedded Sentry runtime is missing: {file}");
            if (!expectedHashes.TryGetValue(file, out var expectedHash))
                throw new InvalidDataException($"Embedded Sentry runtime manifest is missing: {file}");
            var target = Path.Combine(destination, file);
            if (!File.Exists(target) || !string.Equals(ComputeSha256(target), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using var input = assembly.GetManifestResourceStream(resource)
                        ?? throw new FileNotFoundException($"Embedded Sentry runtime cannot be opened: {file}");
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output);
                        output.Flush(true);
                    }
                    if (!string.Equals(ComputeSha256(temp), expectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Embedded Sentry runtime hash mismatch: {file}");
                    File.Move(temp, target, true);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
            paths[file] = target;
        }
        foreach (var file in files)
            if (!string.Equals(ComputeSha256(paths[file]), expectedHashes[file], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Extracted Sentry runtime hash mismatch: {file}");
        return (paths["sentry.dll"], paths["crashpad_handler.exe"]);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

}
