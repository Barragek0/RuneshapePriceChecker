namespace RuneshapePriceChecker.Contracts;

public sealed record LeagueWindowSnapshot(IReadOnlyList<string> ItemNames, DateTimeOffset CapturedAtUtc, IReadOnlyList<int>? RowYPositions = null, bool InterfaceDetected = true, string? BannerMessage = null, string? CaptureMethod = null);
