namespace Meva.Rt.Application;

public sealed class BusinessDayCalculator
{
    private readonly HashSet<DateOnly> _holidays;

    public BusinessDayCalculator(IEnumerable<DateOnly>? holidays = null)
    {
        _holidays = new HashSet<DateOnly>(holidays ?? []);
    }

    public static BusinessDayCalculator FromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new BusinessDayCalculator();

        var holidays = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => DateOnly.TryParse(l, out var d) ? (DateOnly?)d : null)
            .Where(d => d.HasValue)
            .Select(d => d!.Value);

        return new BusinessDayCalculator(holidays);
    }

    public bool IsBusinessDay(DateOnly date)
        => date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday
           && !_holidays.Contains(date);

    public IReadOnlyList<DateOnly> GetUpcomingBusinessDays(DateOnly from, int count)
    {
        var result = new List<DateOnly>(count);
        var current = from.AddDays(1);
        while (result.Count < count)
        {
            if (IsBusinessDay(current))
                result.Add(current);
            current = current.AddDays(1);
        }
        return result;
    }

    public DateOnly AddBusinessDays(DateOnly from, int businessDays)
    {
        var remaining = Math.Max(businessDays, 1);
        var current = from;
        while (remaining > 0)
        {
            current = current.AddDays(1);
            if (IsBusinessDay(current))
                remaining--;
        }
        return current;
    }

    public int CountBusinessDays(DateOnly from, DateOnly to)
    {
        if (to <= from) return 0;
        var count = 0;
        var current = from.AddDays(1);
        while (current <= to)
        {
            if (IsBusinessDay(current)) count++;
            current = current.AddDays(1);
        }
        return count;
    }

    public DateOnly SubtractBusinessDays(DateOnly from, int businessDays)
    {
        var remaining = Math.Max(businessDays, 1);
        var current = from;
        while (remaining > 0)
        {
            current = current.AddDays(-1);
            if (IsBusinessDay(current))
                remaining--;
        }
        return current;
    }

    public bool HasHolidaysForYear(int year)
        => _holidays.Any(d => d.Year == year);
}
