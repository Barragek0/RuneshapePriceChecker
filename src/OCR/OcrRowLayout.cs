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
        int rowGapHeight,
        int rowLateOffsetStartRow,
        int rowLateOffsetStepRows,
        int rowLateOffsetStepPx,
        IReadOnlyCollection<int>? adaptiveShiftStartRows,
        int adaptiveShiftPx)
    {
        rowCount = Math.Max(1, rowCount);
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        adaptiveShiftPx = Math.Max(0, adaptiveShiftPx);

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
        var lateOffsetStartRow = Math.Max(1, rowLateOffsetStartRow);
        var lateOffsetStepRows = Math.Max(1, rowLateOffsetStepRows);
        var lateOffsetStepPx = Math.Max(0, rowLateOffsetStepPx);
        var adaptiveRows = adaptiveShiftStartRows?
            .Where(row => row > 0)
            .Distinct()
            .OrderBy(row => row)
            .ToArray() ?? [];
        var fixedRows = new List<Rectangle>(rowCount);

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var rowNumber = rowIndex + 1;
            var y = startY + (rowIndex * (textHeight + gapHeight));

            if (adaptiveShiftPx > 0 && adaptiveRows.Length > 0)
            {
                var dynamicStepCount = 0;
                for (var i = 0; i < adaptiveRows.Length; i++)
                {
                    if (adaptiveRows[i] <= rowNumber)
                    {
                        dynamicStepCount++;
                    }
                }

                y += dynamicStepCount * adaptiveShiftPx;
            }

            if (lateOffsetStepPx > 0 && rowNumber >= lateOffsetStartRow)
            {
                var stepIndex = ((rowNumber - lateOffsetStartRow) / lateOffsetStepRows) + 1;
                y += stepIndex * lateOffsetStepPx;
            }

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
        }

        return fixedRows.Count > 0
            ? fixedRows
            : [new Rectangle(0, 0, width, height)];
    }
}