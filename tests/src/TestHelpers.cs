using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Contracts;

namespace RuneshapePriceChecker.Tests;

internal sealed class MockPricingSource(PricingSnapshot snapshot) : IPricingSource
{
    public Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task<PricingSnapshot> FetchPricesAsync(string league, CancellationToken ct)
        => Task.FromResult(snapshot);
}

internal sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T> where T : class
{
    public T CurrentValue => currentValue;
    public T Get(string? name) => currentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
