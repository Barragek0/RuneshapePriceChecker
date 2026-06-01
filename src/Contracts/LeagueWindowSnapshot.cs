namespace RuneshapePriceChecker.Contracts;

public sealed record LeagueWindowSnapshot(IReadOnlyList<string> ItemNames, DateTimeOffset CapturedAtUtc);
