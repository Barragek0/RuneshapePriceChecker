using System.ComponentModel.DataAnnotations;

namespace RuneshapePriceChecker.OCR;

public sealed class OcrOptions
{
    public string TesseractDataPath { get; set; } = string.Empty;
    [Required]
    public string Language { get; set; } = "eng";
    public bool UseWindowClientCapture { get; set; } = true;
    public bool EnableImagePreprocessing { get; set; } = true;
    [Range(0, 255)]
    public int BinarizationThreshold { get; set; } = 145;
    public bool EnableTextColorFiltering { get; set; } = true;
    [Range(0, 255)]
    public int TextColorTargetR { get; set; } = 50;
    [Range(0, 255)]
    public int TextColorTargetG { get; set; } = 42;
    [Range(0, 255)]
    public int TextColorTargetB { get; set; } = 34;
    [Range(0, 255)]
    public int TextColorTolerance { get; set; } = 52;
    [Range(0, 255)]
    public int TextColorMaxLuminance { get; set; } = 145;
    [Range(0, 255)]
    public int TextColorMaxChannelSpread { get; set; } = 34;
    public bool SaveDebugImages { get; set; }
    [Range(1, 30)]
    public int DebugImageIntervalSeconds { get; set; } = 15;
    public string DebugImageDirectory { get; set; } = string.Empty;
    public bool DebugOverlay { get; set; }
    public bool HideDebugOverlayWhenInterfaceNotDetected { get; set; }
    public bool ShowPricingOverlay { get; set; } = true;
    public bool ShowBanner { get; set; } = true;
}
