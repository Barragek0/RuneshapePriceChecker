namespace RuneshapePriceChecker.OCR;

public interface IAdaptiveRowShiftState
{
    AdaptiveRowShiftSnapshot GetSnapshot();

    void Update(IReadOnlyCollection<int> shiftStartRows, int shiftPx, bool isActive);
}

public sealed record AdaptiveRowShiftSnapshot(IReadOnlyCollection<int> ShiftStartRows, int ShiftPx, bool IsActive);

public sealed class AdaptiveRowShiftState : IAdaptiveRowShiftState
{
    private readonly object _sync = new();
    private HashSet<int> _shiftStartRows = [];
    private int _shiftPx;
    private bool _isActive;

    public AdaptiveRowShiftSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new AdaptiveRowShiftSnapshot(_shiftStartRows.ToArray(), _shiftPx, _isActive);
        }
    }

    public void Update(IReadOnlyCollection<int> shiftStartRows, int shiftPx, bool isActive)
    {
        lock (_sync)
        {
            _shiftStartRows = shiftStartRows is null
                ? []
                : shiftStartRows.Where(row => row > 0).Distinct().OrderBy(row => row).ToHashSet();
            _shiftPx = Math.Max(0, shiftPx);
            _isActive = isActive && _shiftStartRows.Count > 0 && _shiftPx > 0;
        }
    }
}
