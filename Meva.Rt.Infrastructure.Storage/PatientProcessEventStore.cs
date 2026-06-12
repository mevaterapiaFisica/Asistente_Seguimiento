using System.Text.Json;
using Meva.Rt.Application;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Storage;

public sealed class PatientProcessEventStore : IPatientProcessEventStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public PatientProcessEventStore(string baseDirectory)
    {
        _filePath = Path.Combine(baseDirectory, "patient_process_events.json");
    }

    public async Task AppendAsync(IEnumerable<PatientProcessEvent> events, CancellationToken ct)
    {
        var existing = await LoadAsync(ct);
        var all = existing.Concat(events).ToList();
        await SaveAsync(all, ct);
    }

    public async Task<IReadOnlyList<PatientProcessEvent>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<PatientProcessEvent>>(stream, _jsonOptions, ct) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<PatientProcessEvent>> LoadRecentAsync(int days, CancellationToken ct)
    {
        var all = await LoadAsync(ct);
        var cutoff = DateTime.UtcNow.AddDays(-days);
        return all.Where(e => e.DetectedAtUtc >= cutoff).ToList();
    }

    private async Task SaveAsync(List<PatientProcessEvent> events, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
            await JsonSerializer.SerializeAsync(stream, events, _jsonOptions, ct);
        File.Move(tmpPath, _filePath, overwrite: true);
    }
}
