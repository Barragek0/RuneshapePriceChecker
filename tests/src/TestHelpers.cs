using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Contracts;

namespace RuneshapePriceChecker.Tests;

internal sealed class MockPricingSource(PricingSnapshot snapshot) : IPricingSource
{
    public Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task<PricingSnapshot> FetchPricesAsync(string league, CancellationToken ct)
    {
        return Task.FromResult(snapshot);
    }
}

internal sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T> where T : class
{
    public T CurrentValue => currentValue;
    public T Get(string? name)
    {
        return currentValue;
    }

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        return null;
    }
}
