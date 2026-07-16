// Best-effort mutex that serializes background service work to reduce concurrent
// CLR metadata access (mitigates the MetaDataGetDispenser race in coreclr.dll).

namespace RuneshapePriceChecker.Configuration;

public static class MetadataGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const int TimeoutMs = 500;

    public static bool TryEnter()
    {
        if (!_enabled)
            return true;

        try { return Gate.Wait(TimeoutMs); }
        catch (ObjectDisposedException) { return false; }
    }

    public static void Exit()
    {
        if (!_enabled)
            return;

        try { _ = Gate.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    // Not configurable at runtime — read once at startup to avoid IOptionsMonitor lookups.
    internal static bool _enabled;

    public static void Initialize(bool enabled) => _enabled = enabled;
}
