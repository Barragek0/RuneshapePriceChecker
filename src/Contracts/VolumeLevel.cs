namespace RuneshapePriceChecker.Contracts;

public enum VolumeLevel
{
    Normal = 0,
    Low = 1,     // CurrentQuantity < 100 — yellow warning
    VeryLow = 2  // CurrentQuantity < 10 — red warning
}
