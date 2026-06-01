namespace RuneshapePriceChecker.Contracts;

public interface IPricingCache
{
    PriceQuote? TryGetPriceQuote(string itemName);

    PriceQuote? TryGetPriceQuote(string itemName, int quantity);

    Task RefreshAsync(CancellationToken cancellationToken);
}
