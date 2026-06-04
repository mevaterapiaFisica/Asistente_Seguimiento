using System.Text.Json;
using Meva.Rt.Application;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Storage;

public sealed class WeeklyStatsStore : IWeeklyStatsStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public WeeklyStatsStore(string baseDirectory)
    {
        _filePath = Path.Combine(baseDirectory, "weekly_stats.json");
    }

    public async Task AccumulateAsync(IEnumerable<StageTransitionEvent> newEvents)
    {
        var existing = (await LoadAsync()).ToList();
        var index = existing
            .Select((row, i) => (row, i))
            .ToDictionary(
                x => (x.row.WeekStart, x.row.CenterName, x.row.StageCode, x.row.TreatmentTechnique),
                x => x.i);

        foreach (var e in newEvents)
        {
            var weekStart = MondayOf(e.StageEndDate);
            var key = (weekStart, e.CenterName, e.StageCode, e.TreatmentTechnique);

            if (index.TryGetValue(key, out var idx))
            {
                var row = existing[idx];
                row.Count++;
                row.SumDays += e.DaysInStage;
                row.SumDaysSquared += (double)e.DaysInStage * e.DaysInStage;
            }
            else
            {
                index[key] = existing.Count;
                existing.Add(new WeeklyStageStats
                {
                    WeekStart = weekStart,
                    CenterName = e.CenterName,
                    StageCode = e.StageCode,
                    TreatmentTechnique = e.TreatmentTechnique,
                    Count = 1,
                    SumDays = e.DaysInStage,
                    SumDaysSquared = (double)e.DaysInStage * e.DaysInStage
                });
            }
        }

        await SaveAsync(existing);
    }

    public async Task<IReadOnlyList<WeeklyStageStats>> LoadAsync()
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<WeeklyStageStats>>(stream, _jsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task SaveAsync(List<WeeklyStageStats> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
            await JsonSerializer.SerializeAsync(stream, rows, _jsonOptions);
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    private static DateOnly MondayOf(DateOnly date)
    {
        var dow = (int)date.DayOfWeek; // 0=Sun, 1=Mon … 6=Sat
        var daysBack = dow == 0 ? 6 : dow - 1;
        return date.AddDays(-daysBack);
    }
}
