using System.Drawing;

namespace RuneshapePriceChecker.OCR;

internal static class OcrRowLayout
{
    public static IReadOnlyList<Rectangle> BuildRowRectangles(
        int width,
        int height,
        int rowCount,
        bool useFixedRowGeometry,
        int rowStartOffsetY,
        int rowTextHeight,
        int rowGapHeight)
    {
        rowCount = Math.Max(1, rowCount);
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (!useFixedRowGeometry)
        {
            var equalRows = new List<Rectangle>(rowCount);
            var previousY = 0;
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                var nextY = (int)Math.Round((rowIndex + 1) * (height / (double)rowCount));
                var currentHeight = Math.Max(1, nextY - previousY);
                if (previousY + currentHeight > height)
                {
                    currentHeight = height - previousY;
                }

                if (currentHeight <= 0)
                {
                    break;
                }

                equalRows.Add(new Rectangle(0, previousY, width, currentHeight));
                previousY = nextY;
            }

            return equalRows.Count > 0
                ? equalRows
                : [new Rectangle(0, 0, width, height)];
        }

        var startY = Math.Max(0, rowStartOffsetY);
        var textHeight = Math.Max(1, rowTextHeight);
        var gapHeight = Math.Max(0, rowGapHeight);
        var fixedRows = new List<Rectangle>(rowCount);

        var y = startY;
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (y >= height)
            {
                break;
            }

            var currentHeight = Math.Min(textHeight, height - y);
            if (currentHeight <= 0)
            {
                break;
            }

            fixedRows.Add(new Rectangle(0, y, width, currentHeight));
            y += textHeight + gapHeight;
        }

        return fixedRows.Count > 0
            ? fixedRows
            : [new Rectangle(0, 0, width, height)];
    }
}