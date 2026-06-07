namespace RuneshapePriceChecker.OCR;

public sealed class OcrOptions
{
    public string TesseractDataPath { get; set; } = string.Empty;

    public string Language { get; set; } = "eng";

    public int PageSegmentationMode { get; set; } = 6;

    public int CommandTimeoutSeconds { get; set; } = 5;

    public int ResolutionPollIntervalSeconds { get; set; } = 1;

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

    public int CaptureBoundsOverlayIntervalMs { get; set; } = 250;

    public int RowUpscaleFactor { get; set; } = 2;

    public int LeaguePanelAnchorSampleX { get; set; } = 0;

    public int LeaguePanelAnchorSampleY { get; set; } = 0;

    public float LeaguePanelAnchorFractionX { get; set; } = 0f;

    public float LeaguePanelAnchorFractionY { get; set; } = 0.023f;

    public int LeaguePanelAnchorSampleRadiusPx { get; set; } = 5;

    public int LeaguePanelAnchorSampleRadiusYPx { get; set; } = 15;

    public float LeaguePanelAnchorSampleRadiusFraction { get; set; } = 0f;

    public float LeaguePanelAnchorSampleRadiusYFraction { get; set; } = 0f;

    public int LeaguePanelAnchorTargetR { get; set; } = 193;

    public int LeaguePanelAnchorTargetG { get; set; } = 183;

    public int LeaguePanelAnchorTargetB { get; set; } = 165;

    public int LeaguePanelAnchorTolerance { get; set; } = 12;

    public int LeaguePanelAnchorMinLuminance { get; set; } = 195;

    public int LeaguePanelAnchorMaxChannelSpread { get; set; } = 15;
}
