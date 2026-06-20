namespace RuneshapePriceChecker.Contracts;

public interface ILeagueWindowReader
{
    LeagueWindowSnapshot ReadSnapshot();

    void Warmup();

}

