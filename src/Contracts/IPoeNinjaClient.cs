namespace RuneshapePriceChecker.Contracts;

public interface IPoeNinjaClient
{
    Task<PoeNinjaPricingSnapshot> FetchCurrentPricesAsync(CancellationToken cancellationToken);
}
