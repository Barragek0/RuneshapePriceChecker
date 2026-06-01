namespace RuneshapePriceChecker.Contracts;

public interface ILeagueWindowReader
{
    LeagueWindowSnapshot ReadSnapshot();
}
