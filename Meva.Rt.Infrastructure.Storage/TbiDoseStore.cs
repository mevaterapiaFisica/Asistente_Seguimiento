using System.Text.Json;
using Meva.Rt.Core;

namespace Meva.Rt.Infrastructure.Storage;

public sealed class TbiDoseStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public TbiDoseStore(string baseDirectory)
    {
        _filePath = Path.Combine(baseDirectory, "tbi_dose_info.json");
    }

    public async Task<IReadOnlyList<TbiDoseInfo>> LoadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<TbiDoseInfo>>(stream, _jsonOptions, ct) ?? [];
        }
        catch { return []; }
    }

    public async Task UpsertAsync(TbiDoseInfo info, CancellationToken ct)
    {
        var all = (await LoadAllAsync(ct)).ToList();
        var idx = all.FindIndex(i => i.PatientId == info.PatientId);
        if (idx >= 0) all[idx] = info;
        else all.Add(info);
        await SaveAsync(all, ct);
    }

    private async Task SaveAsync(List<TbiDoseInfo> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
            await JsonSerializer.SerializeAsync(stream, items, _jsonOptions, ct);
        File.Move(tmpPath, _filePath, overwrite: true);
    }
}
