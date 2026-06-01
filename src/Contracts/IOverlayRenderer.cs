namespace RuneshapePriceChecker.Contracts;

public interface IOverlayRenderer
{
    void Render(LeagueWindowSnapshot snapshot, IReadOnlyDictionary<string, PriceQuote?> pricesByItemName);
}
