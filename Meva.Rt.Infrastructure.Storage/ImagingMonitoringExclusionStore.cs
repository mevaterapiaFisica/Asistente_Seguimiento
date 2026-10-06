using System.Text.Json;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Storage;

public sealed class ImagingMonitoringExclusionStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public ImagingMonitoringExclusionStore(string baseDirectory)
    {
        _filePath = Path.Combine(baseDirectory, "imaging_monitoring_exclusions.json");
    }

    public async Task<IReadOnlyList<ImagingMonitoringExclusion>> LoadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<ImagingMonitoringExclusion>>(stream, _jsonOptions, ct) ?? [];
        }
        catch { return []; }
    }

    public async Task AddAsync(ImagingMonitoringExclusion item, CancellationToken ct)
    {
        var all = (await LoadAllAsync(ct)).ToList();
        all.RemoveAll(e => e.PatientId == item.PatientId);
        all.Add(item);
        await SaveAsync(all, ct);
    }

    public async Task RemoveAsync(string patientId, CancellationToken ct)
    {
        var all = (await LoadAllAsync(ct)).ToList();
        all.RemoveAll(e => e.PatientId == patientId);
        await SaveAsync(all, ct);
    }

    private async Task SaveAsync(List<ImagingMonitoringExclusion> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
            await JsonSerializer.SerializeAsync(stream, items, _jsonOptions, ct);
        File.Move(tmpPath, _filePath, overwrite: true);
    }
}
