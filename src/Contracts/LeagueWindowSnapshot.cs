namespace RuneshapePriceChecker.Contracts;

public sealed record LeagueWindowSnapshot(
    IReadOnlyList<string> ItemNames,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<int>? RowYPositions = null,
    bool InterfaceDetected = true,
    string? CaptureMethod = null,
    Rectangle? CropBounds = null,
    IReadOnlyList<Rectangle>? RetryRegions = null,
    IReadOnlyList<Rectangle>? RejectedRegions = null);
