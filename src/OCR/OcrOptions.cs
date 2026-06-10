namespace RuneshapePriceChecker.OCR;

public sealed class OcrOptions
{
    public string TesseractDataPath { get; set; } = string.Empty;
    public string Language { get; set; } = "eng";
    public bool UseWindowClientCapture { get; set; } = true;
    public bool EnableImagePreprocessing { get; set; } = true;
    public int BinarizationThreshold { get; set; } = 145;
    public bool EnableTextColorFiltering { get; set; } = true;
    public int TextColorTargetR { get; set; } = 50;
    public int TextColorTargetG { get; set; } = 42;
    public int TextColorTargetB { get; set; } = 34;
    public int TextColorTolerance { get; set; } = 52;
    public int TextColorMaxLuminance { get; set; } = 145;
    public int TextColorMaxChannelSpread { get; set; } = 34;
    public bool SaveDebugImages { get; set; } = false;
    public int DebugImageIntervalSeconds { get; set; } = 15;
    public string DebugImageDirectory { get; set; } = string.Empty;
    public bool DebugOverlay { get; set; } = false;
    public bool HideDebugOverlayWhenInterfaceNotDetected { get; set; } = false;
    public bool ShowPricingOverlay { get; set; } = true;
    public bool ShowBanner { get; set; } = true;
}
