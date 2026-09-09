namespace RuneshapePriceChecker.Startup;

internal static class SentryPaths
{
    internal static string RootDirectory => Path.Combine(AppContext.BaseDirectory, "sentry");
    internal static string DatabaseDirectory => Path.Combine(RootDirectory, "database");
    internal static string HandlerPath => Path.Combine(RootDirectory, "crashpad_handler.exe");
    internal static string ContextPath => Path.Combine(RootDirectory, "crash-context.json");
}
