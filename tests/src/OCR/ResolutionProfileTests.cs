using Xunit;
namespace RuneshapePriceChecker.Tests.OCR;

public class ResolutionProfileTests
{
    private const double AnchorFractionY = 0.023;
    private const int AnchorSampleRadiusPx = 5;
    private const int AnchorSampleRadiusYPx = 10;

    public record Profile(string Key, int CaptureW, int CaptureH);

#pragma warning disable CA1825 // TheoryData<T> is List<T>-derived; collection expression is correct here
    public static readonly TheoryData<Profile> AllProfiles =
    [
        new("1600x900", 240, 450),
        new("1920x1080", 288, 540),
        new("2560x1440", 418, 720),
        new("3440x1440", 390, 725),
        new("3840x2160", 680, 1080),
    ];
#pragma warning restore CA1825

    private static (int leftX, int rightX, int sampleY, int radiusX, int radiusY, int leftMin, int leftMax, int rightMin, int rightMax, int minY, int maxY) ComputeAnchors(int w, int h)
    {
        var leftX = Math.Max(0, Math.Min(w - 1, 0));
        var rightX = w - 1 - leftX;
        var sampleY = (int)(h * Math.Max(0, Math.Min(1, AnchorFractionY)));
        var radiusX = Math.Max(2, Math.Min(20, AnchorSampleRadiusPx));
        var radiusY = Math.Max(2, Math.Min(20, AnchorSampleRadiusYPx));
        var leftMin = Math.Max(0, leftX - radiusX);
        var leftMax = Math.Min(w - 1, leftX + radiusX);
        var rightMin = Math.Max(0, rightX - radiusX);
        var rightMax = Math.Min(w - 1, rightX + radiusX);
        var minY = Math.Max(0, sampleY - radiusY);
        var maxY = Math.Min(h - 1, sampleY + radiusY);
        return (leftX, rightX, sampleY, radiusX, radiusY, leftMin, leftMax, rightMin, rightMax, minY, maxY);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void LeftAnchor_AtLeftEdge(Profile p)
    {
        var (leftX, _, _, _, _, _, _, _, _, _, _) = ComputeAnchors(p.CaptureW, p.CaptureH);
        Assert.Equal(0, leftX);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RightAnchor_AtRightEdge(Profile p)
    {
        var (_, rightX, _, _, _, _, _, _, _, _, _) = ComputeAnchors(p.CaptureW, p.CaptureH);
        Assert.Equal(p.CaptureW - 1, rightX);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void AnchorY_WithinTop5Percent(Profile p)
    {
        var (_, _, sampleY, _, _, _, _, _, _, _, _) = ComputeAnchors(p.CaptureW, p.CaptureH);
        Assert.True(sampleY <= (int)(p.CaptureH * 0.05));
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Radius_BothInRange(Profile p)
    {
        var (_, _, _, radiusX, radiusY, _, _, _, _, _, _) = ComputeAnchors(p.CaptureW, p.CaptureH);
        Assert.InRange(radiusX, 2, 20);
        Assert.InRange(radiusY, 2, 20);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SearchRegions_WithinBitmap(Profile p)
    {
        var (_, _, _, _, _, leftMin, leftMax, rightMin, rightMax, minY, maxY) = ComputeAnchors(p.CaptureW, p.CaptureH);
        Assert.True(leftMin >= 0 && leftMax < p.CaptureW, $"Left region [{leftMin},{leftMax}] outside [0,{p.CaptureW - 1}]");
        Assert.True(rightMin >= 0 && rightMax < p.CaptureW, $"Right region [{rightMin},{rightMax}] outside [0,{p.CaptureW - 1}]");
        Assert.True(minY >= 0 && maxY < p.CaptureH, $"Y region [{minY},{maxY}] outside [0,{p.CaptureH - 1}]");
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SearchArea_AtLeast50Pixels(Profile p)
    {
        var (_, _, _, _, _, leftMin, leftMax, _, _, minY, maxY) = ComputeAnchors(p.CaptureW, p.CaptureH);
        var area = (leftMax - leftMin + 1) * (maxY - minY + 1);
        Assert.True(area >= 50, $"Area {area} too small");
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Anchors_Symmetric(Profile p)
    {
        var (leftX, rightX, _, _, _, _, _, _, _, _, _) = ComputeAnchors(p.CaptureW, p.CaptureH);
        Assert.Equal(leftX, (p.CaptureW - 1) - rightX);
    }
}
